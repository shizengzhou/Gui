using System;
using System.Globalization;
using Gui.Term;
using Xunit;

namespace Gui.LogicTests;

/// <summary>Covers the session id: RFC 9562 version 7, so it sorts by creation time.</summary>
public class Uuid7Tests
{
    [Fact]
    public void SetsVersion7AndTheRfcVariant()
    {
        string text = Uuid7.New().ToString("D");

        Assert.Equal('7', text[14]);
        Assert.True("89ab".Contains(text[19]));
    }

    [Fact]
    public void PutsTheCreationTimeInTheLeadingBits()
    {
        long before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Guid id = Uuid7.New();
        long after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        Assert.InRange(Timestamp(id), before, after);
    }

    [Fact]
    public void TimestampsDoNotGoBackwards()
    {
        Assert.True(Timestamp(Uuid7.New()) <= Timestamp(Uuid7.New()));
    }

    private static long Timestamp(Guid id)
    {
        string text = id.ToString("D");
        return long.Parse(text[..8] + text[9..13], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }
}