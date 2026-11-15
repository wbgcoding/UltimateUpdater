using System.Globalization;
using System.Text;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>
/// DISM and defrag print their live lines in the machine's OEM code page once redirected, sfc prints
/// UTF-16LE. The step output is read as Latin1 so no byte is lost, then decoded here. A UTF-8 read used to
/// show "f?r" for "für" in the cleanup status line.
/// </summary>
public class RepairStepOutputDecodingTests
{
    private const string Line = "Abbild für den Komponentenspeicher wird überprüft: 40.0%";

    private static string AsLatin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    [Fact]
    public void An_oem_code_page_line_keeps_its_umlauts()
    {
        var oem = CodePagesEncodingProvider.Instance.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage)
                  ?? Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);

        Assert.Equal(Line, CleanupService.DecodeStepSegment(AsLatin1(oem.GetBytes(Line))));
    }

    [Fact]
    public void A_utf16_line_keeps_its_umlauts() =>
        Assert.Equal(Line, CleanupService.DecodeStepSegment(AsLatin1(Encoding.Unicode.GetBytes(Line))));

    [Fact]
    public void A_plain_ascii_line_is_left_as_it_is() =>
        Assert.Equal("[==== 40.0% ====]", CleanupService.DecodeStepSegment("[==== 40.0% ====]"));
}
