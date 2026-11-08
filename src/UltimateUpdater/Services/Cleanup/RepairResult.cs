namespace UltimateUpdater.Services.Cleanup;

/// <summary>How a system file or image check ended. Kept as a number in the history file, so a value added
/// later never makes an older build fail to read the whole record.</summary>
public enum RepairResult
{
    /// <summary>Nothing was wrong.</summary>
    Ok = 0,

    /// <summary>Something was wrong and Windows fixed it.</summary>
    Repaired = 1,

    /// <summary>Something is wrong that the check could not fix.</summary>
    ProblemsFound = 2,
}
