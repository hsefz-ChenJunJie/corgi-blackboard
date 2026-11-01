using System.Globalization;

namespace CorgiBlackboard.Infrastructure.Diagnostics;

public sealed record DiagnosticRetentionPolicy(int RetentionDays = 14, long MaximumBytes = 2L * 1024 * 1024 * 1024)
{
    public static bool TryParse(string daysText, string megabytesText, out DiagnosticRetentionPolicy policy)
    {
        policy = new();
        if (!int.TryParse(daysText, out var days) || days is < 1 or > 36500 ||
            !decimal.TryParse(megabytesText, NumberStyles.Number, CultureInfo.CurrentCulture, out var megabytes) ||
            megabytes < 1 || megabytes > long.MaxValue / (1024m * 1024m))
            return false;
        policy = new(days, (long)(megabytes * 1024m * 1024m));
        return true;
    }
}
