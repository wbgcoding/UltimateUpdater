using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;

namespace UltimateUpdater.Services.Sources;

/// <summary>The latest driver NVIDIA offers for a GPU, from DriverManualLookup.</summary>
public sealed record NvidiaDriver(string Version, string DownloadUrl, long? SizeBytes, DateTimeOffset? ReleaseDate, string? DetailsUrl);

/// <summary>
/// Pure parsing for NVIDIA's (undocumented but long-stable) driver lookup service. Kept separate from
/// the source so the fragile bits (matching a GPU name to a product id, and reading the response)
/// are unit-tested against captured payloads rather than the live endpoint. Every method is defensive:
/// anything unexpected yields null, never an exception (SECURITY.md, hostile-input rule).
/// </summary>
public static class NvidiaDriverLookup
{
    /// <summary>OS ids are stable in NVIDIA's service: Windows 11 = 135, Windows 10 64-bit = 57.</summary>
    public const int OsIdWindows11 = 135;
    public const int OsIdWindows10X64 = 57;

    /// <summary>Reads DriverManualLookup JSON: IDS[0].downloadInfo.{Version,DownloadURL,…}.</summary>
    public static NvidiaDriver? ParseDriverResponse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (!root.TryGetProperty("IDS", out var ids) || ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() == 0)
                return null;

            if (!ids[0].TryGetProperty("downloadInfo", out var info))
                return null;

            var version = GetString(info, "Version");
            var url = GetString(info, "DownloadURL");
            if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(url))
                return null;

            var size = ParseSize(GetString(info, "DownloadURLFileSize"));

            DateTimeOffset? released = null;
            if (DateTimeOffset.TryParse(GetString(info, "ReleaseDateTime"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var parsed))
                released = parsed;

            // DetailsURL becomes the row's Open target, so it is validated HERE, where it arrives from a
            // remote response, not at the click. Any other scheme would be a registered local protocol
            // handler invoked on NVIDIA's word; a details page we cannot vouch for is simply left out.
            return new NvidiaDriver(version!, url!, size, released, UrlSafety.SafeHttpUrl(GetString(info, "DetailsURL")));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Maps a detected adapter name to a Product-Family id from lookupValueSearch (TypeID=3) XML. The
    /// vendor prefix and marketing noise ("NVIDIA", "Laptop GPU") differ between WMI and NVIDIA's own
    /// list, so the comparison is on a normalized form, exact match first and a contains fallback after.
    /// </summary>
    public static int? FindProductId(string? xml, string gpuName)
    {
        var target = Normalize(gpuName);
        if (target.Length == 0)
            return null;

        var candidates = ReadLookupValues(xml);
        if (candidates.Count == 0)
            return null;

        foreach (var (name, value) in candidates)
        {
            if (Normalize(name) == target)
                return value;
        }

        // fallback: the fullest name that is contained in the detected one (so "GeForce RTX 4070"
        // still resolves when Windows reports "NVIDIA GeForce RTX 4070")
        (string Name, int Value)? best = null;
        foreach (var candidate in candidates)
        {
            var normalized = Normalize(candidate.Name);
            if (normalized.Length > 0 && target.Contains(normalized)
                && (best is null || normalized.Length > Normalize(best.Value.Name).Length))
            {
                best = candidate;
            }
        }

        return best?.Value;
    }

    /// <summary>Compares two "560.94"-style versions; true when <paramref name="latest"/> is newer.</summary>
    public static bool IsNewer(string? installed, string? latest)
    {
        var latestValue = ParseVersion(latest);
        if (latestValue is null)
            return false;

        var installedValue = ParseVersion(installed);
        // nothing known installed but a real latest exists → offer it
        return installedValue is null || latestValue > installedValue;
    }

    private static List<(string Name, int Value)> ReadLookupValues(string? xml)
    {
        var result = new List<(string, int)>();
        if (string.IsNullOrWhiteSpace(xml))
            return result;

        try
        {
            var document = XDocument.Parse(xml);
            foreach (var element in document.Descendants().Where(e => e.Name.LocalName == "LookupValue"))
            {
                var name = element.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value;
                var valueText = element.Elements().FirstOrDefault(e => e.Name.LocalName == "Value")?.Value;
                if (!string.IsNullOrWhiteSpace(name) && int.TryParse(valueText, out var value))
                    result.Add((name!, value));
            }
        }
        catch (System.Xml.XmlException)
        {
            // malformed XML; treat as no candidates
        }

        return result;
    }

    private static double? ParseVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;

        return double.TryParse(version, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? ParseSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        // typically "823.35 MB"
        var parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 1
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            || !double.IsFinite(number) || number < 0)
        {
            return null;
        }

        // An unrecognised unit ("TB", a typo, a future NVIDIA format change) must never fall
        // back to MB's multiplier: an invented size is worse than none. Never guess a unit.
        var unit = parts.Length > 1 ? parts[1].ToUpperInvariant() : "MB";
        double? multiplier = unit switch
        {
            "B" => 1d,
            "KB" => 1024d,
            "MB" => 1024d * 1024,
            "GB" => 1024d * 1024 * 1024,
            _ => null,
        };

        if (multiplier is not { } known)
            return null;

        var bytes = number * known;
        return bytes > long.MaxValue ? null : (long)bytes;
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var lowered = value.ToLowerInvariant();
        var builder = new System.Text.StringBuilder(lowered.Length);
        foreach (var c in lowered)
        {
            if (char.IsLetterOrDigit(c))
                builder.Append(c);
            else if (c == ' ')
                builder.Append(' ');
        }

        // drop marketing words that appear on one side only
        var cleaned = builder.ToString();
        foreach (var noise in (string[])["nvidia", "geforce", "laptop gpu", "laptop", "graphics"])
            cleaned = cleaned.Replace(noise, " ");

        return string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
