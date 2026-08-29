using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using ScreenTranslator.Infrastructure;

namespace ScreenTranslator.Overlay;

/// <summary>One block ready to be drawn: where it goes and what to put there.</summary>
/// <param name="Group">The original lines and their geometry.</param>
/// <param name="Translation">The text to draw, or null to leave the original showing.</param>
public sealed record RenderBlock(TextGroup Group, string? Translation);

/// <summary>
/// Paints translations back over the frozen desktop.
///
/// The output is a second full-screen bitmap rather than a tree of controls: the overlay
/// then has nothing to do but blit one image or the other, so switching between original
/// and translation is instant and there is no layout pass that could disagree with the
/// coordinates the OCR gave us.
///
/// Three things are deliberately approximate, and no amount of tuning fixes them — they
/// are properties of the problem, not bugs:
///   · a translation is rarely the same length as its original, so the font shrinks;
///   · text over a photo or a gradient gets a flat patch behind it, because the goal is
///     legible, not invisible;
///   · columns, tables and vertical Japanese are laid out left-to-right like everything
///     else.
/// </summary>
internal static class OverlayRenderer
{
    /// <summary>Below this the text is no longer worth reading; better to overflow the box.</summary>
    private const float MinFontPx = 9.5f;

    private const float MaxFontPx = 64f;

    /// <summary>How far a block may grow downward when the translation will not fit as-is.</summary>
    private const float MaxHeightGrowth = 1.45f;

    /// <summary>Gap left above whatever block comes next, so two paragraphs never touch.</summary>
    private const int BlockGap = 3;

    /// <summary>Breathing room around the drawn text, so glyphs never touch the patch edge.</summary>
    private const int PadX = 3;
    private const int PadY = 2;

    public static Bitmap Render(Bitmap frozen, IReadOnlyList<RenderBlock> blocks)
    {
        var sw = Stopwatch.StartNew();

        var canvas = new Bitmap(frozen.Width, frozen.Height, PixelFormat.Format32bppPArgb);
        try
        {
            using var g = Graphics.FromImage(canvas);
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImageUnscaled(frozen, 0, 0);

            g.CompositingMode = CompositingMode.SourceOver;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            var ceilings = MeasureCeilings(blocks, frozen.Height);

            var drawn = 0;
            for (var i = 0; i < blocks.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(blocks[i].Translation)) continue;
                DrawBlock(g, frozen, blocks[i], ceilings[i]);
                drawn++;
            }

            Log.Info($"原位覆盖绘制 {drawn}/{blocks.Count} 段，用时 {sw.ElapsedMilliseconds}ms");
            return canvas;
        }
        catch
        {
            canvas.Dispose();
            throw;
        }
    }

    /// <summary>
    /// How far down each block is allowed to spill: to just above whatever sits below it.
    ///
    /// Without this, a paragraph whose translation runs long grows into the paragraph
    /// underneath and the two get drawn on top of one another — the blank line between
    /// them disappears and the page turns into a single wall of text. Better to shrink the
    /// type than to eat the neighbour.
    /// </summary>
    private static int[] MeasureCeilings(IReadOnlyList<RenderBlock> blocks, int canvasHeight)
    {
        var ceilings = new int[blocks.Count];

        for (var i = 0; i < blocks.Count; i++)
        {
            var mine = blocks[i].Group.Bounds;
            var limit = canvasHeight;

            for (var j = 0; j < blocks.Count; j++)
            {
                if (i == j) continue;
                var other = blocks[j].Group.Bounds;

                // Only blocks that start below this one and share some of its width can
                // be collided with; something off to the side is not in the way.
                if (other.Top < mine.Bottom) continue;
                var overlap = Math.Min(mine.Right, other.Right) - Math.Max(mine.Left, other.Left);
                if (overlap <= 1) continue;

                limit = Math.Min(limit, (int)other.Top - BlockGap);
            }

            // Never smaller than the words being replaced: the original fitted there, so
            // something of that size has to be allowed to.
            ceilings[i] = Math.Max(limit, (int)Math.Ceiling(mine.Bottom));
        }

        return ceilings;
    }

    private static void DrawBlock(Graphics g, Bitmap frozen, RenderBlock block, int ceiling)
    {
        var original = Rectangle.Round(block.Group.Bounds);
        original.Inflate(PadX, PadY);
        original = Rectangle.Intersect(original, new Rectangle(0, 0, frozen.Width, frozen.Height));
        if (original.Width <= 2 || original.Height <= 2) return;

        var colors = BlockPalette.Sample(frozen, original);

        var (font, needed) = FitFont(g, block.Translation!, block.Group, original, ceiling);
        using (font)
        {
            // The patch covers whatever the text ends up needing, so a translation that
            // grew taller than its original is never drawn half on top of stale words.
            var patch = original;
            var wanted = (int)Math.Ceiling(needed.Height) + PadY * 2 + 2;
            if (wanted > patch.Height) patch.Height = wanted;
            patch = Rectangle.Intersect(patch, new Rectangle(0, 0, frozen.Width, frozen.Height));

            // Fully opaque: at 95% the original glyphs still showed through as a ghost,
            // and light-on-dark text needs only a few percent to stay readable underneath.
            using var back = new SolidBrush(colors.Background);
            g.FillRectangle(back, patch);

            using var ink = new SolidBrush(colors.Ink);
            using var format = BuildFormat();
            var textArea = new RectangleF(
                patch.X + PadX, patch.Y + PadY,
                Math.Max(1, patch.Width - PadX * 2), Math.Max(1, patch.Height - PadY * 2));

            g.DrawString(block.Translation, font, ink, textArea, format);
        }
    }

    /// <summary>
    /// Picks the largest size that fits, starting from the height of the words being
    /// replaced so the page keeps its original visual hierarchy — a heading stays bigger
    /// than the paragraph under it even after both were re-typeset.
    /// </summary>
    private static (Font Font, SizeF Needed) FitFont(
        Graphics g, string text, TextGroup group, Rectangle box, int ceiling)
    {
        // A multi-line group's "line height" is the line pitch, which runs about 1.2x the
        // em size; a single-line group's is just the height of the ink, which runs under
        // it. One factor for both makes every one-line label come out visibly smaller than
        // the words it replaced.
        var factor = group.Lines.Count == 1 ? 1.05f : 0.82f;
        var start = Math.Clamp(group.LineHeight * factor, MinFontPx, MaxFontPx);
        var width = Math.Max(1, box.Width - PadX * 2);

        // Whichever is tighter: the growth allowance, or the room before the next block.
        var maxHeight = Math.Min(box.Height * MaxHeightGrowth, Math.Max(box.Height, ceiling - box.Y));

        using var format = BuildFormat();

        Font? candidate = null;
        var measured = SizeF.Empty;

        for (var size = start; size >= MinFontPx; size -= Math.Max(0.5f, size * 0.08f))
        {
            candidate?.Dispose();
            candidate = BuildFont(size);
            measured = g.MeasureString(text, candidate, width, format);
            if (measured.Height <= maxHeight) return (candidate, measured);
        }

        // Nothing fit even at the floor: draw it at the floor anyway. A slightly overflowing
        // block the user can read beats a perfectly contained one they cannot.
        candidate ??= BuildFont(MinFontPx);
        return (candidate, measured);
    }

    private static Font BuildFont(float pixels) =>
        new("Microsoft YaHei UI", pixels, FontStyle.Regular, GraphicsUnit.Pixel);

    /// <summary>
    /// Word wrap on, trimming off, and deliberately NOT NoClip.
    ///
    /// NoClip lets a long translation run past the patch drawn behind it, straight over
    /// the original words next to it — two sentences overprinted, neither readable. Being
    /// clipped to its own patch is the lesser failure, and the patch grows first.
    /// </summary>
    private static StringFormat BuildFormat() => new(StringFormat.GenericTypographic)
    {
        Alignment = StringAlignment.Near,
        LineAlignment = StringAlignment.Near,
        Trimming = StringTrimming.None,
    };
}
