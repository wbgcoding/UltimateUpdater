namespace UltimateUpdater.Services.Cleanup;

/// <summary>
/// How much care one target deserves before ticking it. Fixed on the catalogue entry, never guessed at
/// runtime: a target's risk does not depend on what it happens to find on THIS machine.
/// </summary>
public enum CleanupRiskLevel
{
    /// <summary>Recreated automatically, or never actually used again, nothing worth a second thought.</summary>
    Safe,

    /// <summary>Reversible in principle, but with a real, sometimes noticeable consequence, a slower first
    /// launch, lost diagnostic data for a problem nobody is actively chasing, no more "restore from the
    /// recycle bin" once it runs.</summary>
    Considered,

    /// <summary>Touches something Windows or another program depends on being exactly right, a registry
    /// write, however carefully backed up and re-checked, belongs here on principle.</summary>
    Risky,
}
