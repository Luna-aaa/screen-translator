using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using ScreenTranslator.Infrastructure;

namespace ScreenTranslator.Capture;

/// <summary>
/// Runs one capture end to end: freeze the desktop, let the user frame a region,
/// crop it out of the frozen snapshot, optionally save it.
/// </summary>
internal sealed class CaptureService
{
    private bool _busy;

    /// <summary>Must be called on the UI thread.</summary>
    /// <param name="saveToDisk">Whether to keep a PNG of the crop.</param>
    /// <param name="captureDir">Already-resolved folder for saved crops.</param>
    public async Task<CaptureOutcome> CaptureAsync(bool saveToDisk, string captureDir)
    {
        if (_busy)
        {
            Log.Info("已有框选在进行中，忽略这次触发");
            return CaptureOutcome.Busy();
        }

        _busy = true;
        var sw = Stopwatch.StartNew();

        // Whatever the user was working in should get the focus back afterwards; the
        // overlay has to take it while it is up, because it needs the Esc key.
        var previousForeground = NativeMethods.GetForegroundWindow();

        Bitmap? frozen = null;
        try
        {
            var bounds = VirtualDesktop.GetBounds();
            Log.Info(VirtualDesktop.Describe());

            frozen = ScreenGrabber.Grab(bounds);

            Rectangle? selection;
            using (var overlay = new SelectionOverlay(frozen, bounds))
            {
                Log.Info($"遮罩就绪，用时 {sw.ElapsedMilliseconds}ms");
                selection = await overlay.ShowAndWaitAsync().ConfigureAwait(true);
            }

            RestoreForeground(previousForeground);

            if (selection is null)
            {
                Log.Info("用户取消了框选");
                return CaptureOutcome.Cancelled();
            }

            var region = selection.Value;
            var crop = ScreenGrabber.Crop(frozen, region);

            // Same offset the overlay was positioned at, so this is where the user
            // actually drew the box on their desktop.
            var screenRect = new Rectangle(
                bounds.X + region.X, bounds.Y + region.Y, region.Width, region.Height);

            string? savedPath = null;
            if (saveToDisk)
            {
                savedPath = TrySave(crop, captureDir);
            }

            Log.Info($"框选完成：屏幕坐标 {screenRect.X},{screenRect.Y} 尺寸 {region.Width}x{region.Height}"
                     + $"，总用时 {sw.ElapsedMilliseconds}ms"
                     + (savedPath is null ? "" : $"，已保存 {savedPath}"));

            return CaptureOutcome.Success(crop, screenRect, savedPath);
        }
        catch (Exception ex)
        {
            RestoreForeground(previousForeground);
            Log.Error("框选截图失败", ex);
            return CaptureOutcome.Failed(ex.Message);
        }
        finally
        {
            frozen?.Dispose();
            _busy = false;
        }
    }

    private static string? TrySave(Bitmap crop, string captureDir)
    {
        try
        {
            Directory.CreateDirectory(captureDir);
            var path = Path.Combine(captureDir, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}.png");
            crop.Save(path, ImageFormat.Png);
            return path;
        }
        catch (Exception ex)
        {
            // A failed save must not lose the capture itself.
            Log.Error($"保存截图失败（目录 {captureDir}）", ex);
            return null;
        }
    }

    private static void RestoreForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return;
        try
        {
            NativeMethods.SetForegroundWindow(hwnd);
        }
        catch (Exception ex)
        {
            Log.Warn($"恢复原前台窗口失败：{ex.Message}");
        }
    }
}
