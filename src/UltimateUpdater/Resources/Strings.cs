using System.Globalization;
using System.Resources;

namespace UltimateUpdater.Resources;

/// <summary>Thin wrapper over the Strings.resx satellite resources (no VS designer-generation dependency).</summary>
public static class Strings
{
    private static readonly ResourceManager ResourceManager = new("UltimateUpdater.Resources.Strings", typeof(Strings).Assembly);

    public static CultureInfo? Culture { get; set; }

    public static string Get(string key) => ResourceManager.GetString(key, Culture) ?? key;
}
