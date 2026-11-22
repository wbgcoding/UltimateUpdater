using UltimateUpdater.Services.Platform;
using UltimateUpdater.Services.Sources.Windows;

namespace UltimateUpdater.Tests;

/// <summary>A registry of nothing but the Uninstall keys a test adds, for the uninstaller tests.</summary>
internal sealed class FakeUninstallRegistry : IRegistryView
{
    public const string Hklm64 = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    public const string Hklm32 = @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
    public const string Hkcu = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private readonly Dictionary<string, Dictionary<string, string>> _keys = new(StringComparer.OrdinalIgnoreCase);

    public FakeUninstallRegistry Set(string keyPath, string valueName, string value)
    {
        if (!_keys.TryGetValue(keyPath, out var values))
            _keys[keyPath] = values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        values[valueName] = value;
        return this;
    }

    /// <summary>A complete, listable program key under <paramref name="root"/>; further values via <see cref="Set"/>.</summary>
    public FakeUninstallRegistry Program(string root, string subKey, string name, string uninstall = @"C:\Program Files\App\unins000.exe")
    {
        var path = $@"{root}\{subKey}";
        Set(path, "DisplayName", name);
        Set(path, "UninstallString", uninstall);
        return this;
    }

    public IReadOnlyList<string> SubKeyNames(string keyPath) =>
        _keys.Keys
            .Where(key => key.StartsWith(keyPath + @"\", StringComparison.OrdinalIgnoreCase))
            .Select(key => key[(keyPath.Length + 1)..].Split('\\')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public string? Value(string keyPath, string valueName) =>
        _keys.TryGetValue(keyPath, out var values) ? values.GetValueOrDefault(valueName) : null;
}
