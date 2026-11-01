using System.IO.Compression;
using System.Text.Json;
using CorgiBlackboard.App.Diagnostics;
using CorgiBlackboard.Core.Coordinates;
using CorgiBlackboard.Core.Diagnostics;
using CorgiBlackboard.Core.Documents;
using CorgiBlackboard.Core.Input;
using CorgiBlackboard.Core.Model;
using CorgiBlackboard.Infrastructure.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace CorgiBlackboard.Infrastructure.Tests;

public sealed class WorkspaceDiagnosticsTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Export_contains_content_and_chrome_samples_with_actual_interruption_state()
    {
        // Given the production bridge attached to the session exported by the app.
        using var temp = new DiagnosticTestDirectory(output);
        await using var session = new DiagnosticSession(new DiagnosticOptions { RootDirectory = temp.Path });
        var workspace = new Workspace();
        var input = new InputArbiter();
        using var bridge = new WorkspaceDiagnostics(workspace, input, session);
        var down = new PointerSample(7, 11, 123, new ScreenPoint(120, 90), PointerDeviceKind.Touch,
            PointerAction.Down, InputSurfaceKind.Content, workspace.BoardSurfaceId, new ViewportTransform(2, 40, 30));

        // When content and chrome use their shared entry and focus interrupts the contacts.
        input.Process(down);
        input.Process(down with { PointerId = 8, Sequence = 12, SurfaceKind = InputSurfaceKind.Chrome, SurfaceId = null });
        input.LostFocus();
        var records = await Export(session, temp.Path);

        // Then the archive describes those inputs and the resulting cancellation, not driver records.
        var samples = records.Where(record => record.Name == "InputSample").ToArray();
        Assert.Equal(2, samples.Length);
        var content = samples[0];
        Assert.Equal(workspace.BoardSurfaceId.Value.ToString("D"), content.SurfaceId);
        Assert.Equal(new DiagnosticPointerSample("Touch:7", "Down", 120, 90, 123),
            Assert.Single(content.PointerSamples!));
        Assert.Equal("11", content.Data!["sourceSequence"]);
        Assert.Equal("2", content.Data["scale"]);
        Assert.Equal("40", content.Data["offsetX"]);
        Assert.Equal("30", content.Data["offsetY"]);
        Assert.Equal("Idle", content.Data["before"]);
        Assert.Equal("Editing", content.Data["after"]);
        Assert.Equal("Chrome", samples[1].Data!["surfaceKind"]);
        Assert.Equal("Touch:8", Assert.Single(samples[1].PointerSamples!).DeviceId);
        var cancellation = Assert.Single(records, record => record.Name == "InputTransition");
        Assert.Equal("SecondContact", cancellation.Data!["reason"]);
        Assert.Equal("Cancel", cancellation.Data["after"]);
        var interruption = Assert.Single(records, record => record.Name == "InputInterrupted");
        Assert.Equal("FocusLost", interruption.Data!["reason"]);
        Assert.Equal("AwaitAllReleased", interruption.Data["after"]);
        Assert.Equal("2", interruption.Data["contactsAfter"]);
        Assert.All(records, record => Assert.Equal(session.Id, record.SessionId));
    }

    [Fact]
    public async Task Export_contains_applied_content_reading_layout_and_camera_activity()
    {
        // Given real workspace models and editing, with no WPF object or injected diagnostic events.
        using var temp = new DiagnosticTestDirectory(output);
        await using var session = new DiagnosticSession(new DiagnosticOptions { RootDirectory = temp.Path });
        var workspace = new Workspace();
        var input = new InputArbiter();
        _ = new EditingSession(workspace, input, new ToolSettings(InputTool.Ink, 0xFFFFFFFF, 4, 24));
        var asset = new Asset(AssetId.New(), "image/png", 1);
        workspace.AddAsset(asset);
        var document = workspace.AddImageDocument(asset.Id, 800, 2400);
        workspace.Layout.SetTeachingArea(400, 348);
        workspace.Layout.Open(document.Id, new WindowRectangle(0, 0, 320, 300));
        using var bridge = new WorkspaceDiagnostics(workspace, input, session);
        var down = new PointerSample(3, 1, 10, new ScreenPoint(20, 30), PointerDeviceKind.Mouse,
            PointerAction.Down, InputSurfaceKind.Content, workspace.BoardSurfaceId, new ViewportTransform(1, 0, 0));

        // When the actual editing and document models change.
        input.Process(down);
        input.Process(down with { Sequence = 2, Timestamp = 11, Action = PointerAction.Up, Position = new ScreenPoint(80, 30) });
        workspace.Reading.SetOffset(document.PageSurfaceIds[0], 0, 600);
        workspace.Layout.EnterFill(document.Id);
        workspace.Camera.Pan(12, -48);
        var records = await Export(session, temp.Path);

        // Then the exported identities and values match the applied model activity.
        var stroke = Assert.Single(workspace.GetElements(workspace.BoardSurfaceId));
        var content = Assert.Single(records, record => record.Name == "ContentApplied");
        Assert.Equal(workspace.BoardSurfaceId.Value.ToString("D"), content.SurfaceId);
        Assert.True(Guid.TryParse(content.OperationId, out var operationId) && operationId != Guid.Empty);
        Assert.Equal(stroke.Id.Value.ToString(), content.Data!["afterIds"]);
        Assert.Equal("", content.Data["beforeIds"]);
        Assert.Equal("1", content.Data["contentRevision"]);
        var commit = Assert.Single(records, record => record.Name == "InputTransition");
        Assert.Equal("Commit", commit.Data!["after"]);
        var reading = Assert.Single(records, record => record.Name == "ReadingChanged");
        Assert.Equal(document.PageSurfaceIds[0].Value.ToString("D"), reading.SurfaceId);
        Assert.Equal("600", reading.Data!["y"]);
        var layout = Assert.Single(records, record => record.Name == "WindowLayoutChanged");
        Assert.Equal(document.Id.Value.ToString("D"), layout.WindowId);
        Assert.Equal("FillEntered", layout.Data!["action"]);
        Assert.Equal("True", layout.Data["filled"]);
        var camera = Assert.Single(records, record => record.Name == "BoardCameraChanged");
        Assert.Equal("12", camera.Data!["x"]);
        Assert.Equal("-48", camera.Data["y"]);
    }

    [Fact]
    public async Task Disposed_bridge_unsubscribes_every_source_without_closing_the_app_session()
    {
        // Given one App-owned session whose window subscription has been disposed.
        using var temp = new DiagnosticTestDirectory(output);
        await using var session = new DiagnosticSession(new DiagnosticOptions { RootDirectory = temp.Path });
        var workspace = new Workspace();
        var input = new InputArbiter();
        _ = new EditingSession(workspace, input, new ToolSettings(InputTool.Ink, 0xFFFFFFFF, 4, 24));
        var asset = new Asset(AssetId.New(), "image/png", 1);
        workspace.AddAsset(asset);
        var document = workspace.AddImageDocument(asset.Id, 800, 2400);
        workspace.Layout.SetTeachingArea(400, 348);
        workspace.Layout.Open(document.Id, new WindowRectangle(0, 0, 320, 300));
        var bridge = new WorkspaceDiagnostics(workspace, input, session);
        bridge.Dispose();
        var sequence = session.Sequence;
        var down = new PointerSample(1, 1, 1, new ScreenPoint(20, 30), PointerDeviceKind.Mouse,
            PointerAction.Down, InputSurfaceKind.Content, workspace.BoardSurfaceId, new ViewportTransform(1, 0, 0));

        // When each previously subscribed source changes and the existing session exports.
        input.Process(down);
        input.Process(down with { Action = PointerAction.Up, Position = new ScreenPoint(80, 30) });
        input.LostFocus();
        workspace.Reading.SetOffset(document.PageSurfaceIds[0], 0, 600);
        workspace.Layout.EnterFill(document.Id);
        workspace.Camera.Pan(12, -48);
        Assert.Equal(sequence, session.Sequence);
        var records = await Export(session, temp.Path);

        // Then only the export barrier appears; disposal neither leaked handlers nor closed the session.
        Assert.Equal("flush", Assert.Single(records).Name);
    }

    private static async Task<DiagnosticSessionRecord[]> Export(DiagnosticSession session, string root)
    {
        var destination = Path.Combine(root, "feedback.zip");
        var result = await new FeedbackExporter().ExportAsync(new FeedbackExportRequest(destination, [session.DirectoryPath]));
        Assert.False(result.Incomplete);
        using var archive = ZipFile.OpenRead(destination);
        using var manifestReader = new StreamReader(archive.GetEntry("manifest.json")!.Open());
        using var manifest = JsonDocument.Parse(await manifestReader.ReadToEndAsync());
        var exportedSession = Assert.Single(manifest.RootElement.GetProperty("sessions").EnumerateArray());
        Assert.Equal(session.Id, exportedSession.GetProperty("sessionId").GetGuid());
        var records = new List<DiagnosticSessionRecord>();
        var lines = new List<string>();
        foreach (var file in exportedSession.GetProperty("files").EnumerateArray())
        {
            var entry = Assert.IsType<ZipArchiveEntry>(archive.GetEntry($"sessions/{session.Id:N}/{file.GetProperty("name").GetString()}"));
            Assert.Equal(file.GetProperty("cutoffBytes").GetInt64(), entry.Length);
            using var reader = new StreamReader(entry.Open());
            while (await reader.ReadLineAsync() is { } line)
            {
                lines.Add(line);
                var record = JsonSerializer.Deserialize<DiagnosticSessionRecord>(line,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                records.Add(Assert.IsType<DiagnosticSessionRecord>(record));
            }
        }
        using var combinedReader = new StreamReader(archive.GetEntry($"sessions/{session.Id:N}/events.jsonl")!.Open());
        Assert.Equal(string.Concat(lines.Select(line => line + "\n")), await combinedReader.ReadToEndAsync());
        Assert.Equal(Enumerable.Range(1, records.Count).Select(value => (long)value), records.Select(record => record.Sequence));
        Assert.Equal(exportedSession.GetProperty("cutoffSequence").GetInt64(), records[^1].Sequence);
        return records.ToArray();
    }
}
