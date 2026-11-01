using System.Reflection;
using System.Runtime.InteropServices;

namespace CorgiBlackboard.Infrastructure.Diagnostics;

internal sealed record DiagnosticBuildIdentity(string ReleaseChannel, string BuildVersion, string GitHash)
{
    public static DiagnosticBuildIdentity FromAssembly(Assembly assembly)
    {
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(attribute => attribute.Key, attribute => attribute.Value ?? "", StringComparer.Ordinal);
        return new(
            metadata.GetValueOrDefault("ReleaseChannel", "Test"),
            metadata.GetValueOrDefault("BuildVersion", assembly.GetName().Version?.ToString() ?? "unknown"),
            metadata.GetValueOrDefault("GitHash", "unknown"));
    }
}

internal static class DiagnosticRuntimeIdentity
{
    public static string OperatingSystem { get; } = RuntimeInformation.OSDescription;
    public static string DeviceId { get; } = Environment.MachineName;
    public static string Dpi { get; } = GetSystemDpi() is uint dpi ? $"{dpi} DPI" : "unknown";
    public static string Architecture { get; } = RuntimeInformation.ProcessArchitecture.ToString();

    private static uint? GetSystemDpi()
    {
        if (!System.OperatingSystem.IsWindows() || !NativeLibrary.TryLoad("user32.dll", out var library)) return null;
        try
        {
            if (!NativeLibrary.TryGetExport(library, "GetDpiForSystem", out var address)) return null;
            return Marshal.GetDelegateForFunctionPointer<GetDpiForSystemDelegate>(address)();
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint GetDpiForSystemDelegate();
}
