using System;
using System.Collections.Generic;

namespace Gui.Term;

/// <summary>
/// "osc-color-report-format" for the replies libghostty-vt emits. The C API always answers
/// OSC 4/10/11/12 queries with 16-bit components and exposes no option to change that, so the
/// bytes are rewritten on their way to the pty: "none" drops the replies and "8-bit" scales
/// each component back to its high byte. Any other value passes the payload through.
/// </summary>
internal static class ColorReport
{
    public const string SixteenBit = "16-bit";
    public const string EightBit = "8-bit";
    public const string None = "none";

    /// <summary>The value this app and Ghostty share; anything unknown falls back to 16-bit.</summary>
    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        None => None,
        EightBit => EightBit,
        _ => SixteenBit,
    };

    /// <summary>
    /// Returns the bytes to hand to the pty, or null when the whole payload is suppressed.
    /// The input instance comes back when no reply needed rewriting.
    /// </summary>
    public static byte[]? Apply(byte[] data, string format)
    {
        bool suppress = format == None;
        bool downscale = format == EightBit;
        if (!suppress && !downscale) return data;

        List<byte>? output = null;
        int copied = 0;
        int index = 0;

        while (index + 1 < data.Length)
        {
            if (data[index] != 0x1B || data[index + 1] != 0x5D)
            {
                index++;
                continue;
            }

            // OSC <command> ; <payload> (BEL | ESC \)
            int commandStart = index + 2;
            int semicolon = Array.IndexOf(data, (byte)';', commandStart);
            if (semicolon < 0) break;
            if (!IsColorCommand(data, commandStart, semicolon) || !IsColorReply(data, semicolon))
            {
                index = semicolon + 1;
                continue;
            }

            int end = FindTerminator(data, semicolon + 1, out int endLength);
            if (end < 0) break;

            output ??= new List<byte>(data.Length);
            output.AddRange(new ArraySegment<byte>(data, copied, index - copied));
            if (!suppress)
            {
                output.AddRange(new ArraySegment<byte>(data, index, 2));
                output.AddRange(new ArraySegment<byte>(data, commandStart, semicolon - commandStart + 1));
                AppendDownscaled(output, data.AsSpan(semicolon + 1, end - semicolon - 1));
                output.AddRange(new ArraySegment<byte>(data, end, endLength));
            }

            copied = end + endLength;
            index = copied;
        }

        if (output is null) return data;
        output.AddRange(new ArraySegment<byte>(data, copied, data.Length - copied));
        return output.Count == 0 ? null : output.ToArray();
    }

    private static bool IsColorCommand(byte[] data, int start, int end)
    {
        if (end - start != 1 && end - start != 2) return false;
        int value = 0;
        for (int i = start; i < end; i++)
        {
            if (data[i] < (byte)'0' || data[i] > (byte)'9') return false;
            value = value * 10 + (data[i] - '0');
        }
        return value is 4 or 10 or 11 or 12;
    }

    /// <summary>A reply carries "rgb:"; the setters and other OSC 4 traffic do not.</summary>
    private static bool IsColorReply(byte[] data, int semicolon)
    {
        for (int i = semicolon + 1; i + 3 < data.Length; i++)
        {
            if (data[i] == 0x07 || (data[i] == 0x1B && data[i + 1] == 0x5C)) return false;
            if (data[i] is (byte)'r' or (byte)'R' &&
                data[i + 1] is (byte)'g' or (byte)'G' &&
                data[i + 2] is (byte)'b' or (byte)'B' &&
                data[i + 3] == (byte)':')
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Index of BEL or ST, or -1 when the sequence runs past the buffer.</summary>
    private static int FindTerminator(byte[] data, int start, out int length)
    {
        length = 0;
        for (int i = start; i < data.Length; i++)
        {
            if (data[i] == 0x07)
            {
                length = 1;
                return i;
            }
            if (data[i] == 0x1B && i + 1 < data.Length && data[i + 1] == 0x5C)
            {
                length = 2;
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Copies a payload, shrinking each `rgb:hhhh/hhhh/hhhh` group to `rgb:hh/hh/hh`. Groups
    /// that are not four hex digits each are copied untouched.
    /// </summary>
    private static void AppendDownscaled(List<byte> output, ReadOnlySpan<byte> payload)
    {
        int index = 0;
        while (index < payload.Length)
        {
            if (!StartsWithRgb(payload, index))
            {
                output.Add(payload[index++]);
                continue;
            }

            output.Add((byte)'r');
            output.Add((byte)'g');
            output.Add((byte)'b');
            output.Add((byte)':');
            index += 4;

            for (int component = 0; component < 3 && index < payload.Length; component++)
            {
                int start = index;
                while (index < payload.Length && payload[index] != (byte)'/') index++;
                ReadOnlySpan<byte> digits = payload[start..index];
                if (digits.Length == 4 && IsHex(digits[0]) && IsHex(digits[1]) && IsHex(digits[2]) && IsHex(digits[3]))
                {
                    output.Add(digits[0]);
                    output.Add(digits[1]);
                }
                else
                {
                    foreach (byte digit in digits) output.Add(digit);
                }
                if (index < payload.Length && payload[index] == (byte)'/') output.Add(payload[index++]);
            }
        }
    }

    private static bool StartsWithRgb(ReadOnlySpan<byte> payload, int index) =>
        index + 3 < payload.Length &&
        payload[index] is (byte)'r' or (byte)'R' &&
        payload[index + 1] is (byte)'g' or (byte)'G' &&
        payload[index + 2] is (byte)'b' or (byte)'B' &&
        payload[index + 3] == (byte)':';

    private static bool IsHex(byte value) =>
        value is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';
}
