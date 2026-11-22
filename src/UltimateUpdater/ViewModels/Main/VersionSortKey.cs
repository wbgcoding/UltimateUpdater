using System.Text;
using System.Text.RegularExpressions;

namespace UltimateUpdater.ViewModels.Main;

/// <summary>A text key that sorts version strings by their numbers: every digit run is padded to a fixed
/// width, so 10.0 sorts after 9.9 while text parts (beta, rc) still compare as text.</summary>
internal static partial class VersionSortKey
{
    private const int DigitWidth = 12;

    internal static string For(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return string.Empty;

        var builder = new StringBuilder(version.Length + 16);
        foreach (Match part in Parts().Matches(version.Trim()))
        {
            if (char.IsAsciiDigit(part.Value[0]))
            {
                var digits = part.Value.TrimStart('0');
                if (digits.Length > DigitWidth)
                    digits = digits[^DigitWidth..];
                builder.Append(digits.PadLeft(DigitWidth, '0'));
            }
            else
                builder.Append(part.Value.ToLowerInvariant());
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"[0-9]+|[^0-9]+")]
    private static partial Regex Parts();
}
