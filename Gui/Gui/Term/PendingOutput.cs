using System;
using System.Collections.Generic;

namespace Gui.Term;

/// <summary>
/// Output read from the pty before a pane has taken it. Normally a pane drains it on the
/// next UI turn; while a tab is being handed to another window the owner is null for a
/// moment and everything the shell writes piles up in here.
///
/// A session whose handover failed never gets an owner again, so the buffer is capped:
/// beyond <see cref="Cap"/> the oldest bytes are dropped and the loss is announced once in
/// the next batch. Without the cap such a session would grow for the life of the process.
/// </summary>
internal sealed class PendingOutput
{
    public const int DefaultCap = 8 * 1024 * 1024;

    /// <summary>Prefix put in front of the first batch that lost bytes.</summary>
    private static readonly byte[] DroppedNotice =
        "\r\n\u001b[2m[older output dropped]\u001b[0m\r\n"u8.ToArray();

    /// <summary>Capacity below which a drained buffer is not worth reallocating.</summary>
    private const int KeepCapacity = 256 * 1024;

    private readonly List<byte> _bytes = new(64 * 1024);

    public PendingOutput(int cap = DefaultCap)
    {
        if (cap <= 0) throw new ArgumentOutOfRangeException(nameof(cap));
        Cap = cap;
    }

    public int Cap { get; }

    public int Count => _bytes.Count;

    /// <summary>True once output had to be thrown away to stay under <see cref="Cap"/>.</summary>
    public bool Dropped { get; private set; }

    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        _bytes.AddRange(data);

        int overflow = _bytes.Count - Cap;
        if (overflow <= 0) return;

        _bytes.RemoveRange(0, Resync(overflow));
        Dropped = true;
    }

    /// <summary>The buffered bytes, or null when nothing is pending.</summary>
    public byte[]? Take()
    {
        if (_bytes.Count == 0) return null;

        byte[] body = _bytes.ToArray();
        _bytes.Clear();
        // A single burst must not keep its peak allocation for the rest of the session.
        if (_bytes.Capacity > KeepCapacity) _bytes.Capacity = KeepCapacity;

        if (!Dropped) return body;

        Dropped = false;
        byte[] batch = new byte[DroppedNotice.Length + body.Length];
        DroppedNotice.CopyTo(batch, 0);
        body.CopyTo(batch, DroppedNotice.Length);
        return batch;
    }

    /// <summary>
    /// Moves the cut to the next ESC so the terminal is never handed the tail of an escape
    /// sequence, which would be parsed as text or swallow the output that follows.
    /// </summary>
    private int Resync(int cut)
    {
        // Only look a little way forward: a payload without any escape sequence simply keeps
        // its plain-text cut, and a long scan per read would cost more than it saves.
        const int Window = 4096;
        int limit = Math.Min(_bytes.Count, cut + Window);
        for (int i = cut; i < limit; i++)
        {
            if (_bytes[i] == 0x1B) return i;
        }
        return Math.Min(cut, _bytes.Count);
    }
}
