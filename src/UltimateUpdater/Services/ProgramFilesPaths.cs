namespace UltimateUpdater.Services;

/// <summary>
/// The Program Files root(s), resolved once per call instead of six independent copies of the same two
/// <see cref="Environment.GetFolderPath"/> calls scattered across the sources and the cleanup gate. On a
/// 32-bit OS the 64-bit and x86 roots resolve to the very same path, so <see cref="Roots"/> de-duplicates
/// rather than handing every caller the same directory twice.
/// </summary>
public static class ProgramFilesPaths
{
    /// <summary>The 64-bit Program Files root.</summary>
    public static string ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    /// <summary>The 32-bit Program Files (x86) root.</summary>
    public static string ProgramFilesX86 => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

    /// <summary>Both roots, de-duplicated (a 32-bit OS reports the same path for both).</summary>
    public static IReadOnlyList<string> Roots()
    {
        var programFiles = ProgramFiles;
        var programFilesX86 = ProgramFilesX86;
        return string.Equals(programFiles, programFilesX86, StringComparison.OrdinalIgnoreCase)
            ? [programFiles]
            : [programFiles, programFilesX86];
    }
}
