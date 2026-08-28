using System.Drawing;
using System.Text;
using System.Windows.Forms;
using ScreenTranslator.Infrastructure;

namespace ScreenTranslator.Capture;

/// <summary>
/// The bounding rectangle of every monitor combined, in physical pixels. This single
/// rectangle is what the overlay window covers and what gets screenshotted, which is
/// why the mask always spans all displays rather than just the primary one.
/// </summary>
internal static class VirtualDesktop
{
    public static Rectangle GetBounds()
    {
        var x = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        var y = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        var w = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        var h = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);

        if (w > 0 && h > 0) return new Rectangle(x, y, w, h);

        // Should not happen, but never hand back an empty rect.
        Log.Warn("GetSystemMetrics 返回的虚拟桌面尺寸无效，回退到 SystemInformation");
        var fallback = SystemInformation.VirtualScreen;
        return fallback.Width > 0 && fallback.Height > 0
            ? fallback
            : Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
    }

    /// <summary>
    /// One log line per monitor, including its scale factor. When a capture ever does
    /// come out misaligned, this is the first thing worth looking at.
    /// </summary>
    public static string Describe()
    {
        var sb = new StringBuilder();
        var bounds = GetBounds();
        sb.Append($"虚拟桌面 {bounds.Width}x{bounds.Height} @ ({bounds.X},{bounds.Y})");

        var index = 0;
        foreach (var screen in Screen.AllScreens)
        {
            index++;
            var b = screen.Bounds;
            var scale = GetScalePercent(b);
            sb.Append($" | 屏{index} {b.Width}x{b.Height} @ ({b.X},{b.Y}) 缩放{scale}%");
            if (screen.Primary) sb.Append(" 主");
        }
        return sb.ToString();
    }

    private static int GetScalePercent(Rectangle monitorBounds)
    {
        try
        {
            // Sample a point inside the monitor rather than its corner, which can land
            // on the neighbouring display.
            var pt = new NativeMethods.POINT(
                monitorBounds.X + monitorBounds.Width / 2,
                monitorBounds.Y + monitorBounds.Height / 2);

            var monitor = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero) return 0;

            if (NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out _) != 0)
                return 0;

            return (int)Math.Round(dpiX * 100.0 / 96.0);
        }
        catch (Exception ex)
        {
            Log.Warn($"读取显示器缩放比例失败：{ex.Message}");
            return 0;
        }
    }
}
