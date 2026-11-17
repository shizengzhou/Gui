using System.Text;
using Gui.Term;
using Xunit;

namespace Gui.LogicTests;

/// <summary>
/// The "text:" action's value decoder: Zig string-literal escapes, decoded when the binding
/// runs, so a bad escape drops the action instead of invalidating the config line.
/// </summary>
public class ActionTextTests
{
    private static byte[] Decode(string text)
    {
        Assert.True(ActionText.TryDecode(text, out byte[] bytes), $"{text} should decode");
        return bytes;
    }

    [Fact]
    public void ControlEscapesBecomeSingleBytes()
    {
        Assert.Equal(new byte[] { 0x15 }, Decode("\\x15"));
        Assert.Equal(new byte[] { 0x0A, 0x0D, 0x09 }, Decode("\\n\\r\\t"));
    }

    [Fact]
    public void QuotedBackslashAndQuotesDecode()
    {
        Assert.Equal(new byte[] { 0x5C }, Decode("\\\\"));
        Assert.Equal(new byte[] { 0x27 }, Decode("\\'"));
        Assert.Equal(new byte[] { 0x22 }, Decode("\\\""));
    }

    [Fact]
    public void PlainTextIsUtf8()
    {
        Assert.Equal(Encoding.UTF8.GetBytes("hi"), Decode("hi"));
        Assert.Equal(Encoding.UTF8.GetBytes("中"), Decode("中"));
    }

    [Fact]
    public void UnicodeEscapesAreEncodedAsUtf8()
    {
        Assert.Equal(Encoding.UTF8.GetBytes("π"), Decode("\\u{3c0}"));
        Assert.Equal(new byte[] { 0xF0, 0x9F, 0x98, 0x80 }, Decode("\\u{1F600}"));
    }

    [Fact]
    public void InvalidEscapesDropTheWholeValue()
    {
        Assert.False(ActionText.TryDecode("\\q", out byte[] unknown));
        Assert.Empty(unknown);
        Assert.False(ActionText.TryDecode("\\x1", out _));
        Assert.False(ActionText.TryDecode("\\u{", out _));
        Assert.False(ActionText.TryDecode("\\u{D800}", out _));
        Assert.False(ActionText.TryDecode("trailing\\", out _));
    }

    [Fact]
    public void EmptyValueDecodesToNothing() => Assert.Empty(Decode(string.Empty));

    [Fact]
    public void CursorKeyPairsSplitOnTheFirstComma()
    {
        Assert.True(ActionText.TryDecodePair("\\x1b[A,\\x1bOA", out byte[] normal, out byte[] application));
        Assert.Equal(new byte[] { 0x1B, 0x5B, 0x41 }, normal);
        Assert.Equal(new byte[] { 0x1B, 0x4F, 0x41 }, application);

        Assert.False(ActionText.TryDecodePair("\\x1b[A", out _, out _));
        Assert.False(ActionText.TryDecodePair("\\q,\\x1bOA", out _, out _));
    }
}
