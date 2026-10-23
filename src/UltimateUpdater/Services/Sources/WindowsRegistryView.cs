using System.IO;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace UltimateUpdater.Services.Sources;

/// <summary>The real registry. Never throws: a key this app may not read is a key that says nothing.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRegistryView : IRegistryView
{
    public static readonly WindowsRegistryView Instance = new();

    public IReadOnlyList<string> SubKeyNames(string keyPath)
    {
        try
        {
            using var key = Open(keyPath);
            return key?.GetSubKeyNames() ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }

    public string? Value(string keyPath, string valueName)
    {
        try
        {
            using var key = Open(keyPath);
            return key?.GetValue(valueName) as string;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    public int? DwordValue(string keyPath, string valueName)
    {
        try
        {
            using var key = Open(keyPath);
            return key?.GetValue(valueName) is int number ? number : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static RegistryKey? Open(string keyPath)
    {
        var separator = keyPath.IndexOf('\\');
        if (separator <= 0)
            return null;

        var root = keyPath[..separator] switch
        {
            "HKLM" => Registry.LocalMachine,
            "HKCU" => Registry.CurrentUser,
            _ => null,
        };

        return root?.OpenSubKey(keyPath[(separator + 1)..]);
    }
}
