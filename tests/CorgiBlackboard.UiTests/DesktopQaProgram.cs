using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CorgiBlackboard.UiTests;

internal static class DesktopQaProgram
{
    [STAThread]
    public static int Main(string[] args)
    {
        // The assembly can be compiled/test-discovered safely; native work requires this exact opt-in.
        if (args.Length != 5 || args[0] != "--post-release-desktop" || !QaContract.Scenarios.Contains(args[1]))
        {
            Console.Error.WriteLine("Desktop QA is 发布后实际环境待测. Usage: --post-release-desktop Scenario ExePath EvidenceDir ExpectedSourceCommit");
            return 2;
        }
        var scenario = args[1];
        var exe = Path.GetFullPath(args[2]);
        var evidence = Path.GetFullPath(args[3]);
        var source = args[4];
        if (!File.Exists(exe) || source.Length is not (40 or 64) || !source.All(Uri.IsHexDigit) || Directory.Exists(evidence))
        {
            Console.Error.WriteLine("Require an existing executable, its expected 40- or 64-character source commit, and a NEW evidence directory.");
            return 2;
        }
        Directory.CreateDirectory(evidence);
        var temporary = Path.Combine(Path.GetTempPath(), "corgi-desktop-qa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        var errors = new List<string>();
        var cleanupErrors = new List<string>();
        var passed = false;
        var captured = false;
        var started = DateTimeOffset.UtcNow;
        var hash = Hash(exe);
        NativeDesktopSession? session = null;
        JsonObject? identity = null;
        using var actions = new StreamWriter(Path.Combine(evidence, "actions.jsonl")) { AutoFlush = true };
        void Trace(object action) => actions.WriteLine(JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, ticks = Stopwatch.GetTimestamp(), action }));
        try
        {
            session = new NativeDesktopSession(exe, temporary, evidence, Trace);
            session.Start();
            identity = session.Snapshot;
            QaContract.Require(identity["gitHash"]!.GetValue<string>() == source, "Executable source commit differs from expected build.");
            DesktopScenarios.Run(scenario, session);
            passed = true;
        }
        catch (Exception error) { errors.Add(error.ToString()); }
        finally
        {
            if (session is not null)
            {
                try { session.CaptureEvidence(); captured = true; }
                catch (Exception error) { errors.Add("Capture: " + error); }
                session.Cleanup(cleanupErrors);
            }
            try { Directory.Delete(temporary, true); }
            catch (Exception error) { cleanupErrors.Add("Temporary cleanup: " + error.Message); }
        }
        var cleanup = new
        {
            schemaVersion = 1, ownedPid = session?.OwnedPid, exited = session?.Exited ?? true,
            graceful = session?.Graceful ?? false, processExitCode = session?.ProcessExitCode,
            releasedContacts = session?.ContactsReleased ?? true, releasedMouse = session?.MouseReleased ?? true,
            temporaryDirectory = temporary, temporaryRemoved = !Directory.Exists(temporary),
            errors = cleanupErrors
        };
        try { session?.Dispose(); }
        catch (Exception error) { cleanupErrors.Add("Automation/process handle disposal: " + error); }
        File.WriteAllText(Path.Combine(evidence, "cleanup.json"), JsonSerializer.Serialize(cleanup, QaContract.Json));
        var clean = cleanupErrors.Count == 0 && cleanup.exited && cleanup.releasedContacts && cleanup.releasedMouse &&
            cleanup.temporaryRemoved && cleanup.graceful && cleanup.processExitCode == 0;
        var complete = captured && File.Exists(Path.Combine(evidence, "uia.json")) && hash == Hash(exe);
        actions.Dispose();
        var artifacts = Directory.GetFiles(evidence).Select(path => new { file = Path.GetFileName(path), sha256 = Hash(path) }).ToArray();
        var payloads = new[] { "CorgiBlackboard.App.dll", "CorgiBlackboard.Infrastructure.dll", "CorgiBlackboard.Core.dll" }
            .Select(name => Path.Combine(Path.GetDirectoryName(exe)!, name)).Where(File.Exists)
            .Select(path => new { file = Path.GetFileName(path), sha256 = Hash(path) }).ToArray();
        var exit = QaContract.ExitCode(passed, clean, complete && errors.Count == 0);
        File.WriteAllText(Path.Combine(evidence, "result.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1, scenario, pass = exit == 0, exitCode = exit, startedUtc = started,
            finishedUtc = DateTimeOffset.UtcNow, exePath = exe, exeSha256 = hash, expectedSourceCommit = source,
            observedBuild = identity, osBuild = Environment.OSVersion.VersionString,
            dpi = session?.Dpi, inputBackend = "Win32 InjectTouchInput / FlaUI native mouse",
            screenshotSha256 = captured ? Hash(Path.Combine(evidence, "screen.png")) : null,
            screenshotReview = "pending human review", errors, cleanupPassed = clean,
            scenarioPassed = passed, evidenceComplete = complete,
            artifacts, managedPayloads = payloads, productPass = scenario != "HarnessFailure" && exit == 0
        }, QaContract.Json));
        actions.Dispose();
        return exit;
    }

    internal static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
