using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using CorgiBlackboard.Core.Input;
using CorgiBlackboard.Core.Diagnostics;
using CorgiBlackboard.Infrastructure.Diagnostics;
using CorgiBlackboard.Infrastructure.Images;
using Xunit;

namespace CorgiBlackboard.UiTests;

public sealed class DiagnosticProducerTests
{
    [Fact]
    public void Tool_selection_records_all_selected_values_when_settings_change()
    {
        // Given settings distinct from the initial ink defaults.
        var settings = new ToolSettings(InputTool.Eraser, 0xFFAABBCC, 12, 48);
        // When production builds the selection record.
        var record = DiagnosticProducers.ToolSelected(settings);
        // Then all machine-consumed settings describe this selection.
        Assert.Equal("Eraser", record.Data!["tool"]);
        Assert.Equal("FFAABBCC", record.Data["inkArgb"]);
        Assert.Equal("12", record.Data["inkWidthDip"]);
        Assert.Equal("48", record.Data["eraserDiameterDip"]);
    }

    [Fact]
    public void Image_record_retains_metadata_hash_and_elapsed_time_when_loaded()
    {
        // Given source bytes and a stream positioned after decoding.
        var bytes = Enumerable.Range(0, 200_000).Select(i => (byte)i).ToArray();
        using var source = new MemoryStream(bytes) { Position = 19 };
        // When the decoder's pure fingerprint seam and production event builder run.
        var record = DiagnosticProducers.ImageLoaded(
            new DiagnosticAsset("grid.png", "image/png", bytes.Length,
                ImageFingerprint.Compute(source, CancellationToken.None)), TimeSpan.FromMilliseconds(125));
        // Then the hash covers the entire file and the event retains safe metadata.
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), record.Asset!.Sha256);
        Assert.Equal("grid.png", record.Asset.DisplayName);
        Assert.Equal("image/png", record.Asset.MediaType);
        Assert.Equal(bytes.Length, record.Asset.Length);
        Assert.Equal(125, record.DurationMilliseconds);
        Assert.Equal(DiagnosticKind.Asset, record.Kind);
    }

    [Fact]
    public void Fingerprinting_cancels_when_request_is_cancelled()
    {
        // Given a cancelled image request.
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var source = new MemoryStream(new byte[128]);
        // When hashing starts, then the worker observes cancellation.
        Assert.Throws<OperationCanceledException>(() => ImageFingerprint.Compute(source, cancellation.Token));
    }

    [Fact]
    public void Failure_retains_type_chain_without_sensitive_exception_fields_when_serialized()
    {
        // Given sensitive messages and custom exception Data.
        var error = new IOException(@"C:\private\lesson.png token=secret",
            new UnauthorizedAccessException("credential-secret"));
        error.Data["bytes"] = new byte[] { 1, 2, 3 };
        // When a handled or unhandled producer creates its record.
        var record = DiagnosticProducers.Failure("ImageLoadFailed", error, TimeSpan.FromMilliseconds(22));
        // Then only the bounded type chain and timing cross the diagnostic boundary.
        Assert.Equal(typeof(IOException).FullName, record.Exception!.Type);
        Assert.Equal(typeof(UnauthorizedAccessException).FullName, record.Exception.Inner!.Type);
        Assert.Equal("", record.Exception.Message);
        Assert.Null(record.Exception.Stack);
        Assert.Equal("", record.Exception.Inner.Message);
        Assert.Null(record.Exception.Inner.Stack);
        Assert.Equal(22, record.DurationMilliseconds);
        Assert.Equal(DiagnosticSeverity.Error, record.Severity);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(record));
    }

    [Fact]
    public void Exception_capture_bounds_chain_when_nested_deeply()
    {
        // Given a deeply nested failure.
        Exception error = new IOException("sensitive");
        for (var i = 0; i < 30; i++) error = new InvalidOperationException("sensitive", error);
        // When the production exception projection runs.
        var node = DiagnosticProducers.Failure("DomainUnhandledException", error, TimeSpan.Zero).Exception;
        // Then at most eight exception types are retained.
        var count = 0;
        while (node is not null) { count++; node = node.Inner; }
        Assert.Equal(8, count);
    }

    [Fact]
    public void Performance_record_contains_numeric_counters_when_sampled()
    {
        // Given a measured heartbeat interval.
        var elapsed = TimeSpan.FromMilliseconds(31_250);
        // When production samples runtime counters.
        var record = DiagnosticProducers.Performance(elapsed);
        // Then the interval and bounded runtime counters are machine-readable.
        Assert.Equal(31_250, record.DurationMilliseconds);
        foreach (var key in new[] { "managedBytes", "gen0Collections", "gen2Collections", "threadPoolThreads" })
            Assert.True(long.Parse(record.Data![key], System.Globalization.CultureInfo.InvariantCulture) >= 0);
    }

    [Fact]
    public void Embedded_checklist_matches_canonical_version_when_session_default_is_read()
    {
        // Given the shipped version's canonical resource.
        var expected = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "canonical-checklist.md"));
        // When the session default resource loader reads it.
        var actual = DiagnosticChecklist.Read();
        // Then resource equality and the scenario IDs survive without pinning prose.
        Assert.Equal(expected, actual);
        var ids = Regex.Matches(actual, @"(?m)^\| (S\d{2}) \|").Select(match => match.Groups[1].Value);
        Assert.Equal(Enumerable.Range(1, 19).Select(i => $"S{i:00}"), ids);
    }
}
