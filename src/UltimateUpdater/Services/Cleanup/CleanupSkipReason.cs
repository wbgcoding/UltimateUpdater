using System.Globalization;

namespace UltimateUpdater.Services.Cleanup;

/// <summary>
/// Why an area of a run was not done: the resource key of the sentence plus its single optional
/// argument. Kept as data rather than the resolved text so the history record still follows the UI
/// language, like the area names beside it.
/// </summary>
internal sealed record CleanupSkipReason(string Key, string? Argument = null)
{
    public string Text => Format(Key, Argument is null ? [] : [Argument]);

    internal static string Format(string key, IReadOnlyList<string> arguments) => arguments.Count == 0
        ? LocalizationService.Instance[key]
        : string.Format(CultureInfo.CurrentUICulture, LocalizationService.Instance[key], [.. arguments]);
}
