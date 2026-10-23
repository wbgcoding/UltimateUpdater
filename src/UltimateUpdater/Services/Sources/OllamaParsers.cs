using System.Globalization;
using System.Text.Json;

namespace UltimateUpdater.Services.Sources;

/// <summary>
/// Pure parsing for <see cref="OllamaSource"/>. Everything the local server and the registry say is treated
/// as hostile input: a malformed document yields nothing rather than throwing, and a model name is validated
/// before it is ever put into a URL.
/// </summary>
public static class OllamaParsers
{
    /// <summary>
    /// `GET http://127.0.0.1:11434/api/tags`, captured verbatim on a clean VM:
    /// { "models": [ { "name": "all-minilm:latest", "size": 45960996,
    /// "digest": "1b226e2802dbb772…75ef",
    /// "modified_at": "2026-07-26T18:03:01.8176022+02:00" } ] }
    /// A model with no digest is skipped: the digest IS the comparison, so an entry without one cannot say
    /// anything about being out of date.
    /// </summary>
    public static IReadOnlyList<OllamaModel> ParseLocalModels(string json)
    {
        var results = new List<OllamaModel>();

        if (string.IsNullOrWhiteSpace(json))
            return results;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return results;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("models", out var models)
                || models.ValueKind != JsonValueKind.Array)
            {
                return results;
            }

            foreach (var element in models.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                    continue;

                var name = JsonElementHelpers.GetString(element, "name");
                var digest = NormalizeDigest(JsonElementHelpers.GetString(element, "digest"));

                if (name.Length == 0 || digest is null)
                    continue;

                long? size = element.TryGetProperty("size", out var sizeValue)
                             && sizeValue.ValueKind == JsonValueKind.Number
                             && sizeValue.TryGetInt64(out var bytes) && bytes > 0
                    ? bytes
                    : null;

                DateTimeOffset? modified =
                    DateTimeOffset.TryParse(
                        JsonElementHelpers.GetString(element, "modified_at"), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var date)
                        ? date
                        : null;

                results.Add(new OllamaModel(name, digest, size, modified));
            }
        }

        return results;
    }

    /// <summary>
    /// A digest as 64 lowercase hex characters, with or without the "sha256:" prefix the registry uses.
    /// Anything else is not a digest and must not be compared as if it were.
    /// </summary>
    public static string? NormalizeDigest(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var digest = value.Trim();
        if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            digest = digest["sha256:".Length..];

        if (digest.Length != 64 || !digest.All(Uri.IsHexDigit))
            return null;

        return digest.ToLowerInvariant();
    }

    /// <summary>
    /// Splits "name:tag" for a model from Ollama's own library, which is the only namespace whose registry
    /// path this app can know. A name carrying a slash belongs to a user namespace or another registry
    /// entirely. Those are refused rather than guessed at, because a guessed URL built from a name is how a
    /// model name turns into a request to somebody else's host.
    /// </summary>
    public static bool TrySplitLibraryModel(string? model, out string name, out string tag)
    {
        name = string.Empty;
        tag = string.Empty;

        if (string.IsNullOrWhiteSpace(model) || model.Contains('/') || model.Contains('\\'))
            return false;

        var parts = model.Split(':');
        if (parts.Length is not (1 or 2))
            return false;

        if (!IsPlainToken(parts[0]))
            return false;

        var candidateTag = parts.Length == 2 ? parts[1] : "latest";
        if (!IsPlainToken(candidateTag))
            return false;

        name = parts[0];
        tag = candidateTag;
        return true;
    }

    /// <summary>
    /// Letters, digits and the few separators model names really use: nothing that changes a path. The
    /// ".." check is not decoration: Uri.EscapeDataString leaves dots alone, so a segment of ".." travels
    /// into the URL intact and walks out of /v2/library/ (caught by its own test).
    /// </summary>
    private static bool IsPlainToken(string value) =>
        value.Length is > 0 and <= 128
        && !value.Contains("..")
        && value != "."
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
}
