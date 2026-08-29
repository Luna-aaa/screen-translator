using System.Drawing;
using ScreenTranslator.Capture;
using ScreenTranslator.Infrastructure;

namespace ScreenTranslator.Overlay;

/// <summary>
/// One request's worth of a whole-screen translation: a handful of neighbouring blocks,
/// and a crop of the screen containing just them.
/// </summary>
/// <param name="Indexes">Where these blocks sit in the full list, so answers can be put back.</param>
/// <param name="Segments">The texts to translate, in the same order as <paramref name="Indexes"/>.</param>
/// <param name="Region">The crop's rectangle in frozen-bitmap coordinates, for the log and the block map.</param>
/// <param name="Image">
/// A crop this batch owns outright, or null on the text-only route. Owning it is not a
/// detail — see <see cref="SnapshotBatch.Split"/>.
/// </param>
internal sealed record SnapshotBatch(
    IReadOnlyList<int> Indexes,
    IReadOnlyList<string> Segments,
    Rectangle Region,
    Bitmap? Image) : IDisposable
{
    public void Dispose() => Image?.Dispose();
}

/// <summary>
/// Splits a screen's worth of text into several small requests instead of one big one.
///
/// One request for the whole screen was the obvious first design and it lost on every
/// count. Splitting wins three times over:
///
///   · <b>Faster.</b> A model's latency is dominated by how much it writes. Four batches
///     writing a quarter each, sent at the same time, finish in roughly a quarter of the
///     wall time. Measured before this change: a median of 21 seconds, worst case 52.
///
///   · <b>Cheaper and sharper.</b> Each batch carries only the strip of screen its own
///     blocks live in, not the whole desktop. Fewer pixels in total, and what is sent
///     survives the resize at a higher effective resolution — small type stays legible.
///
///   · <b>More reliable.</b> Asked to number forty-five answers, models start merging and
///     dropping them; one real run came back with four. Twelve is a length they keep.
///
/// And it removes a crash. The single-request version handed the one frozen bitmap to a
/// background encoder while the overlay was still painting that same bitmap on the UI
/// thread; GDI+ objects are not thread-safe, so it threw "Object is currently in use
/// elsewhere" — four times in one day of real use. Here every batch is cropped up front,
/// on the calling thread, and owns its copy. Nothing is shared, so nothing can race.
/// </summary>
internal static class SnapshotBatching
{
    /// <summary>
    /// Blocks per request. Small enough that models reliably keep the numbering, large
    /// enough that the per-request overhead (prompt, image, round trip) still pays off.
    /// </summary>
    public const int BlocksPerBatch = 12;

    /// <summary>
    /// How many requests are in flight at once. Enough to cut the wall time substantially,
    /// few enough not to trip a provider's rate limit and turn the whole thing into 429s.
    /// </summary>
    public const int MaxParallel = 3;

    /// <summary>Margin around a batch's crop, so glyphs are not clipped at the edge.</summary>
    private const int CropPadding = 24;

    /// <param name="frozen">
    /// Read on this thread only. Each batch gets its own crop, so callers may keep using
    /// <paramref name="frozen"/> afterwards — including painting it — while the batches
    /// are encoded and sent on other threads.
    /// </param>
    /// <param name="withImage">False on the text-only route, which sends no picture at all.</param>
    public static List<SnapshotBatch> Split(
        IReadOnlyList<TextGroup> groups, Bitmap frozen, bool withImage)
    {
        var batches = new List<SnapshotBatch>();
        var canvas = new Rectangle(0, 0, frozen.Width, frozen.Height);

        // Groups arrive in reading order, which already puts neighbours next to each other.
        // Taking consecutive runs is therefore all the spatial grouping that is needed —
        // no clustering pass, and no risk of it deciding something surprising.
        for (var start = 0; start < groups.Count; start += BlocksPerBatch)
        {
            var count = Math.Min(BlocksPerBatch, groups.Count - start);
            var indexes = new List<int>(count);
            var segments = new List<string>(count);

            var region = Rectangle.Round(groups[start].Bounds);
            for (var i = start; i < start + count; i++)
            {
                indexes.Add(i);
                segments.Add(groups[i].Text);
                region = Rectangle.Union(region, Rectangle.Round(groups[i].Bounds));
            }

            region.Inflate(CropPadding, CropPadding);
            region = Rectangle.Intersect(region, canvas);

            Bitmap? crop = null;
            if (withImage && region.Width > 1 && region.Height > 1)
            {
                try
                {
                    crop = ScreenGrabber.Crop(frozen, region);
                }
                catch (Exception ex)
                {
                    // A batch without its picture still translates, just without the model
                    // being able to correct what OCR misread. Better than losing the batch.
                    Log.Warn($"整屏翻译：第 {batches.Count + 1} 批裁图失败，这批只发文字：{ex.Message}");
                }
            }

            batches.Add(new SnapshotBatch(indexes, segments, region, crop));
        }

        Log.Info($"整屏翻译：{groups.Count} 段分成 {batches.Count} 批"
                 + $"，每批最多 {BlocksPerBatch} 段，最多 {MaxParallel} 批同时发"
                 + (withImage ? $"，图片总像素 {TotalPixels(batches) / 1_000_000.0:0.0}MP" : "，纯文字"));

        return batches;
    }

    private static long TotalPixels(IEnumerable<SnapshotBatch> batches) =>
        batches.Sum(b => (long)(b.Image?.Width ?? 0) * (b.Image?.Height ?? 0));
}
