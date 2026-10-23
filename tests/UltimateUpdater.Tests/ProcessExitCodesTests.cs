using UltimateUpdater.Services.Sources;
using Xunit;

namespace UltimateUpdater.Tests;

/// <summary>
/// <see cref="ProcessExitCodes.IsCrash"/> is what tells a genuine crash (WORK-IMPL.md item 3: sdkmanager's
/// batch wrapper aborting under a mismatched JVM) apart from a tool's own ordinary nonzero failure exit.
/// </summary>
public class ProcessExitCodesTests
{
    /// <summary>STATUS_STACK_BUFFER_OVERRUN (0xC0000409), the exact code from the owner's machine.</summary>
    [Fact]
    public void The_owners_stack_buffer_overrun_code_is_a_crash()
    {
        Assert.True(ProcessExitCodes.IsCrash(-1073740791));
    }

    /// <summary>.NET's own unhandled-exception exit code, outside the NTSTATUS band checked separately.</summary>
    [Fact]
    public void The_dotnet_unhandled_exception_code_is_a_crash()
    {
        Assert.True(ProcessExitCodes.IsCrash(-532462766));
    }

    [Fact]
    public void A_clean_exit_is_not_a_crash()
    {
        Assert.False(ProcessExitCodes.IsCrash(0));
    }

    /// <summary>An ordinary tool-reported failure (npm, chocolatey and friends use small positive codes).</summary>
    [Fact]
    public void A_small_positive_exit_code_is_not_a_crash()
    {
        Assert.False(ProcessExitCodes.IsCrash(1));
    }

    /// <summary>One below the NTSTATUS error floor (0xBFFFFFFF) must not be swept in by an off-by-one band.</summary>
    [Fact]
    public void The_code_just_below_the_ntstatus_floor_is_not_a_crash()
    {
        Assert.False(ProcessExitCodes.IsCrash(unchecked((int)0xBFFFFFFF)));
    }

    /// <summary>The NTSTATUS error floor itself (0xC0000000) is already a crash.</summary>
    [Fact]
    public void The_ntstatus_floor_itself_is_a_crash()
    {
        Assert.True(ProcessExitCodes.IsCrash(unchecked((int)0xC0000000)));
    }

    /// <summary>Npm's own measured exit code (0xFFFFF026, an ordinary node/libuv error), not a crash.</summary>
    [Fact]
    public void Npms_own_measured_exit_code_is_not_a_crash()
    {
        Assert.False(ProcessExitCodes.IsCrash(-4058));
    }

    /// <summary>The node/libuv error band's ceiling.</summary>
    [Fact]
    public void Minus_one_is_not_a_crash()
    {
        Assert.False(ProcessExitCodes.IsCrash(-1));
    }

    /// <summary>The node/libuv error band's floor.</summary>
    [Fact]
    public void Minus_four_thousand_ninety_five_is_not_a_crash()
    {
        Assert.False(ProcessExitCodes.IsCrash(-4095));
    }

    /// <summary>STATUS_ACCESS_VIOLATION (0xC0000005), just outside the node/libuv band, stays a crash.</summary>
    [Fact]
    public void The_access_violation_code_is_a_crash()
    {
        Assert.True(ProcessExitCodes.IsCrash(unchecked((int)0xC0000005)));
    }

    /// <summary>.NET's own unhandled-exception exit code stays a crash even with the new band carved out.</summary>
    [Fact]
    public void The_dotnet_unhandled_exception_code_is_still_a_crash_with_the_new_band()
    {
        Assert.True(ProcessExitCodes.IsCrash(unchecked((int)0xE0434352)));
    }
}
