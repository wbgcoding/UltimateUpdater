using System.IO;
using Microsoft.Win32;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>A key or value name with a control character cannot be written into a REGEDIT5 line without
/// breaking the line, so the whole backup fails closed instead of producing a malformed file.</summary>
public class RegistryBackupNameSafetyTests : IDisposable
{
    private readonly string _relativeRoot = @"Software\WuNameSafety" + Guid.NewGuid().ToString("N");

    public RegistryBackupNameSafetyTests() => Registry.CurrentUser.CreateSubKey(_relativeRoot).Dispose();

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_relativeRoot, throwOnMissingSubKey: false);

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\tb")]
    [InlineData("a\u001fb")]
    public void A_value_name_with_a_control_character_fails_the_export(string valueName)
    {
        using (var key = Registry.CurrentUser.OpenSubKey(_relativeRoot, writable: true)!)
            key.SetValue(valueName, "x");

        Assert.Throws<IOException>(() => RegistryBackupService.ExportKeyTreeText(@"HKCU\" + _relativeRoot));
    }

    [Fact]
    public void A_subkey_name_with_a_line_break_fails_the_export()
    {
        Registry.CurrentUser.CreateSubKey(_relativeRoot + @"\a" + "\n" + "b").Dispose();

        Assert.Throws<IOException>(() => RegistryBackupService.ExportKeyTreeText(@"HKCU\" + _relativeRoot));
    }

    [Fact]
    public void Ordinary_names_still_export()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(_relativeRoot, writable: true)!)
        {
            key.SetValue("plain name", "x");
            key.CreateSubKey("child").Dispose();
        }

        var text = RegistryBackupService.ExportKeyTreeText(@"HKCU\" + _relativeRoot);

        Assert.Contains("\"plain name\"=\"x\"", text);
        Assert.Contains(@"\child]", text);
    }
}
