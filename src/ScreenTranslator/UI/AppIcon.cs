using System.Reflection;
using System.Windows.Media.Imaging;
using ScreenTranslator.Infrastructure;

namespace ScreenTranslator.UI;

/// <summary>
/// Exposes the embedded app.ico to WPF. The .ico is an EmbeddedResource (so WinForms'
/// NotifyIcon can read it as a stream), which means pack:// URIs don't apply — we decode
/// the stream by hand instead.
/// </summary>
public static class AppIcon
{
    private const string ResourceName = "ScreenTranslator.Resources.app.ico";

    private static BitmapSource? _cached;

    public static BitmapSource? WindowIcon => _cached ??= Load();

    private static BitmapSource? Load()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (stream is null) return null;

            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

            // Prefer a frame around 48px: big enough for the title bar and the Alt+Tab
            // card, small enough that Windows isn't downscaling the 256px art every draw.
            var frame = decoder.Frames
                .OrderBy(f => Math.Abs(f.PixelWidth - 48))
                .FirstOrDefault();

            frame?.Freeze();
            return frame;
        }
        catch (Exception ex)
        {
            Log.Error("加载窗口图标失败", ex);
            return null;
        }
    }
}
