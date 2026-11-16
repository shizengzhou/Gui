using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Windows.Storage.Streams;
using Windows.UI;

namespace Gui.Term;

/// <summary>
/// Renders the font browser's sample with Win2D, the same Win2D/DirectWrite pipeline the pane
/// draws its cells with. The CLI asks for it over IPC and shows the PNG through the terminal's
/// image protocol, so the preview has the pane's own outlines, hinting and antialiasing instead
/// of a second rasterizer's approximation.
/// </summary>
internal static class SampleImage
{
    /// <summary>The sample kitty's chooser shows, on three lines.</summary>
    private static readonly string[] Lines =
    {
        "abcdefghijklmnopqrstuvwxyz 0123456789",
        "ABCDEFGHIJKLMNOPQRSTUVWXYZ",
        "!\"#$%&'()*+,-./:;<=>?@[\\]^_\u0060{|}~",
    };

    /// <summary>
    /// Writes the sample for one family, sized so a glyph advances exactly one cell, to
    /// <paramref name="output"/>. Returns an error message, or null when the PNG is written.
    /// </summary>
    public static string? Render(string family, int cellWidth, int cellHeight, string? foreground,
        string output)
    {
        if (cellWidth <= 0 || cellHeight <= 0) return "render-sample needs a cell size";

        Color color = ParseColor(foreground) ?? Color.FromArgb(255, 245, 245, 247);

        CanvasDevice device = CanvasDevice.GetSharedDevice();
        using var format = new CanvasTextFormat
        {
            FontFamily = family,
            FontSize = cellHeight * 0.8f,
            WordWrapping = CanvasWordWrapping.NoWrap,
            HorizontalAlignment = CanvasHorizontalAlignment.Left,
            VerticalAlignment = CanvasVerticalAlignment.Top,
        };

        // Size the text so one glyph advances exactly one cell: the pane's grid is what makes
        // the sample read like terminal output rather than a caption.
        float em;
        float lineHeight;
        using (var probe = new CanvasTextLayout(device, "M", format, 0, 0))
        {
            float advance = (float)probe.LayoutBounds.Width;
            if (advance <= 0) return "render-sample could not measure the font";
            em = format.FontSize * cellWidth / advance;
        }
        format.FontSize = em;

        using (var measured = new CanvasTextLayout(device, "Mg", format, 0, 0))
            lineHeight = (float)measured.LayoutBounds.Height;

        int longest = 0;
        foreach (string line in Lines) longest = Math.Max(longest, line.Length);

        const int pad = 2;
        int width = longest * cellWidth + pad * 2;
        int height = Lines.Length * cellHeight + pad * 2;
        float top = Math.Max(0f, (cellHeight - lineHeight) / 2);

        using var target = new CanvasRenderTarget(device, width, height, 96);
        using (CanvasDrawingSession ds = target.CreateDrawingSession())
        {
            ds.Units = CanvasUnits.Pixels;
            ds.Clear(Colors.Transparent);
            for (int row = 0; row < Lines.Length; row++)
            {
                string line = Lines[row];
                for (int col = 0; col < line.Length; col++)
                {
                    if (line[col] == ' ') continue;
                    ds.DrawText(line.Substring(col, 1), pad + col * cellWidth,
                        pad + row * cellHeight + top, color, format);
                }
            }
        }

        try
        {
            using var stream = new InMemoryRandomAccessStream();
            target.SaveAsync(stream, CanvasBitmapFileFormat.Png).AsTask().GetAwaiter().GetResult();
            stream.Seek(0);

            var bytes = new byte[stream.Size];
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            reader.LoadAsync((uint)stream.Size).AsTask().GetAwaiter().GetResult();
            reader.ReadBytes(bytes);

            string? directory = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllBytes(output, bytes);
            return null;
        }
        catch (Exception error)
        {
            return $"render-sample failed: {error.Message}";
        }
    }

    private static Color? ParseColor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string hex = text.Trim().TrimStart('#');
        if (hex.Length != 6) return null;
        try
        {
            return Color.FromArgb(255,
                Convert.ToByte(hex[..2], 16),
                Convert.ToByte(hex.Substring(2, 2), 16),
                Convert.ToByte(hex.Substring(4, 2), 16));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
