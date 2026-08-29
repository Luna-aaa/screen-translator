using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using ScreenTranslator.Infrastructure;

namespace ScreenTranslator.Translate;

/// <summary>Result of preparing a crop for a vision model.</summary>
/// <param name="DataUri">"data:image/png;base64,…", ready to drop into an image_url message part.</param>
/// <param name="Description">One line for the log: what was sent and how big it ended up.</param>
public sealed record EncodedImage(string DataUri, string Description);

/// <summary>
/// Turns a captured crop into something a vision model will read well without costing more
/// than it has to. Vision services bill roughly by pixel count, so both directions matter:
/// a 2560-wide screenshot is shrunk, and a 30-pixel-tall subtitle strip is enlarged,
/// because these models tile their input and a strip thinner than one tile gives the model
/// almost nothing to work with.
/// </summary>
internal static class ImageEncoder
{
    /// <summary>
    /// Below this, the shorter edge is scaled up. Chosen to clear the 28-pixel tile size
    /// these models use by a comfortable margin — a one-line caption should still land on
    /// several rows of tiles.
    /// </summary>
    private const int MinShortEdge = 224;

    /// <summary>Never enlarge past this multiple; upscaling invents nothing, it only costs.</summary>
    private const double MaxUpscale = 3.0;

    /// <summary>
    /// PNG is preferred because screen text is exactly what it compresses best, and JPEG
    /// artefacts around small glyphs are the last thing a reader-model needs. Past this
    /// size the crop is photographic enough that PNG has stopped being a good deal.
    /// </summary>
    private const int PngSizeLimitBytes = 1_200_000;

    private const long JpegQuality = 88L;

    public static EncodedImage Encode(Bitmap source, int maxEdge)
    {
        maxEdge = Math.Clamp(maxEdge, 640, 3200);

        var scale = ScaleFor(source.Width, source.Height, maxEdge);
        using Bitmap? prepared = scale == 1.0 ? null : Resize(source, scale);
        var image = prepared ?? source;

        var png = ToBytes(image, ImageFormat.Png);
        if (png.Length <= PngSizeLimitBytes)
        {
            return Describe(png, "image/png", source, image, scale);
        }

        var jpeg = ToJpeg(image);
        // A pathological crop (a photo with a huge flat area) can compress worse as JPEG
        // than as PNG; send whichever actually came out smaller.
        return jpeg.Length < png.Length
            ? Describe(jpeg, "image/jpeg", source, image, scale)
            : Describe(png, "image/png", source, image, scale);
    }

    private static EncodedImage Describe(byte[] bytes, string mime, Bitmap source, Image sent, double scale)
    {
        var description =
            $"{source.Width}×{source.Height} → {sent.Width}×{sent.Height}"
            + $"（{(scale > 1 ? "放大" : scale < 1 ? "缩小" : "原尺寸")} {scale:0.##}×）"
            + $"，{mime}，{bytes.Length / 1024.0:0.#} KB";

        Log.Info($"看图直翻：图片 {description}");
        return new EncodedImage($"data:{mime};base64,{Convert.ToBase64String(bytes)}", description);
    }

    /// <summary>
    /// Shrinking wins over enlarging when both apply — an image can be both very wide and
    /// very short, and blowing it up to fix the height would blow the width past the cap.
    /// </summary>
    internal static double ScaleFor(int width, int height, int maxEdge)
    {
        var longEdge = Math.Max(width, height);
        if (longEdge > maxEdge) return (double)maxEdge / longEdge;

        var shortEdge = Math.Min(width, height);
        if (shortEdge <= 0) return 1.0;
        if (shortEdge >= MinShortEdge) return 1.0;

        var up = Math.Min((double)MinShortEdge / shortEdge, MaxUpscale);
        // Do not let the fix for one edge push the other past the budget.
        if (longEdge * up > maxEdge) up = (double)maxEdge / longEdge;
        return up <= 1.0 ? 1.0 : up;
    }

    private static Bitmap Resize(Bitmap source, double scale)
    {
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));

        var target = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(target);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.DrawImage(source, new Rectangle(0, 0, width, height));
        return target;
    }

    private static byte[] ToBytes(Image image, ImageFormat format)
    {
        using var stream = new MemoryStream();
        image.Save(stream, format);
        return stream.ToArray();
    }

    private static byte[] ToJpeg(Image image)
    {
        var codec = ImageCodecInfo.GetImageEncoders()
            .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
        if (codec is null) return ToBytes(image, ImageFormat.Jpeg);

        using var parameters = new EncoderParameters(1);
        using var quality = new EncoderParameter(Encoder.Quality, JpegQuality);
        parameters.Param[0] = quality;

        using var stream = new MemoryStream();
        image.Save(stream, codec, parameters);
        return stream.ToArray();
    }

    /// <summary>
    /// A small picture of text, for "测试连接" on the vision route. Round-tripping this
    /// proves the model can actually see — a text-only model answers the prompt without
    /// the image and looks like it passed.
    /// </summary>
    public static Bitmap BuildProbeImage()
    {
        var bitmap = new Bitmap(420, 150, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.White);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        using var font = new Font("Segoe UI", 26f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color.Black);
        g.DrawString("Good morning.", font, brush, new PointF(20, 24));
        g.DrawString("The sky is blue.", font, brush, new PointF(20, 76));

        return bitmap;
    }
}
