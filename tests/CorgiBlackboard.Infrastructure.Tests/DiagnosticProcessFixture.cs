using System.Diagnostics;
using CorgiBlackboard.Core.Diagnostics;
using CorgiBlackboard.Infrastructure.Diagnostics;

namespace CorgiBlackboard.Infrastructure.Tests;

internal static class DiagnosticProcessFixture
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 2) return 64;
        if (args[0] == "session")
        {
            await using var session = new DiagnosticSession(new DiagnosticOptions { RootDirectory = args[1] });
            session.TryRecord(new DiagnosticEvent("foreign-process-event"));
            await session.FlushAsync();
            Console.WriteLine(session.DirectoryPath);
            await Console.In.ReadLineAsync();
            return 0;
        }
        if (args[0] == "lease")
        {
            try
            {
                using var lease = DiagnosticDirectoryAccess.Lease(args[1], () => Console.WriteLine("contended"));
                Console.WriteLine("leased");
                await Console.In.ReadLineAsync();
                return 0;
            }
            catch (DirectoryNotFoundException) { Console.WriteLine("missing"); return 2; }
        }
        return 64;
    }
}

internal sealed class DiagnosticChild : IAsyncDisposable
{
    public Process Process { get; }
    public DiagnosticChild(string mode, string directory)
    {
        var sdk = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? throw new InvalidOperationException("DOTNET_ROOT required");
        var start = new ProcessStartInfo(Path.Combine(sdk, "dotnet.exe"))
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        start.ArgumentList.Add(typeof(DiagnosticProcessFixture).Assembly.Location);
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(directory);
        Process = Process.Start(start)!;
    }
    public Task<string?> ReadLine() => Process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
    public async Task<int> Exit()
    {
        await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        return Process.ExitCode;
    }
    public async ValueTask DisposeAsync()
    {
        if (!Process.HasExited)
        {
            Process.Kill(entireProcessTree: true);
            await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        Process.Dispose();
    }
}
