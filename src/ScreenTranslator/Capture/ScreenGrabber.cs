using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using ScreenTranslator.Infrastructure;

namespace ScreenTranslator.Capture;

/// <summary>
/// Takes the frozen snapshot the whole selection flow is built on.
///
/// The snapshot is grabbed once, up front, and everything afterwards — the dimmed
/// overlay the user drags on, and the final crop — is derived from this one bitmap.
/// That is what makes the crop provably aligned with what the user framed: there is no
/// second coordinate system to get wrong, and no DPI conversion anywhere in the path.
/// </summary>
internal static class ScreenGrabber
{
    /// <param name="bounds">Virtual-desktop rectangle in physical pixels.</param>
    public static Bitmap Grab(Rectangle bounds)
    {
        var sw = Stopwatch.StartNew();

        // PArgb is the fastest format for the repeated blits the overlay does.
        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
        try
        {
            using var g = Graphics.FromImage(bitmap);
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }

        Log.Info($"截取虚拟桌面 {bounds.Width}x{bounds.Height} 用时 {sw.ElapsedMilliseconds}ms");
        return bitmap;
    }

    /// <summary>
    /// Pre-computes the darkened copy the overlay paints as its background. Doing this
    /// once beats alpha-blending a full-screen rectangle on every mouse move.
    /// </summary>
    public static Bitmap BuildDimmed(Bitmap source, int alpha)
    {
        var sw = Stopwatch.StartNew();

        var dimmed = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppPArgb);
        try
        {
            using var g = Graphics.FromImage(dimmed);
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            g.DrawImageUnscaled(source, 0, 0);

            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
            using var shade = new SolidBrush(Color.FromArgb(alpha, 0, 0, 0));
            g.FillRectangle(shade, 0, 0, dimmed.Width, dimmed.Height);
        }
        catch
        {
            dimmed.Dispose();
            throw;
        }

        Log.Info($"生成遮罩底图用时 {sw.ElapsedMilliseconds}ms");
        return dimmed;
    }

    /// <summary>
    /// Copies one rectangle out of the frozen snapshot. Explicit nearest-neighbour and
    /// half-pixel-offset modes keep this an exact 1:1 copy — GDI+ defaults can shift a
    /// same-size DrawImage by a pixel.
    /// </summary>
    public static Bitmap Crop(Bitmap source, Rectangle region)
    {
        var safe = Rectangle.Intersect(region, new Rectangle(0, 0, source.Width, source.Height));
        if (safe.Width <= 0 || safe.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(region), "选区超出了截图范围");

        var crop = new Bitmap(safe.Width, safe.Height, PixelFormat.Format32bppArgb);
        try
        {
            using var g = Graphics.FromImage(crop);
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
            g.DrawImage(source, new Rectangle(0, 0, safe.Width, safe.Height), safe, GraphicsUnit.Pixel);
        }
        catch
        {
            crop.Dispose();
            throw;
        }

        return crop;
    }
}
