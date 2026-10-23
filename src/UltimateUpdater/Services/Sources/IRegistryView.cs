namespace UltimateUpdater.Services.Sources;

/// <summary>
/// The registry reads a launcher lookup needs. It sits behind a seam so the resolution rules can be
/// exercised against a table of keys instead of whatever happens to be installed on the machine that
/// runs the tests. Key paths are written "HKLM\..." / "HKCU\...".
/// </summary>
public interface IRegistryView
{
    /// <summary>Names of the subkeys under a key; empty when the key does not exist.</summary>
    IReadOnlyList<string> SubKeyNames(string keyPath);

    /// <summary>A string value, or null when key or value is missing. An empty name reads the default value.</summary>
    string? Value(string keyPath, string valueName);

    /// <summary>A DWORD value (an Uninstall entry's <c>SystemComponent</c> flag, say), or null when key or
    /// value is missing or not a number. The default reads the string form, which suits table-backed views.</summary>
    int? DwordValue(string keyPath, string valueName) =>
        int.TryParse(Value(keyPath, valueName), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : null;
}
