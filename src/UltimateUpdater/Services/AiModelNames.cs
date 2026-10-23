using System.Text.RegularExpressions;

namespace UltimateUpdater.Services;

/// <summary>
/// Turns a raw Ollama model tag (<c>llama3.2:3b-instruct-q4_K_M</c>) into a readable, shortened name
/// (<c>Llama 3.2 3B Instruct</c>), rule-based rather than a lookup against a fixed list, so a model
/// released after this shipped still gets a sensible name instead of its raw tag. Pure and static: no
/// network, no state. <see cref="OllamaSource"/> uses <see cref="Friendly"/> for a row's display name;
/// the raw tag stays the row's <c>Id</c> and is shown as a tooltip/detail wherever the row shows ids.
/// </summary>
public static class AiModelNames
{
    /// <summary>
    /// Family names this table cannot decompose generically: a hyphenated version that keeps its hyphen
    /// and glues its digit directly onto a letter (DeepSeek-R1, unlike every other family's "Name N.N"
    /// spacing), and two brand names that are themselves lower-case/hyphenated
    /// (gpt-oss, all-MiniLM). Keyed by the family segment with every separator normalized to a single
    /// "-" and lower-cased.
    /// </summary>
    private static readonly Dictionary<string, string> ExactFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        ["deepseek-r1"] = "DeepSeek-R1",
        ["gpt-oss"] = "gpt-oss",
        ["all-minilm"] = "all-MiniLM",
    };

    /// <summary>
    /// Canonical spelling for one family-name WORD (a "-"/"_"-delimited segment of the family, its own
    /// trailing digits already split off). A few entries are themselves portmanteaus with no separator
    /// in the raw tag at all (codellama, codegemma, starcoder, ...) and expand to more than one word.
    /// Unknown words fall through to a plain title-case, never silently dropped.
    /// </summary>
    private static readonly Dictionary<string, string> FamilyWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["llama"] = "Llama",
        ["codellama"] = "Code Llama",
        ["qwen"] = "Qwen",
        ["qwq"] = "QwQ",
        ["gemma"] = "Gemma",
        ["codegemma"] = "CodeGemma",
        ["mistral"] = "Mistral",
        ["mixtral"] = "Mixtral",
        ["nemo"] = "Nemo",
        ["small"] = "Small",
        ["codestral"] = "Codestral",
        ["ministral"] = "Ministral",
        ["magistral"] = "Magistral",
        ["devstral"] = "Devstral",
        ["phi"] = "Phi",
        ["deepseek"] = "DeepSeek",
        ["coder"] = "Coder",
        ["gpt"] = "GPT",
        ["oss"] = "OSS",
        ["granite"] = "Granite",
        ["command"] = "Command",
        ["r"] = "R",
        ["aya"] = "Aya",
        ["falcon"] = "Falcon",
        ["starcoder"] = "StarCoder",
        ["yi"] = "Yi",
        ["smollm"] = "SmolLM",
        ["tinyllama"] = "TinyLlama",
        ["nomic"] = "Nomic",
        ["mxbai"] = "mxbai",
        ["embed"] = "Embed",
        ["minilm"] = "MiniLM",
        ["bge"] = "BGE",
        ["snowflake"] = "Snowflake",
        ["arctic"] = "Arctic",
        ["llava"] = "LLaVA",
        ["moondream"] = "Moondream",
        ["minicpm"] = "MiniCPM",
        ["olmo"] = "OLMo",
        ["nemotron"] = "Nemotron",
        ["hermes"] = "Hermes",
        ["dolphin"] = "Dolphin",
        ["kimi"] = "Kimi",
        ["glm"] = "GLM",
        ["internlm"] = "InternLM",
        ["cogito"] = "Cogito",
        ["exaone"] = "EXAONE",
        ["solar"] = "Solar",
        ["orca"] = "Orca",
        ["vicuna"] = "Vicuna",
        ["zephyr"] = "Zephyr",
        ["wizardlm"] = "WizardLM",
        ["openchat"] = "OpenChat",
        ["guard"] = "Guard",
    };

    /// <summary>Tag words kept as their own title-case word rather than dropped or left lower-case.</summary>
    private static readonly HashSet<string> KeptTagWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "instruct", "coder", "vision", "embed", "thinking", "uncensored",
    };

    private static readonly Regex WordThenDigits = new(
        @"^(?<word>[A-Za-z]*)(?<digits>[0-9]+(?:\.[0-9]+)*)?$", RegexOptions.Compiled);

    private static readonly Regex MultiSizePattern = new(
        @"^(?<count>[0-9]+)x(?<size>[0-9]+(?:\.[0-9]+)?)(?<unit>[bm])$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SizePattern = new(
        @"^(?<size>[0-9]+(?:\.[0-9]+)?)(?<unit>[bm])$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex QuantPattern = new(
        @"^i?q[0-9]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// A readable, shortened name for a raw registry tag such as <c>llama3.2:3b-instruct-q4_K_M</c> or
    /// <c>user/foo-bar2:7b-q5_K_S</c>. Never throws: a tag this cannot make sense of at all comes back
    /// title-cased rather than untouched, so the row still shows something better than a raw slug.
    /// </summary>
    public static string Friendly(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
            return rawName;

        // A registry host/namespace prefix ("user/", "registry.ollama.ai/library/") names WHERE the
        // model came from, never part of the model's own name.
        var lastSlash = rawName.LastIndexOf('/');
        var withoutNamespace = lastSlash >= 0 ? rawName[(lastSlash + 1)..] : rawName;

        var colon = withoutNamespace.IndexOf(':');
        var family = colon >= 0 ? withoutNamespace[..colon] : withoutNamespace;
        var tag = colon >= 0 ? withoutNamespace[(colon + 1)..] : string.Empty;

        var familyDisplay = FormatFamily(family);
        var tagDisplay = FormatTag(tag);

        return tagDisplay.Length > 0 ? $"{familyDisplay} {tagDisplay}" : familyDisplay;
    }

    private static string FormatFamily(string family)
    {
        if (family.Length == 0)
            return family;

        var normalizedWhole = Regex.Replace(family, "[-_]+", "-").Trim('-');
        if (ExactFamilies.TryGetValue(normalizedWhole, out var exact))
            return exact;

        var tokens = family.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return TitleCase(family);

        var parts = tokens.Select(FormatFamilyToken).Where(p => p.Length > 0);
        return string.Join(' ', parts);
    }

    private static string FormatFamilyToken(string token)
    {
        var match = WordThenDigits.Match(token);
        var word = match.Groups["word"].Value;
        var digits = match.Groups["digits"].Success ? match.Groups["digits"].Value : null;

        var wordDisplay = word.Length == 0
            ? string.Empty
            : FamilyWords.TryGetValue(word, out var canonical) ? canonical : TitleCase(word);

        if (digits is null)
            return wordDisplay;

        return wordDisplay.Length == 0 ? digits : $"{wordDisplay} {digits}";
    }

    private static string FormatTag(string tag)
    {
        if (tag.Length == 0)
            return tag;

        var segments = tag.Split('-', StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>();

        foreach (var segment in segments)
        {
            var lower = segment.ToLowerInvariant();

            if (lower is "latest" or "gguf")
                continue;

            if (QuantPattern.IsMatch(lower) || lower is "fp16" or "bf16")
                continue;

            var multiSize = MultiSizePattern.Match(lower);
            if (multiSize.Success)
            {
                kept.Add($"{multiSize.Groups["count"].Value}x{multiSize.Groups["size"].Value}{multiSize.Groups["unit"].Value.ToUpperInvariant()}");
                continue;
            }

            var size = SizePattern.Match(lower);
            if (size.Success)
            {
                kept.Add($"{size.Groups["size"].Value}{size.Groups["unit"].Value.ToUpperInvariant()}");
                continue;
            }

            kept.Add(KeptTagWords.Contains(lower) ? TitleCase(lower) : TitleCase(segment));
        }

        return string.Join(' ', kept);
    }

    /// <summary>Upper-cases only the first letter; the rest of a raw tag word is normally already
    /// lower-case, and forcing the remainder to lower-case as well would mangle an embedded version
    /// like "V1.5".</summary>
    private static string TitleCase(string word) =>
        word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..];
}
