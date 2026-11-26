namespace UltimateUpdater.Tests;

/// <summary>
/// A fact that needs the real winget app-execution alias of the machine running the suite. Without one the
/// test is reported as skipped with that reason, instead of passing silently as if it had checked something.
/// </summary>
public sealed class WingetHostFactAttribute : FactAttribute
{
    public WingetHostFactAttribute()
    {
        if (!File.Exists(AliasPath))
            Skip = "winget is not installed on this machine";
    }

    internal static string AliasPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
}
