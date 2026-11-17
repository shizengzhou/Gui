using System;

namespace Gui.Term;

/// <summary>
/// An OpenType feature tag in the form Win2D's CanvasTypographyFeatureName stores it: the four
/// characters little-endian, which is what its own members hold (ContextualAlternates is
/// 0x746C6163 for "calt"). Its projection turns that value back into the tag DirectWrite
/// compares against, so a tag the enum has no member for — cv01..cv99 — can be passed the same
/// way. The packing lives here so a test can pin the byte order: with the bytes the other way
/// round DirectWrite receives a byte-swapped tag and silently ignores the feature.
/// </summary>
internal static class FontFeatureTag
{
    /// <summary>Four printable ASCII characters: the shape every OpenType feature tag has.</summary>
    public static bool IsTag(string text)
    {
        if (text.Length != 4) return false;
        foreach (char c in text)
        {
            if (c < 0x20 || c > 0x7E) return false;
        }
        return true;
    }

    /// <summary>The tag as Win2D stores it: "calt" is 0x746C6163, not 0x63616C74.</summary>
    public static uint Pack(string tag) =>
        ((uint)tag[3] << 24) | ((uint)tag[2] << 16) | ((uint)tag[1] << 8) | tag[0];
}
