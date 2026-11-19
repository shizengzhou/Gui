using System;
using System.Text;
using Xunit;

namespace Gui.LogicTests;

/// <summary>
/// A session whose pane never arrives (a tab handover that failed after the old pane was
/// closed) keeps receiving pty output with no one to drain it. The buffer must stop growing
/// at a fixed size, and the discarded part has to be announced rather than silently lost.
/// </summary>
public class PendingOutputTests
{
    private static byte[] Filler(byte value, int length)
    {
        var data = new byte[length];
        Array.Fill(data, value);
        return data;
    }

    [Fact]
    public void DeliversEverythingThatFits()
    {
        var buffer = new Term.PendingOutput(1024);
        buffer.Append(Encoding.UTF8.GetBytes("hello "));
        buffer.Append(Encoding.UTF8.GetBytes("world"));

        Assert.Equal("hello world", Encoding.UTF8.GetString(buffer.Take()!));
        Assert.False(buffer.Dropped);
        Assert.Null(buffer.Take());
    }

    [Fact]
    public void StopsGrowingAtTheCapAndKeepsTheNewestBytes()
    {
        var buffer = new Term.PendingOutput(1024);
        buffer.Append(Filler((byte)'A', 600));
        buffer.Append(Filler((byte)'B', 600));
        buffer.Append(Filler((byte)'C', 600));

        // 1800 bytes went in, 1024 may stay.
        Assert.True(buffer.Count <= 1024, $"buffer kept {buffer.Count} bytes");
        Assert.True(buffer.Dropped);

        byte[] kept = buffer.Take()!;
        Assert.Equal((byte)'C', kept[^1]);           // newest output survives
        Assert.DoesNotContain((byte)'A', kept);      // oldest output is gone
    }

    [Fact]
    public void NeverStartsInTheMiddleOfAnEscapeSequence()
    {
        var buffer = new Term.PendingOutput(64);

        // 200 bytes: 140 filler bytes, then an escape sequence, then filler. The naive cut
        // (drop 136) lands inside plain text; the buffer has to move forward to the ESC so
        // the terminal never receives half a sequence.
        var data = new byte[200];
        Array.Fill(data, (byte)'a');
        data[140] = 0x1B;
        data[141] = (byte)'[';
        data[142] = (byte)'3';
        data[143] = (byte)'1';
        data[144] = (byte)'m';
        Array.Fill(data, (byte)'b', 145, data.Length - 145);

        buffer.Append(data);

        // The batch opens with the dropped-output notice; the payload behind it must start
        // on the ESC of the sequence, not four bytes into it.
        string delivered = Encoding.UTF8.GetString(buffer.Take()!);
        const string noticeEndMark = "\u001b[0m\r\n";
        int noticeEnd = delivered.IndexOf(noticeEndMark, StringComparison.Ordinal) + noticeEndMark.Length;
        Assert.True(noticeEnd > 3, "the batch should announce the dropped bytes");
        Assert.StartsWith("\u001b[31m", delivered[noticeEnd..]);
    }

    [Fact]
    public void AnnouncesTheDroppedPartOnce()
    {
        var buffer = new Term.PendingOutput(1024);
        buffer.Append(Filler((byte)'x', 4096));

        string delivered = Encoding.UTF8.GetString(buffer.Take()!);
        Assert.Contains("dropped", delivered);

        // The announcement belongs to the batch that lost data, not to every later drain.
        buffer.Append(Encoding.UTF8.GetBytes("next"));
        Assert.Equal("next", Encoding.UTF8.GetString(buffer.Take()!));
    }
}
