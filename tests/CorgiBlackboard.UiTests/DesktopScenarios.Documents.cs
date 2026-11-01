using System.IO.Compression;
using System.Drawing;
using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using CorgiBlackboard.TestFixtures;
using static CorgiBlackboard.UiTests.QaContract;

namespace CorgiBlackboard.UiTests;

internal static partial class DesktopScenarios
{
    private static JsonArray Documents(JsonNode state) => state["documents"]!.AsArray();
    private static JsonObject Document(JsonNode state, string id) =>
        Documents(state).OfType<JsonObject>().Single(document => document["id"]!.GetValue<string>() == id);
    private static string Id(JsonNode document, string suffix) => $"Document.{document["id"]!.GetValue<string>()}.{suffix}";

    private static string OpenImage(NativeDesktopSession app, string name)
    {
        var fixtures = Path.Combine(app.Temporary, "images");
        if (!Directory.Exists(fixtures)) ImageFixtureFactory.Create(fixtures);
        var path = Path.Combine(fixtures, name);
        var before = Documents(app.Snapshot).Select(value => value!["id"]!.GetValue<string>()).ToHashSet();
        AutomationElement? dialog = null;
        app.Structure(() => app.Click("OpenImage"), () =>
        {
            dialog = app.Window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Window));
            return dialog is not null && dialog.FindFirstDescendant(cf => cf.ByAutomationId("1148")) is not null;
        });
        // Standard Windows common file dialog: filename combo/edit ID 1148 and Open ID 1.
        var filename = app.Find("1148", dialog);
        var edit = filename.Patterns.Value.IsSupported ? filename : filename.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit));
        Require(edit is not null, "Native file dialog exposes no filename ValuePattern.");
        app.SetValue(edit!, path);
        app.Act(() => app.Click(app.Find("1", dialog)),
            state => Documents(state).Count == before.Count + 1);
        var added = Documents(app.Snapshot).Single(value => !before.Contains(value!["id"]!.GetValue<string>()))!;
        Require(added["assetId"]!.GetValue<string>() != Guid.Empty.ToString(), "Missing imported asset identity.");
        return added["id"]!.GetValue<string>();
    }

    private static void Restore(NativeDesktopSession app, string id)
    {
        AutomationElement? restore = null;
        app.Structure(() => app.Click("MinimizedDocuments"), () =>
        {
            restore = app.Automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId($"Document.{id}.Restore"));
            return restore is not null;
        });
        app.Act(() => app.Click(restore!), state => !Flag(Document(state, id), "minimized"));
    }

    private static void ImageWindows(NativeDesktopSession app)
    {
        var first = OpenImage(app, "long-grid-800x2400.png");
        var second = OpenImage(app, "wide-grid-2400x800.png");
        Require(first != second && Documents(app.Snapshot).Count == 2, "Multiple images were not independently opened.");
        var old = Document(app.Snapshot, second).DeepClone();
        var title = Id(old, "Title");
        app.Drag(title, 40, 30, state => !JsonNode.DeepEquals(Document(state, second)["rectangle"], old["rectangle"]));
        var moved = Document(app.Snapshot, second).DeepClone();
        app.Drag(Id(moved, "ResizeSE"), 60, 40,
            state => Value(Document(state, second)["rectangle"]!, "Width") > Value(moved["rectangle"]!, "Width"));
        var resized = Document(app.Snapshot, second).DeepClone();
        app.Stage("image-windows");
        app.Act(() => app.Click(Id(resized, "Minimize")), state => Flag(Document(state, second), "minimized"));
        Restore(app, second);
        Require(JsonNode.DeepEquals(resized["rectangle"], Document(app.Snapshot, second)["rectangle"]), "Minimize lost normal rectangle.");
        var before = app.Snapshot;
        app.Wheel("BoardSurface", -1, state => Value(state["camera"]!, "y") == Value(before["camera"]!, "y") - 48);
        Require(JsonNode.DeepEquals(before["documents"], app.Snapshot["documents"]), "Board navigation moved fixed image windows.");
        app.Act(() => app.Click($"Document.{second}.Close"), state => Documents(state).Count == 1);
        Require(Documents(app.Snapshot)[0]!["id"]!.GetValue<string>() == first, "Closing image changed other document identity.");
        app.Act(() => app.Click($"Document.{first}.Close"), state => Documents(state).Count == 0);
    }

    private static void ImageFitRestore(NativeDesktopSession app)
    {
        var window = app.Window.BoundingRectangle;
        app.Resize((int)Math.Round(800 * app.Scale), (int)Math.Round(500 * app.Scale));
        var id = OpenImage(app, "long-grid-800x2400.png");
        var normal = Document(app.Snapshot, id);
        Require(Value(normal, "scale") == 1 && Value(normal, "width") == 800 && Value(normal, "height") == 2400,
            "Normal mode used file metadata DPI instead of one image pixel per DIP.");
        app.Select("Tool.Pen");
        app.Select("Tool.Color.White");
        app.Wheel(Id(normal, "Content"), -12.5, state => Value(Document(state, id), "offsetY") == 600);
        app.Stroke(Id(normal, "Content"), (50, 50), (200, 50));
        app.Pixel(Id(normal, "Content"), 125, 50, Color.White);
        var snapshot = Document(app.Snapshot, id).DeepClone();
        app.Act(() => app.Click(Id(snapshot, "Fill")), state => Flag(Document(state, id), "filled"));
        var filled = Document(app.Snapshot, id);
        Require(Math.Abs(Value(filled, "scale") - Value(filled, "viewportWidth") / 800) < 1e-9 &&
            Value(filled, "offsetX") == 0, "Fill is not width fit.");
        Require(JsonNode.DeepEquals(snapshot["strokes"], filled["strokes"]), "Fit rewrote page-local content.");
        app.Pixel(Id(filled, "Content"), 125 * Value(filled, "scale"),
            (650 - Value(filled, "offsetY")) * Value(filled, "scale"), Color.White);
        var scale = Value(filled, "scale");
        var offset = Value(filled, "offsetY");
        app.Stage("image-filled");
        app.Wheel(Id(filled, "Content"), -(1800 - offset) * scale / 48,
            state => Math.Abs(Value(Document(state, id), "offsetY") - 1800) < 0.75);
        var filledOffset = Value(Document(app.Snapshot, id), "offsetY");
        app.Act(() => app.Click($"Document.{id}.Minimize"), state => Flag(Document(state, id), "minimized"));
        Restore(app, id);
        Require(Flag(Document(app.Snapshot, id), "filled"), "Minimize discarded fill mode.");
        Require(Value(Document(app.Snapshot, id), "offsetY") == filledOffset,
            "Minimize/restore discarded the filled page-local reading offset.");
        app.Act(() => app.Click($"Document.{id}.Fill"), state => !Flag(Document(state, id), "filled"));
        var restored = Document(app.Snapshot, id);
        Require(JsonNode.DeepEquals(snapshot["rectangle"], restored["rectangle"]) &&
            Value(snapshot, "offsetY") == Value(restored, "offsetY") &&
            JsonNode.DeepEquals(snapshot["strokes"], restored["strokes"]), "Normal rectangle/reading/annotation restore failed.");
        app.Stage("image-restored");
        app.Pixel(Id(restored, "Content"), 125, 650 - Value(restored, "offsetY"), Color.White);
        app.Act(() => app.Click($"Document.{id}.Close"), state => Documents(state).Count == 0);
        var small = OpenImage(app, "small-grid-200x100.png");
        app.Act(() => app.Click($"Document.{small}.Fill"), state => Flag(Document(state, small), "filled"));
        Require(Value(Document(app.Snapshot, small), "offsetY") == 0, "Short image acquired negative scroll range.");
        app.Act(() => app.Click($"Document.{small}.Close"), state => Documents(state).Count == 0);
        app.Resize(window.Width, window.Height);
    }

    private static void DiagnosticsExport(NativeDesktopSession app)
    {
        app.Select("Tool.Pen");
        app.Stroke("BoardSurface", (100, 420), (300, 420));
        AutomationElement? dialog = null;
        app.Structure(() => app.Click("Diagnostics.Open"), () =>
        {
            dialog = app.Window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Window));
            return dialog?.FindFirstDescendant(cf => cf.ByAutomationId("Diagnostics.Sessions")) is not null;
        });
        var sessions = app.Find("Diagnostics.Sessions", dialog).AsListBox();
        Require(sessions.Items.Length == 1, "QA logs are not isolated from user sessions.");
        sessions.Items[0].Select();
        var destination = Path.Combine(app.Temporary, "feedback.zip");
        app.SetValue(app.Find("Diagnostics.OutputPath", dialog), destination);
        app.SetValue(app.Find("Diagnostics.Steps", dialog), "Task11 native input");
        app.SetValue(app.Find("Diagnostics.Expected", dialog), "Offline feedback for this executable");
        app.SetValue(app.Find("Diagnostics.Actual", dialog), "Collected on release desktop");
        // File publication is atomic. Subscribe before native Export click; watcher is not polling.
        using var watcher = new FileSystemWatcher(app.Temporary, "feedback.zip") { EnableRaisingEvents = false };
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Created += (_, _) => published.TrySetResult();
        watcher.Renamed += (_, _) => published.TrySetResult();
        watcher.EnableRaisingEvents = true;
        app.Click(app.Find("Diagnostics.Export", dialog));
        published.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        using (var archive = ZipFile.OpenRead(destination))
        {
            Require(archive.GetEntry("manifest.json") is not null, "Feedback lacks manifest.");
            using var reader = new StreamReader(archive.GetEntry("manifest.json")!.Open());
            var manifest = JsonNode.Parse(reader.ReadToEnd())!;
            var exported = manifest["sessions"]!.AsArray().Single()!;
            Require(exported["sessionId"]!.GetValue<string>() == app.Snapshot["sessionId"]!.GetValue<string>(), "Exported wrong session.");
            Require(!Flag(manifest, "incomplete"), "Feedback is incomplete.");
            Require(archive.Entries.Any(entry => entry.FullName.EndsWith(".jsonl", StringComparison.Ordinal)), "No diagnostic events.");
            Require(archive.Entries.All(entry => !entry.FullName.Contains("..") && !entry.FullName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)),
                "Unexpected feedback payload.");
            Require(archive.GetEntry("classroom-checklist.md") is not null && archive.GetEntry("environment.json") is not null,
                "Feedback lacks checklist/environment.");
            var events = archive.Entries.Single(entry => entry.FullName.EndsWith("/events.jsonl", StringComparison.Ordinal));
            using var eventReader = new StreamReader(events.Open());
            long sequence = 0;
            var pointers = false;
            while (eventReader.ReadLine() is { } line)
            {
                var record = JsonNode.Parse(line)!;
                Require(Number(record, "sequence") == ++sequence, "Diagnostic event sequence gap.");
                Require(record["gitHash"]!.GetValue<string>() == app.Snapshot["gitHash"]!.GetValue<string>() &&
                    record["buildVersion"]!.GetValue<string>() == app.Snapshot["version"]!.GetValue<string>(),
                    "Diagnostic event build identity differs from launched executable.");
                pointers |= record["pointerSamples"] is JsonArray { Count: > 0 };
            }
            Require(sequence > 0 && pointers, "Native board action was not recorded as diagnostic pointer samples.");
        }
        app.Stage("diagnostics-export");
        app.CloseDialog(dialog!.AsWindow());
    }
}
