using System.Diagnostics;
using System.Drawing;
using ScreenTranslator.Capture;
using ScreenTranslator.Config;
using ScreenTranslator.Infrastructure;
using ScreenTranslator.Ocr;

namespace ScreenTranslator.Overlay;

/// <summary>
/// Runs one whole-screen translation snapshot end to end and puts it on screen.
///
/// The hybrid the plan calls for, and the reason this is not simply "the picture route
/// over the whole screen": a model that reads pictures will not tell you reliably where
/// on the screen it read something, and drawing a translation in the wrong place is worse
/// than not drawing it. So OCR is asked for one thing only — where the text is — while
/// the model is asked for the thing it is good at, which is what the text means. Whether
/// OCR read the characters correctly stops mattering, because the model gets the picture.
///
/// It is a snapshot, not a live layer. The moment the user scrolls, what is underneath is
/// stale, which is exactly why it dismisses on any key or click.
///
/// The pipeline itself lives in <see cref="SnapshotPipeline"/>; this class owns the
/// window, the frozen bitmap and the foreground dance around them.
/// </summary>
internal sealed class SnapshotService
{
    private readonly IOcrLayoutProvider _ocr;
    private bool _busy;

    public SnapshotService(IOcrLayoutProvider ocr) => _ocr = ocr;

    /// <summary>True while a snapshot is on screen, so the hotkey cannot stack two of them.</summary>
    public bool IsBusy => _busy;

    /// <summary>Must be called on the UI thread.</summary>
    public async Task RunAsync(AppConfig config)
    {
        if (_busy)
        {
            Log.Info("已有整屏翻译在进行中，忽略这次触发");
            return;
        }

        _busy = true;
        var sw = Stopwatch.StartNew();
        var previousForeground = NativeMethods.GetForegroundWindow();

        Bitmap? frozen = null;
        SnapshotOverlay? overlay = null;

        try
        {
            var bounds = VirtualDesktop.GetBounds();
            frozen = ScreenGrabber.Grab(bounds);

            overlay = new SnapshotOverlay(frozen, bounds);
            var closed = overlay.ShowAndWaitAsync();

            await FillAsync(overlay, frozen, config, sw).ConfigureAwait(true);

            // The overlay stays up until the user dismisses it; everything above only
            // decided what it should be showing.
            await closed.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Log.Info("整屏翻译被取消");
        }
        catch (Exception ex)
        {
            Log.Error("整屏翻译失败", ex);
            overlay?.SetFailure($"出错了：{ex.Message}");
        }
        finally
        {
            overlay?.Dispose();
            frozen?.Dispose();
            RestoreForeground(previousForeground);
            _busy = false;
            Log.Info($"整屏翻译结束，总用时 {sw.ElapsedMilliseconds}ms");
        }
    }

    /// <summary>Everything between "the screen is frozen" and "the overlay has an answer".</summary>
    private async Task FillAsync(SnapshotOverlay overlay, Bitmap frozen, AppConfig config, Stopwatch sw)
    {
        var token = overlay.Lifetime;
        var (translator, withImage) = SnapshotPipeline.ChooseTranslator(config);

        var result = await SnapshotPipeline
            .RunAsync(frozen, config, _ocr, translator, withImage, overlay.SetStatus, token)
            .ConfigureAwait(true);

        if (token.IsCancellationRequested || result.Status == SnapshotStatus.Cancelled) return;

        if (!result.IsSuccess)
        {
            overlay.SetFailure(result.Message);
            return;
        }

        var rendered = OverlayRenderer.Render(frozen, result.Blocks);
        overlay.SetResult(rendered, result.Blocks, Paths.ResolveSnapshotDir(config.Snapshot.CaptureDirectory));

        var missing = result.Blocks.Count - result.Answered;
        var note = missing > 0 ? $"　·　{missing} 段没翻出来，保留原文" : "";
        overlay.SetStatus(
            $"整屏翻译完成　·　{result.Answered} 段　·　{sw.ElapsedMilliseconds / 1000.0:0.0} 秒{note}");
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
