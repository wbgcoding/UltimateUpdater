namespace UltimateUpdater.Services;

/// <summary>Shared by every name-matching site that wants "letters and digits only, lowercase" for a
/// loose comparison (<c>BrandIcons</c>, <c>IconCacheService</c> and <c>SecurityRelevance</c>
/// all had byte-identical copies of this before). <c>NvidiaDriverLookup.Normalize</c> stays separate; it
/// keeps spaces and strips marketing words like "nvidia"/"geforce", a genuinely different contract for
/// matching GPU model names. <c>SettingsService.Normalize</c> stays separate too; it validates/clamps an
/// <c>AppSettings</c> object, unrelated beyond the name.</summary>
internal static class TextNormalization
{
    internal static string AlphanumericLower(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
