using System.Text.Json;

namespace UltimateUpdater.Services;

/// <summary>Shared by every JSON-backed parser that wants "a string property, or empty"; a null/missing
/// property or a non-string value is never worth failing a whole parse over.
/// <c>PackageManagerParsers.GetString</c> and <c>NvidiaDriverLookup.GetString</c> stay separate: one also
/// accepts a JSON number, the other returns null rather than empty on a miss; both genuinely different
/// contracts, not copies of this one).</summary>
internal static class JsonElementHelpers
{
    internal static string GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>Parses untrusted tool output; malformed or empty text is a miss, never an exception.</summary>
    internal static bool TryParseJson(string text, out JsonDocument? document)
    {
        document = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        try
        {
            document = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
