using System;
using System.Collections.Generic;
using System.Text;

namespace Gui.Term;

/// <summary>
/// Decodes the value of a "text:" keybind action. Ghostty reuses its config string parser
/// there, which is why the value takes Zig string-literal escapes (\n, \r, \t, \\, \',
/// \", \xNN, \u{...}). Like Ghostty, the decoding happens when the binding runs: a bad
/// escape costs one log line and the action is dropped, the config line itself stays valid.
/// </summary>
internal static class ActionText
{
    /// <summary>Decodes <paramref name="text"/> into the bytes that go to the shell.</summary>
    public static bool TryDecode(string text, out byte[] bytes)
    {
        var buffer = new List<byte>(text.Length);
        int index = 0;

        while (index < text.Length)
        {
            int escape = text.IndexOf('\\', index);
            if (escape < 0)
            {
                Append(buffer, text[index..]);
                break;
            }

            if (escape > index) Append(buffer, text[index..escape]);
            if (escape + 1 >= text.Length) return Fail(out bytes);

            char kind = text[escape + 1];
            index = escape + 2;
            switch (kind)
            {
                case 'n': buffer.Add(0x0A); break;
                case 'r': buffer.Add(0x0D); break;
                case 't': buffer.Add(0x09); break;
                case '\\': buffer.Add(0x5C); break;
                case '\'': buffer.Add(0x27); break;
                case '"': buffer.Add(0x22); break;

                case 'x':
                {
                    if (index + 1 >= text.Length) return Fail(out bytes);
                    if (!TryHex(text[index], out int high) || !TryHex(text[index + 1], out int low))
                        return Fail(out bytes);

                    buffer.Add((byte)((high << 4) | low));
                    index += 2;
                    break;
                }

                case 'u':
                {
                    if (index >= text.Length || text[index] != '{') return Fail(out bytes);
                    int close = text.IndexOf('}', index + 1);
                    if (close < 0 || close == index + 1 || close - index - 1 > 6) return Fail(out bytes);

                    int codepoint = 0;
                    for (int i = index + 1; i < close; i++)
                    {
                        if (!TryHex(text[i], out int digit)) return Fail(out bytes);
                        codepoint = codepoint * 16 + digit;
                    }

                    // Surrogates are not codepoints, and Zig's parser rejects them too.
                    if (codepoint > 0x10FFFF || codepoint is >= 0xD800 and <= 0xDFFF) return Fail(out bytes);

                    Append(buffer, char.ConvertFromUtf32(codepoint));
                    index = close + 1;
                    break;
                }

                default:
                    return Fail(out bytes);
            }
        }

        bytes = buffer.ToArray();
        return true;
    }

    /// <summary>
    /// Splits "cursor_key" values: two escape-decoded halves separated by the first comma,
    /// in CursorKey's field order (normal, application).
    /// </summary>
    public static bool TryDecodePair(string value, out byte[] first, out byte[] second)
    {
        first = Array.Empty<byte>();
        second = Array.Empty<byte>();

        int comma = value.IndexOf(',');
        if (comma < 0) return false;

        return TryDecode(value[..comma], out first) && TryDecode(value[(comma + 1)..], out second);
    }

    private static void Append(List<byte> buffer, string run)
    {
        if (run.Length > 0) buffer.AddRange(Encoding.UTF8.GetBytes(run));
    }

    private static bool TryHex(char c, out int value)
    {
        value = c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1,
        };
        return value >= 0;
    }

    private static bool Fail(out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        return false;
    }
}
