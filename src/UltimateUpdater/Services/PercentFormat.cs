using System.Globalization;

namespace UltimateUpdater.Services;

/// <summary>
/// One place that turns a 0-100 figure into "50%" (English) or "50 %" with a no-break space (German), so
/// every progress label follows the chosen UI language instead of gluing " %" onto a number. Follows
/// <see cref="CultureInfo.CurrentUICulture"/>, the same culture <see cref="ByteSize.Format(long)"/> uses.
/// </summary>
public static class PercentFormat
{
    public static string Format(double percent) =>
        (percent / 100d).ToString("P0", CultureInfo.CurrentUICulture);
}
