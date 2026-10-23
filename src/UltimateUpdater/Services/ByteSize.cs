using System.Globalization;

namespace UltimateUpdater.Services;

/// <summary>One place that turns a byte count into "45.2 MB" (or, in German, "45,2 MB"), so the table and
/// the progress window agree. <see cref="Format"/> deliberately follows the chosen UI language, not a fixed
/// default; <see cref="CultureInfo.CurrentUICulture"/>, the same culture the date formatting in
/// <c>UpdateRowViewModel</c>/<c>CleanupHistoryRowViewModel</c> follows. <see cref="FormatInvariant"/> is the
/// separate path for text that must NOT vary by Windows language; <c>ActivityLog</c>'s log lines, read on
/// whatever machine diagnoses them.</summary>
public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Format(long bytes) => Format(bytes, CultureInfo.CurrentUICulture);

    public static string FormatInvariant(long bytes) => Format(bytes, CultureInfo.InvariantCulture);

    private static string Format(long bytes, CultureInfo culture)
    {
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(culture, $"{value:0.0} {Units[unit]}");
    }
}
