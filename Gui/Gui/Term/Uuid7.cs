using System;

namespace Gui.Term;

/// <summary>
/// RFC 9562 version 7 UUIDs. .NET 8 predates <c>Guid.CreateVersion7</c>; the layout is the
/// Unix millisecond clock in the leading 48 bits, then version and variant, then randomness,
/// so ids created later sort after earlier ones in UUID text form.
/// </summary>
internal static class Uuid7
{
    public static Guid New()
    {
        long millis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Span<byte> random = stackalloc byte[10];
        Random.Shared.NextBytes(random);

        return new Guid(
            (int)(millis >> 16),
            unchecked((short)millis),
            (short)(0x7000 | (random[0] << 4) | (random[1] >> 4)),
            new byte[]
            {
                (byte)(0x80 | (random[2] & 0x3F)),
                random[3], random[4], random[5], random[6], random[7], random[8], random[9],
            });
    }
}