#if DEBUG
using System.IO;

namespace UltimateUpdater.Review;

/// <summary>Window size a surface is shot at: the size the window opens with, or the smallest the user can drag it to.</summary>
internal enum ReviewSize
{
    Default,
    Min,
}

/// <summary>
/// The review harness command line (Debug builds only):
/// <c>--review &lt;ids|all|area:NAME|ID[,ID...]&gt; --out DIR [--lang en,de] [--theme light,dark] [--size default|min|both]</c>.
/// <c>--review ids</c> only lists the surface ids.
/// </summary>
internal sealed class ReviewOptions
{
    public const string Usage =
        "--review <ids|all|area:NAME|ID[,ID...]> --out <dir> [--lang en,de|all] [--theme light,dark] [--size default|min|both] [--allow-host]";

    public required IReadOnlyList<string> Selectors { get; init; }

    public required string OutDirectory { get; init; }

    public required IReadOnlyList<string> Languages { get; init; }

    public required IReadOnlyList<string> Themes { get; init; }

    public required IReadOnlyList<ReviewSize> Sizes { get; init; }

    /// <summary>Lets the run happen outside a virtual machine. Off by default: the harness shows real windows.</summary>
    public bool AllowHost { get; init; }

    public bool ListOnly => Selectors.Count == 1 && Selectors[0].Equals("ids", StringComparison.OrdinalIgnoreCase);

    public static ReviewOptions? Parse(string[] args, out string error)
    {
        error = string.Empty;
        string? Value(string name)
        {
            var index = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        var selector = Value("--review");
        var output = Value("--out");
        if (string.IsNullOrWhiteSpace(selector) || string.IsNullOrWhiteSpace(output))
        {
            error = "Usage: " + Usage;
            return null;
        }

        string[] Split(string? value, string fallback) =>
            (value ?? fallback).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var languages = Split(Value("--lang"), "en,de");
        if (languages is ["all"])
            languages = [.. LocalizationService.SupportedLanguages];

        var themes = Split(Value("--theme"), "light,dark");
        var size = Value("--size") ?? "default";
        var sizes = size.ToLowerInvariant() switch
        {
            "min" => new[] { ReviewSize.Min },
            "both" => [ReviewSize.Default, ReviewSize.Min],
            _ => new[] { ReviewSize.Default },
        };

        return new ReviewOptions
        {
            Selectors = Split(selector, "all"),
            OutDirectory = Path.GetFullPath(output),
            Languages = languages,
            Themes = themes,
            Sizes = sizes,
            AllowHost = args.Any(a => a.Equals("--allow-host", StringComparison.OrdinalIgnoreCase)),
        };
    }
}
#endif
