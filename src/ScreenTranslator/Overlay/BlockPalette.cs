using System.Drawing;

namespace ScreenTranslator.Overlay;

/// <summary>The two colours a block should be redrawn in: what was behind the text, and the text itself.</summary>
public readonly record struct BlockColors(Color Background, Color Ink);

/// <summary>
/// Works out what colours to redraw a block in by looking at the pixels that were there.
///
/// The alternative — a fixed white box with black text — is legible but turns a dark game
/// UI into a page of sticky notes. Sampling keeps white-on-dark white-on-dark, which is
/// what makes the result read as the same screen rather than as something pasted over it.
/// </summary>
internal static class BlockPalette
{
    /// <summary>Roughly how many pixels to look at per block. Enough to be stable, few enough to be instant.</summary>
    private const int TargetSamples = 400;

    /// <summary>Channel bucket size when finding the most common colour. 16 levels per channel.</summary>
    private const int Quantum = 16;

    public static BlockColors Sample(Bitmap source, Rectangle area)
    {
        var safe = Rectangle.Intersect(area, new Rectangle(0, 0, source.Width, source.Height));
        if (safe.Width <= 1 || safe.Height <= 1)
            return new BlockColors(Color.White, Color.Black);

        var step = Math.Max(1, (int)Math.Sqrt((double)safe.Width * safe.Height / TargetSamples));

        // Modal colour, not mean: averaging text and background together yields the muddy
        // midpoint of the two, which then has no contrast with either.
        var buckets = new Dictionary<int, (long R, long G, long B, int Count)>();
        var samples = new List<Color>(TargetSamples);

        for (var y = safe.Top; y < safe.Bottom; y += step)
        {
            for (var x = safe.Left; x < safe.Right; x += step)
            {
                var c = source.GetPixel(x, y);
                samples.Add(c);

                var key = (c.R / Quantum << 16) | (c.G / Quantum << 8) | (c.B / Quantum);
                var slot = buckets.TryGetValue(key, out var existing) ? existing : default;
                buckets[key] = (slot.R + c.R, slot.G + c.G, slot.B + c.B, slot.Count + 1);
            }
        }

        if (samples.Count == 0) return new BlockColors(Color.White, Color.Black);

        var top = buckets.OrderByDescending(b => b.Value.Count).First().Value;
        var background = Color.FromArgb(
            (int)(top.R / top.Count), (int)(top.G / top.Count), (int)(top.B / top.Count));

        // The ink is whatever sits furthest from the background in brightness — that is
        // the text, by construction, since text is the only thing in a text block that
        // contrasts with its own background.
        var backLuma = Luminance(background);
        var ink = background;
        var best = -1.0;
        foreach (var c in samples)
        {
            var distance = Math.Abs(Luminance(c) - backLuma);
            if (distance > best) { best = distance; ink = c; }
        }

        // Too little contrast to trust (a block of solid colour, or an anti-aliasing-only
        // sample): fall back to plain black or white, whichever the background can carry.
        if (best < 60) ink = backLuma > 140 ? Color.FromArgb(20, 20, 24) : Color.FromArgb(245, 245, 248);

        return new BlockColors(background, Color.FromArgb(255, ink));
    }

    private static double Luminance(Color c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
}
