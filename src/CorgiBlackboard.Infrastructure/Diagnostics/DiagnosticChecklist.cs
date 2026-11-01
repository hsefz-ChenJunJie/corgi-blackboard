namespace CorgiBlackboard.Infrastructure.Diagnostics;

internal static class DiagnosticChecklist
{
    internal static string Read()
    {
        using var stream = typeof(DiagnosticChecklist).Assembly.GetManifestResourceStream(
            "CorgiBlackboard.ClassroomChecklist.md")
            ?? throw new InvalidOperationException("The versioned classroom checklist resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
