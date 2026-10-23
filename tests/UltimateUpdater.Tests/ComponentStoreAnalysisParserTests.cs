using System.Text;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>
/// <see cref="ComponentStoreAnalysisParser"/> turns DISM's own, localized
/// AnalyzeComponentStore text into a <see cref="ComponentStoreAnalysis"/>. Fixtures below are captured
/// English DISM wording plus a German rendering built to the same layout DISM itself uses (a real DISM
/// run was not available in this environment; see the item's own report line).
/// </summary>
public class ComponentStoreAnalysisParserTests
{
    private const string EnglishOutput = """
        Deployment Image Servicing and Management tool
        Version: 10.0.19041.1

        Image Version: 10.0.19041.1

        [==========================100.0%==========================]
        Component Store (WinSxS) information:

        Windows Explorer Reported Size of Component Store : 8.19 GB
        Actual Size of Component Store : 4.87 GB
        Shared with Windows : 3.98 GB
        Backups and Disabled Features : 0.51 GB
        Cache and Temporary Data : 0.02 GB

        Date of Last Cleanup : N/A
        Number of Reclaimable Packages : 0
        Component Store Cleanup Recommended : No

        The operation completed successfully.
        """;

    private const string GermanOutput = """
        Bereitstellungsimage-Verwaltungstool fuer Windows
        Version: 10.0.19041.1

        Abbildversion: 10.0.19041.1

        [==========================100.0%==========================]
        Informationen zum Komponentenspeicher (WinSxS):

        Von Windows-Explorer gemeldete Groesse des Komponentenspeichers : 8,19 GB
        Tatsächliche Größe des Komponentenspeichers : 4,87 GB
        Freigegeben für Windows : 3,98 GB
        Sicherungen und deaktivierte Features : 0,51 GB
        Zwischenspeicher- und temporaere Daten : 0,02 GB

        Datum der letzten Bereinigung : Nicht verfuegbar
        Anzahl der wiederherstellbaren Pakete : 0
        Bereinigung des Komponentenspeichers empfohlen : Ja

        Der Vorgang wurde erfolgreich beendet.
        """;

    private static readonly long ExpectedActual = (long)Math.Round(4.87 * 1024 * 1024 * 1024);
    private static readonly long ExpectedShared = (long)Math.Round(3.98 * 1024 * 1024 * 1024);
    private static readonly long ExpectedBackups = (long)Math.Round(0.51 * 1024 * 1024 * 1024);

    /// <summary>
    /// DISM ran without <c>/English</c>, so on any non-English display
    /// language its message text came back in that language and the parser had to guess every wording it
    /// might use. Pulled out of <see cref="CleanupService.AnalyzeComponentStoreAsync"/> into a plain
    /// constant so this is provable without launching the real, admin-only DISM process.
    /// </summary>
    [Fact]
    public void The_real_dism_call_always_forces_English_output()
    {
        Assert.Contains("/English", CleanupService.AnalyzeComponentStoreArguments);
    }

    [Fact]
    public void English_output_is_read_in_full()
    {
        var result = ComponentStoreAnalysisParser.Parse(EnglishOutput);

        Assert.Equal(ExpectedActual, result.ActualSizeBytes);
        Assert.Equal(ExpectedShared, result.SharedWithWindowsBytes);
        Assert.Equal(ExpectedBackups, result.BackupsAndDisabledFeaturesBytes);
        Assert.False(result.CleanupRecommended);
    }

    [Fact]
    public void German_output_with_comma_decimals_is_read_in_full()
    {
        var result = ComponentStoreAnalysisParser.Parse(GermanOutput);

        Assert.Equal(ExpectedActual, result.ActualSizeBytes);
        Assert.Equal(ExpectedShared, result.SharedWithWindowsBytes);
        Assert.Equal(ExpectedBackups, result.BackupsAndDisabledFeaturesBytes);
        Assert.True(result.CleanupRecommended);
    }

    /// <summary>DISM killed, or its buffer cut, halfway through: only what was actually printed is
    /// known, the rest stays null rather than guessed from what came before it.</summary>
    [Fact]
    public void Truncated_output_leaves_the_missing_fields_null_instead_of_guessing()
    {
        const string truncated = """
            Deployment Image Servicing and Management tool
            Version: 10.0.19041.1

            Component Store (WinSxS) information:

            Actual Size of Component Store : 4.87 GB
            """;

        var result = ComponentStoreAnalysisParser.Parse(truncated);

        Assert.Equal(ExpectedActual, result.ActualSizeBytes);
        Assert.Null(result.SharedWithWindowsBytes);
        Assert.Null(result.BackupsAndDisabledFeaturesBytes);
        Assert.Null(result.CleanupRecommended);
    }

    [Fact]
    public void Empty_output_is_fully_unknown()
    {
        var result = ComponentStoreAnalysisParser.Parse(string.Empty);

        Assert.Equal(ComponentStoreAnalysis.Unknown, result);
    }

    [Fact]
    public void Null_output_is_fully_unknown()
    {
        var result = ComponentStoreAnalysisParser.Parse(null);

        Assert.Equal(ComponentStoreAnalysis.Unknown, result);
    }

    /// <summary>Text DISM never actually prints (a hostile or garbled capture) never throws and never
    /// invents a number; every field simply stays null.</summary>
    [Fact]
    public void Unrecognised_text_is_fully_unknown_and_never_throws()
    {
        var result = ComponentStoreAnalysisParser.Parse("some : line\nanother random line with no colon");

        Assert.Equal(ComponentStoreAnalysis.Unknown, result);
    }

    [Fact]
    public void A_missing_unit_on_an_otherwise_matching_line_is_left_unknown()
    {
        var result = ComponentStoreAnalysisParser.Parse("Actual Size of Component Store : 4.87");

        Assert.Null(result.ActualSizeBytes);
    }

    /// <summary>What ProcessRunner's UTF-8 StreamReader actually receives when DISM writes UTF-16LE into a
    /// redirected pipe (this fix's own shape, <see cref="Utf16RedirectRecoveryTests"/>), byte-for-byte, not
    /// a hand-written approximation.</summary>
    private static string MisdecodeAsIfUtf16(string original) =>
        Encoding.UTF8.GetString(Encoding.Unicode.GetBytes(original));

    /// <summary>
    /// DISM's AnalyzeComponentStore output is captured whole (<see
    /// cref="CleanupService.AnalyzeComponentStoreAsync"/>), the same UTF-16LE-redirect shape this fix already
    /// fixed for the streamed progress line, but never recovered here before this item - every label match
    /// failed silently against the NUL-interleaved text and the card showed dashes DISM had never actually
    /// printed. Proves the exact fix CleanupService now applies: detect, recover, then parse.
    /// </summary>
    [Fact]
    public void Misdecoded_utf16_output_is_recovered_and_read_in_full()
    {
        var misdecoded = MisdecodeAsIfUtf16(EnglishOutput);
        Assert.True(Utf16RedirectRecovery.LooksLikeMisdecodedUtf16(misdecoded));

        var recovered = Utf16RedirectRecovery.Recover(misdecoded);
        var result = ComponentStoreAnalysisParser.Parse(recovered);

        Assert.Equal(ExpectedActual, result.ActualSizeBytes);
        Assert.Equal(ExpectedShared, result.SharedWithWindowsBytes);
        Assert.Equal(ExpectedBackups, result.BackupsAndDisabledFeaturesBytes);
        Assert.False(result.CleanupRecommended);
    }

    /// <summary>Without the recovery step, the misdecoded text parses to nothing at all - proves the bug
    /// this item fixes was real, not just a defensive test.</summary>
    [Fact]
    public void Misdecoded_utf16_output_parses_to_nothing_without_recovery()
    {
        var misdecoded = MisdecodeAsIfUtf16(EnglishOutput);

        var result = ComponentStoreAnalysisParser.Parse(misdecoded);

        Assert.Equal(ComponentStoreAnalysis.Unknown, result);
    }
}

/// <summary>
/// The exact bytes <c>dism /online /cleanup-image /analyzecomponentstore</c> printed on the lab
/// VM (German display language, a real elevated run, 25.09.2026), captured whole and never touched beyond
/// base64-encoding it to fit this file. This fix's German fixture above was hand-built to a guessed
/// layout and never checked against a real run; the real bytes exposed two separate bugs at once: DISM's
/// redirected output there is the machine's own OEM code page (CP850), not the UTF-16LE shape this fix
/// already recover, and "Shared with Windows" prints in the OPPOSITE word order from that guessed fixture.
/// </summary>
public class RealVmCaptureTests
{
    // Raw bytes exactly as DISM wrote them (CP850, CRLF line endings, the progress bar's own CR-redraws
    // included); base64 only so this source file itself stays valid UTF-8.
    private const string GermanVmCaptureBase64 = """
        DQpUb29sIHp1ciBJbWFnZXZlcndhbHR1bmcgZoFyIGRpZSBCZXJlaXRzdGVsbHVuZw0KVmVyc2lvbjogMTAuMC4yNjEwMC44
        OTcyDQoNCkFiYmlsZHZlcnNpb246IDEwLjAuMjYyMDAuOTE2OA0KDQoNWz09ICAgICAgICAgICAgICAgICAgICAgICAgIDMu
        OSUgICAgICAgICAgICAgICAgICAgICAgICAgICBdIA0KDVs9PT09ICAgICAgICAgICAgICAgICAgICAgICA3LjYlICAgICAg
        ICAgICAgICAgICAgICAgICAgICAgXSANCg1bPT09PT09PT0gICAgICAgICAgICAgICAgICAgMTQuMSUgICAgICAgICAgICAg
        ICAgICAgICAgICAgIF0gDQoNWz09PT09PT09PT0gICAgICAgICAgICAgICAgIDE3LjUlICAgICAgICAgICAgICAgICAgICAg
        ICAgICBdIA0KDVs9PT09PT09PT09ICAgICAgICAgICAgICAgICAxNy41JSAgICAgICAgICAgICAgICAgICAgICAgICAgXSAN
        Cg1bPT09PT09PT09PSAgICAgICAgICAgICAgICAgMTcuNSUgICAgICAgICAgICAgICAgICAgICAgICAgIF0gDQoNWz09PT09
        PT09PT0gICAgICAgICAgICAgICAgIDE3LjUlICAgICAgICAgICAgICAgICAgICAgICAgICBdIA0KDVs9PT09PT09PT09ICAg
        ICAgICAgICAgICAgICAxNy41JSAgICAgICAgICAgICAgICAgICAgICAgICAgXSANCg1bPT09PT09PT09PSAgICAgICAgICAg
        ICAgICAgMTcuNSUgICAgICAgICAgICAgICAgICAgICAgICAgIF0gDQoNWz09PT09PT09PT0gICAgICAgICAgICAgICAgIDE3
        LjUlICAgICAgICAgICAgICAgICAgICAgICAgICBdIA0KDVs9PT09PT09PT09ICAgICAgICAgICAgICAgICAxNy41JSAgICAg
        ICAgICAgICAgICAgICAgICAgICAgXSANCg1bPT09PT09PT09PSAgICAgICAgICAgICAgICAgMTcuNSUgICAgICAgICAgICAg
        ICAgICAgICAgICAgIF0gDQoNWz09PT09PT09PT0gICAgICAgICAgICAgICAgIDE3LjUlICAgICAgICAgICAgICAgICAgICAg
        ICAgICBdIA0KDVs9PT09PT09PT09ICAgICAgICAgICAgICAgICAxNy41JSAgICAgICAgICAgICAgICAgICAgICAgICAgXSAN
        Cg1bPT09PT09PT09PSAgICAgICAgICAgICAgICAgMTcuNSUgICAgICAgICAgICAgICAgICAgICAgICAgIF0gDQoNWz09PT09
        PT09PT0gICAgICAgICAgICAgICAgIDE3LjUlICAgICAgICAgICAgICAgICAgICAgICAgICBdIA0KDVs9PT09PT09PT09ICAg
        ICAgICAgICAgICAgICAxOC44JSAgICAgICAgICAgICAgICAgICAgICAgICAgXSANCg1bPT09PT09PT09PSAgICAgICAgICAg
        ICAgICAgMTguOCUgICAgICAgICAgICAgICAgICAgICAgICAgIF0gDQoNWz09PT09PT09PT0gICAgICAgICAgICAgICAgIDE4
        LjglICAgICAgICAgICAgICAgICAgICAgICAgICBdIA0KDVs9PT09PT09PT09ICAgICAgICAgICAgICAgICAxOC44JSAgICAg
        ICAgICAgICAgICAgICAgICAgICAgXSANCg1bPT09PT09PT09PSAgICAgICAgICAgICAgICAgMTguOCUgICAgICAgICAgICAg
        ICAgICAgICAgICAgIF0gDQoNWz09PT09PT09PT09ICAgICAgICAgICAgICAgIDE5LjglICAgICAgICAgICAgICAgICAgICAg
        ICAgICBdIA0KDVs9PT09PT09PT09PT09PT0gICAgICAgICAgICAyNi45JSAgICAgICAgICAgICAgICAgICAgICAgICAgXSAN
        Cg1bPT09PT09PT09PT09PT09PT09ICAgICAgICAgMzEuNyUgICAgICAgICAgICAgICAgICAgICAgICAgIF0gDQoNWz09PT09
        PT09PT09PT09PT09PT09ICAgICAgIDM1LjQlICAgICAgICAgICAgICAgICAgICAgICAgICBdIA0KDVs9PT09PT09PT09PT09
        PT09PT09PT09ICAgICAzOC44JSAgICAgICAgICAgICAgICAgICAgICAgICAgXSANCg1bPT09PT09PT09PT09PT09PT09PT09
        PT09PSAgNDQuNSUgICAgICAgICAgICAgICAgICAgICAgICAgIF0gDQoNWz09PT09PT09PT09PT09PT09PT09PT09PT09PTUz
        LjIlICAgICAgICAgICAgICAgICAgICAgICAgICBdIA0KDVs9PT09PT09PT09PT09PT09PT09PT09PT09PT01OS44JT09ICAg
        ICAgICAgICAgICAgICAgICAgICAgXSANCg1bPT09PT09PT09PT09PT09PT09PT09PT09PT09OTAuNyU9PT09PT09PT09PT09
        PT09PT09PSAgICAgIF0gDQoNWz09PT09PT09PT09PT09PT09PT09PT09PT09PTk3LjklPT09PT09PT09PT09PT09PT09PT09
        PT09ICBdIA0KDVs9PT09PT09PT09PT09PT09PT09PT09PT09PT05OC4wJT09PT09PT09PT09PT09PT09PT09PT09PSAgXSAN
        Cg1bPT09PT09PT09PT09PT09PT09PT09PT09PT0xMDAuMCU9PT09PT09PT09PT09PT09PT09PT09PT09PV0gDQoNCkluZm9y
        bWF0aW9uZW4genVtIEtvbXBvbmVudGVuc3BlaWNoZXIgKFdpblN4Uyk6DQoNClZvbiBXaW5kb3dzLUV4cGxvcmVyIGdlbWVs
        ZGV0ZSBHcpThZSBkZXMgS29tcG9uZW50ZW5zcGVpY2hlcnMgOiAxNC45MSBHQg0KDQpUYXRzhGNobGljaGUgR3KU4WUgZGVz
        IEtvbXBvbmVudGVuc3BlaWNoZXJzIDogMTQuNDggR0INCg0KICAgIEZyZWlnZWdlYmVuIGaBciBXaW5kb3dzIDogNy45MCBH
        Qg0KICAgIFNpY2hlcnVuZ2VuIHVuZCBkZWFrdGl2aWVydGUgRmVhdHVyZXMgOiA2LjM3IEdCDQogICAgQ2FjaGUgdW5kIHRl
        bXBvcoRyZSBEYXRlbiA6IDIwNy40NiBNQg0KDQpEYXR1bSBkZXIgbGV0enRlbiBCZXJlaW5pZ3VuZyA6IDIwMjYtMDktMjUg
        MTM6MDM6MTMNCg0KQW56YWhsIHZvbiBQYWtldGVuLCBkaWUgZnJlaWdlZ2ViZW4gd2VyZGVuIGuUbm5lbiA6IDINCkJlcmVp
        bmlndW5nIGRlcyBLb21wb25lbnRlbnNwZWljaGVycyBlbXBmb2hsZW4gOiBKYQ0KDQpEZXIgVm9yZ2FuZyB3dXJkZSBlcmZv
        bGdyZWljaCBiZWVuZGV0Lg0K
        """;

    private static readonly long ExpectedActual = (long)Math.Round(14.48 * 1024 * 1024 * 1024);
    private static readonly long ExpectedShared = (long)Math.Round(7.90 * 1024 * 1024 * 1024);
    private static readonly long ExpectedBackups = (long)Math.Round(6.37 * 1024 * 1024 * 1024);

    private static byte[] RawBytes() =>
        Convert.FromBase64String(GermanVmCaptureBase64);

    /// <summary>What <see cref="Services.ProcessRunner"/> now hands back for this one call: the real bytes,
    /// preserved 1:1 as chars by decoding with <see cref="Encoding.Latin1"/> instead of the UTF-8 every
    /// other tool gets.</summary>
    private static string Latin1PreservedCapture() =>
        Encoding.Latin1.GetString(RawBytes());

    [Fact]
    public void Real_capture_is_not_the_utf16_shape()
    {
        Assert.False(Utf16RedirectRecovery.LooksLikeMisdecodedUtf16(Latin1PreservedCapture()));
    }

    [Fact]
    public void Real_capture_needs_oem_recovery()
    {
        Assert.True(OemTextRecovery.NeedsRecovery(Latin1PreservedCapture()));
    }

    [Fact]
    public void Real_capture_yields_the_sizes_dism_actually_printed_once_recovered()
    {
        var recovered = OemTextRecovery.Recover(Latin1PreservedCapture());

        var result = ComponentStoreAnalysisParser.Parse(recovered);

        Assert.Equal(ExpectedActual, result.ActualSizeBytes);
        Assert.Equal(ExpectedShared, result.SharedWithWindowsBytes);
        Assert.Equal(ExpectedBackups, result.BackupsAndDisabledFeaturesBytes);
        Assert.True(result.CleanupRecommended);
    }

    /// <summary>What the app actually showed on the VM before this item: decoded as UTF-8 (the old,
    /// still-default behaviour for every other <see cref="Services.ProcessRunner"/> caller), every accented
    /// label byte becomes an unrecoverable U+FFFD and no label matches at all - proves the "keine Größen
    /// gemeldet" finding was this bug, not a fluke of that one run.</summary>
    [Fact]
    public void Real_capture_decoded_as_utf8_parses_to_nothing_the_original_bug()
    {
        var misdecoded = Encoding.UTF8.GetString(RawBytes());

        var result = ComponentStoreAnalysisParser.Parse(misdecoded);

        Assert.Null(result.ActualSizeBytes);
        Assert.Null(result.SharedWithWindowsBytes);
    }
}
