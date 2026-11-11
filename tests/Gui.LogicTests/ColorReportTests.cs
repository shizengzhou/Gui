using System.Text;
using Gui.Term;
using Xunit;

namespace Gui.LogicTests;

/// <summary>
/// "osc-color-report-format": the terminal core always answers with 16-bit components, so
/// "8-bit" and "none" are applied to the bytes on their way back to the pty.
/// </summary>
public class ColorReportTests
{
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void SixteenBitPassesThePayloadThrough()
    {
        byte[] data = Bytes("\u001b]11;rgb:0101/0202/0303\u001b\\");
        Assert.Same(data, ColorReport.Apply(data, ColorReport.SixteenBit));
    }

    [Fact]
    public void EightBitKeepsTheHighByteOfEachComponent()
    {
        byte[] data = Bytes("\u001b]11;rgb:0101/0202/0303\u001b\\");
        byte[] result = ColorReport.Apply(data, ColorReport.EightBit)!;
        Assert.Equal("\u001b]11;rgb:01/02/03\u001b\\", Encoding.UTF8.GetString(result));
    }

    [Fact]
    public void PaletteRepliesKeepTheirIndex()
    {
        byte[] data = Bytes("\u001b]4;12;rgb:0a0a/1414/1e1e\u001b\\");
        byte[] result = ColorReport.Apply(data, ColorReport.EightBit)!;
        Assert.Equal("\u001b]4;12;rgb:0a/14/1e\u001b\\", Encoding.UTF8.GetString(result));
    }

    [Fact]
    public void NoneDropsTheReplyButKeepsEverythingElse()
    {
        byte[] data = Bytes("a\u001b]10;rgb:0101/0202/0303\u0007b");
        byte[] result = ColorReport.Apply(data, ColorReport.None)!;
        Assert.Equal("ab", Encoding.UTF8.GetString(result));
    }

    [Fact]
    public void NoneSuppressesASoleReply()
    {
        Assert.Null(ColorReport.Apply(Bytes("\u001b]10;rgb:0101/0202/0303\u001b\\"), ColorReport.None));
    }

    [Fact]
    public void SuppressedReplyKeepsTheBytesAfterIt()
    {
        byte[] data = Bytes("\u001b]10;rgb:0101/0202/0303\u001b\\tail");
        byte[] result = ColorReport.Apply(data, ColorReport.None)!;
        Assert.Equal("tail", Encoding.UTF8.GetString(result));
    }

    [Fact]
    public void OtherOscSequencesAreUntouched()
    {
        byte[] data = Bytes("\u001b]0;title\u0007\u001b]52;c;aGk=\u001b\\");
        Assert.Same(data, ColorReport.Apply(data, ColorReport.None));
    }

    [Fact]
    public void UnterminatedReplyIsLeftAlone()
    {
        byte[] data = Bytes("\u001b]11;rgb:0101/0202/0303");
        Assert.Same(data, ColorReport.Apply(data, ColorReport.None));
    }

    [Fact]
    public void NormalizeFallsBackToSixteenBit()
    {
        Assert.Equal(ColorReport.EightBit, ColorReport.Normalize(" 8-BIT "));
        Assert.Equal(ColorReport.None, ColorReport.Normalize("none"));
        Assert.Equal(ColorReport.SixteenBit, ColorReport.Normalize("16bit"));
        Assert.Equal(ColorReport.SixteenBit, ColorReport.Normalize(null));
    }
}
