using System;

namespace Gui.Term;

/// <summary>How thick one arm of a box-drawing glyph is.</summary>
public enum BoxWeight : byte
{
    None = 0,
    Light = 1,
    Heavy = 2,
    Double = 3,
}

/// <summary>The diagonal strokes U+2571..U+2573 use instead of arms.</summary>
public enum BoxDiagonal : byte
{
    None = 0,
    Forward = 1,   // ╱
    Backward = 2,  // ╲
    Cross = 3,     // ╳
}

/// <summary>
/// What one cell of the box-drawing block (U+2500..U+257F) is made of: an arm toward each
/// edge, or a diagonal. The renderer turns this into rectangles, so the strokes land exactly
/// on the cell edges and "adjust-box-thickness" has something to scale.
/// </summary>
public readonly record struct BoxSpec(
    BoxWeight Left,
    BoxWeight Right,
    BoxWeight Up,
    BoxWeight Down,
    int Dashes = 0,
    bool Rounded = false,
    BoxDiagonal Diagonal = BoxDiagonal.None)
{
    /// <summary>True when the horizontal arm is the double line (two parallel strokes).</summary>
    public bool HorizontalDouble => Left == BoxWeight.Double || Right == BoxWeight.Double;

    /// <summary>True when the vertical arm is the double line.</summary>
    public bool VerticalDouble => Up == BoxWeight.Double || Down == BoxWeight.Double;
}

/// <summary>
/// The box-drawing block, described rather than drawn. Ghostty renders these characters with
/// its own geometry instead of the font's glyphs, which is what lets it offer a thickness
/// adjustment; this table is the same idea. Every character of the block is covered, so a
/// terminal that draws its boxes with them looks the same here.
/// </summary>
public static class BoxDrawing
{
    private const BoxWeight N = BoxWeight.None;
    private const BoxWeight L = BoxWeight.Light;
    private const BoxWeight H = BoxWeight.Heavy;
    private const BoxWeight D = BoxWeight.Double;

    public static bool TryGet(char rune, out BoxSpec spec)
    {
        spec = rune switch
        {
            '\u2500' => new BoxSpec(L, L, N, N),
            '\u2501' => new BoxSpec(H, H, N, N),
            '\u2502' => new BoxSpec(N, N, L, L),
            '\u2503' => new BoxSpec(N, N, H, H),
            '\u2504' => new BoxSpec(L, L, N, N, Dashes: 3),
            '\u2505' => new BoxSpec(H, H, N, N, Dashes: 3),
            '\u2506' => new BoxSpec(N, N, L, L, Dashes: 3),
            '\u2507' => new BoxSpec(N, N, H, H, Dashes: 3),
            '\u2508' => new BoxSpec(L, L, N, N, Dashes: 4),
            '\u2509' => new BoxSpec(H, H, N, N, Dashes: 4),
            '\u250A' => new BoxSpec(N, N, L, L, Dashes: 4),
            '\u250B' => new BoxSpec(N, N, H, H, Dashes: 4),

            '\u250C' => new BoxSpec(N, L, N, L),
            '\u250D' => new BoxSpec(N, H, N, L),
            '\u250E' => new BoxSpec(N, L, N, H),
            '\u250F' => new BoxSpec(N, H, N, H),
            '\u2510' => new BoxSpec(L, N, N, L),
            '\u2511' => new BoxSpec(H, N, N, L),
            '\u2512' => new BoxSpec(L, N, N, H),
            '\u2513' => new BoxSpec(H, N, N, H),
            '\u2514' => new BoxSpec(N, L, L, N),
            '\u2515' => new BoxSpec(N, H, L, N),
            '\u2516' => new BoxSpec(N, L, H, N),
            '\u2517' => new BoxSpec(N, H, H, N),
            '\u2518' => new BoxSpec(L, N, L, N),
            '\u2519' => new BoxSpec(H, N, L, N),
            '\u251A' => new BoxSpec(L, N, H, N),
            '\u251B' => new BoxSpec(H, N, H, N),

            '\u251C' => new BoxSpec(N, L, L, L),
            '\u251D' => new BoxSpec(N, H, L, L),
            '\u251E' => new BoxSpec(N, L, H, L),
            '\u251F' => new BoxSpec(N, L, L, H),
            '\u2520' => new BoxSpec(N, L, H, H),
            '\u2521' => new BoxSpec(N, H, H, L),
            '\u2522' => new BoxSpec(N, H, L, H),
            '\u2523' => new BoxSpec(N, H, H, H),
            '\u2524' => new BoxSpec(L, N, L, L),
            '\u2525' => new BoxSpec(H, N, L, L),
            '\u2526' => new BoxSpec(L, N, H, L),
            '\u2527' => new BoxSpec(L, N, L, H),
            '\u2528' => new BoxSpec(L, N, H, H),
            '\u2529' => new BoxSpec(H, N, H, L),
            '\u252A' => new BoxSpec(H, N, L, H),
            '\u252B' => new BoxSpec(H, N, H, H),

            '\u252C' => new BoxSpec(L, L, N, L),
            '\u252D' => new BoxSpec(H, L, N, L),
            '\u252E' => new BoxSpec(L, H, N, L),
            '\u252F' => new BoxSpec(H, H, N, L),
            '\u2530' => new BoxSpec(L, L, N, H),
            '\u2531' => new BoxSpec(H, L, N, H),
            '\u2532' => new BoxSpec(L, H, N, H),
            '\u2533' => new BoxSpec(H, H, N, H),
            '\u2534' => new BoxSpec(L, L, L, N),
            '\u2535' => new BoxSpec(H, L, L, N),
            '\u2536' => new BoxSpec(L, H, L, N),
            '\u2537' => new BoxSpec(H, H, L, N),
            '\u2538' => new BoxSpec(L, L, H, N),
            '\u2539' => new BoxSpec(H, L, H, N),
            '\u253A' => new BoxSpec(L, H, H, N),
            '\u253B' => new BoxSpec(H, H, H, N),

            '\u253C' => new BoxSpec(L, L, L, L),
            '\u253D' => new BoxSpec(H, L, L, L),
            '\u253E' => new BoxSpec(L, H, L, L),
            '\u253F' => new BoxSpec(H, H, L, L),
            '\u2540' => new BoxSpec(L, L, H, L),
            '\u2541' => new BoxSpec(L, L, L, H),
            '\u2542' => new BoxSpec(L, L, H, H),
            '\u2543' => new BoxSpec(H, L, H, L),
            '\u2544' => new BoxSpec(L, H, H, L),
            '\u2545' => new BoxSpec(H, L, L, H),
            '\u2546' => new BoxSpec(L, H, L, H),
            '\u2547' => new BoxSpec(H, H, H, L),
            '\u2548' => new BoxSpec(H, H, L, H),
            '\u2549' => new BoxSpec(H, L, H, H),
            '\u254A' => new BoxSpec(L, H, H, H),
            '\u254B' => new BoxSpec(H, H, H, H),

            '\u254C' => new BoxSpec(L, L, N, N, Dashes: 2),
            '\u254D' => new BoxSpec(H, H, N, N, Dashes: 2),
            '\u254E' => new BoxSpec(N, N, L, L, Dashes: 2),
            '\u254F' => new BoxSpec(N, N, H, H, Dashes: 2),

            '\u2550' => new BoxSpec(D, D, N, N),
            '\u2551' => new BoxSpec(N, N, D, D),

            '\u2552' => new BoxSpec(N, D, N, L),
            '\u2553' => new BoxSpec(N, L, N, D),
            '\u2554' => new BoxSpec(N, D, N, D),
            '\u2555' => new BoxSpec(D, N, N, L),
            '\u2556' => new BoxSpec(L, N, N, D),
            '\u2557' => new BoxSpec(D, N, N, D),
            '\u2558' => new BoxSpec(N, D, L, N),
            '\u2559' => new BoxSpec(N, L, D, N),
            '\u255A' => new BoxSpec(N, D, D, N),
            '\u255B' => new BoxSpec(D, N, L, N),
            '\u255C' => new BoxSpec(L, N, D, N),
            '\u255D' => new BoxSpec(D, N, D, N),

            '\u255E' => new BoxSpec(N, D, L, L),
            '\u255F' => new BoxSpec(N, L, D, D),
            '\u2560' => new BoxSpec(N, D, D, D),
            '\u2561' => new BoxSpec(D, N, L, L),
            '\u2562' => new BoxSpec(L, N, D, D),
            '\u2563' => new BoxSpec(D, N, D, D),

            '\u2564' => new BoxSpec(D, D, N, L),
            '\u2565' => new BoxSpec(L, L, N, D),
            '\u2566' => new BoxSpec(D, D, N, D),
            '\u2567' => new BoxSpec(D, D, L, N),
            '\u2568' => new BoxSpec(L, L, D, N),
            '\u2569' => new BoxSpec(D, D, D, N),
            '\u256A' => new BoxSpec(D, D, L, L),
            '\u256B' => new BoxSpec(L, L, D, D),
            '\u256C' => new BoxSpec(D, D, D, D),

            '\u256D' => new BoxSpec(N, L, N, L, Rounded: true),
            '\u256E' => new BoxSpec(L, N, N, L, Rounded: true),
            '\u256F' => new BoxSpec(L, N, L, N, Rounded: true),
            '\u2570' => new BoxSpec(N, L, L, N, Rounded: true),

            '\u2571' => new BoxSpec(N, N, N, N, Diagonal: BoxDiagonal.Forward),
            '\u2572' => new BoxSpec(N, N, N, N, Diagonal: BoxDiagonal.Backward),
            '\u2573' => new BoxSpec(N, N, N, N, Diagonal: BoxDiagonal.Cross),

            '\u2574' => new BoxSpec(L, N, N, N),
            '\u2575' => new BoxSpec(N, N, L, N),
            '\u2576' => new BoxSpec(N, L, N, N),
            '\u2577' => new BoxSpec(N, N, N, L),
            '\u2578' => new BoxSpec(H, N, N, N),
            '\u2579' => new BoxSpec(N, N, H, N),
            '\u257A' => new BoxSpec(N, H, N, N),
            '\u257B' => new BoxSpec(N, N, N, H),
            '\u257C' => new BoxSpec(L, H, N, N),
            '\u257D' => new BoxSpec(N, N, L, H),
            '\u257E' => new BoxSpec(H, L, N, N),
            '\u257F' => new BoxSpec(N, N, H, L),

            _ => default,
        };

        return rune is >= '\u2500' and <= '\u257F';
    }
}
