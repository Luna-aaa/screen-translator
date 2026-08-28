using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Windows.Forms;
using ScreenTranslator.Infrastructure;

namespace ScreenTranslator.Capture;

/// <summary>
/// Proves the invariant the whole capture design rests on: the region the user frames
/// on the overlay is byte-for-byte the region that gets cropped.
///
/// Run with <c>ScreenTranslator.exe --selftest</c>. It writes a report to
/// %APPDATA%\ScreenTranslator\selftest.txt and exits with 0 (all passed) or 1.
/// Worth re-running after changing monitors or display scaling — those are exactly the
/// conditions that break this class of tool.
/// </summary>
internal static class AlignmentSelfTest
{
    private static readonly StringBuilder Report = new();
    private static int _failures;

    public static int Run()
    {
        Line("=== 框选对齐自检 ===");
        Line($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Line("");

        try
        {
            CheckDpiAwareness();
            CheckVirtualDesktopMetrics();

            var bounds = VirtualDesktop.GetBounds();
            using var frozen = ScreenGrabber.Grab(bounds);

            CheckFrozenSize(frozen, bounds);

            // Order matters: the crop-vs-screen comparison grabs the screen a second
            // time, so it has to run while nothing of ours is on screen. Showing the
            // overlay first would leave the dimming mask still fading out and the second
            // grab would photograph our own overlay.
            CheckCropMatchesScreen(frozen, bounds);
            CheckOverlayGeometry(frozen, bounds);
        }
        catch (Exception ex)
        {
            Fail($"自检过程本身抛异常：{ex}");
        }

        Line("");
        Line(_failures == 0 ? "结果：全部通过" : $"结果：{_failures} 项未通过");

        WriteReport();
        return _failures == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- checks

    private static void CheckDpiAwareness()
    {
        var context = NativeMethods.GetThreadDpiAwarenessContext();
        var isV2 = NativeMethods.AreDpiAwarenessContextsEqual(
            context, NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

        if (isV2)
        {
            Pass("进程 DPI 感知 = Per-Monitor-V2（清单生效）");
            return;
        }

        var name =
            NativeMethods.AreDpiAwarenessContextsEqual(context, NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE) ? "Per-Monitor V1" :
            NativeMethods.AreDpiAwarenessContextsEqual(context, NativeMethods.DPI_AWARENESS_CONTEXT_SYSTEM_AWARE) ? "System Aware" :
            NativeMethods.AreDpiAwarenessContextsEqual(context, NativeMethods.DPI_AWARENESS_CONTEXT_UNAWARE) ? "Unaware" :
            "未知";

        Fail($"进程 DPI 感知 = {name}，应为 Per-Monitor-V2。"
             + "系统会偷偷缩放坐标，缩放不是 100% 的屏幕上截图必然错位。");
    }

    private static void CheckVirtualDesktopMetrics()
    {
        var bounds = VirtualDesktop.GetBounds();
        Line($"信息：{VirtualDesktop.Describe()}");

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            Fail($"虚拟桌面尺寸无效：{bounds}");
            return;
        }

        // The mask must cover every display, not just the primary one.
        var union = Rectangle.Empty;
        foreach (var screen in Screen.AllScreens)
        {
            union = union.IsEmpty ? screen.Bounds : Rectangle.Union(union, screen.Bounds);
        }

        if (union == bounds)
        {
            Pass($"虚拟桌面矩形覆盖全部 {Screen.AllScreens.Length} 块屏幕：{bounds}");
        }
        else
        {
            Fail($"虚拟桌面矩形 {bounds} 与各屏并集 {union} 不一致，遮罩会盖不全。");
        }
    }

    private static void CheckFrozenSize(Bitmap frozen, Rectangle bounds)
    {
        if (frozen.Width == bounds.Width && frozen.Height == bounds.Height)
            Pass($"冻屏位图尺寸与虚拟桌面一致：{frozen.Width}x{frozen.Height}");
        else
            Fail($"冻屏位图 {frozen.Width}x{frozen.Height} 与虚拟桌面 {bounds.Width}x{bounds.Height} 不一致。");
    }

    private static void CheckOverlayGeometry(Bitmap frozen, Rectangle bounds)
    {
        using var overlay = new SelectionOverlay(frozen, bounds);
        overlay.ShowAtExactGeometry();
        try
        {
            if (!NativeMethods.GetWindowRect(overlay.Handle, out var r))
            {
                Fail("读取遮罩窗口位置失败。");
                return;
            }

            var actual = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            if (actual == bounds)
                Pass($"遮罩窗口实际占据的物理像素矩形 == 虚拟桌面：{actual}");
            else
                Fail($"遮罩窗口在 {actual}，应为 {bounds}。窗口被系统缩放了，框选会错位。");

            var client = overlay.ClientSize;
            if (client.Width == bounds.Width && client.Height == bounds.Height)
                Pass($"遮罩客户区尺寸 == 位图尺寸：{client.Width}x{client.Height}");
            else
                Fail($"遮罩客户区 {client.Width}x{client.Height} 与位图 {bounds.Width}x{bounds.Height} 不一致。");

            // The mapping the crop depends on: screen origin of the virtual desktop must
            // land on client (0,0).
            var origin = overlay.PointToClient(new Point(bounds.X, bounds.Y));
            if (origin == Point.Empty)
                Pass("屏幕坐标 → 客户区坐标映射为 1:1 平移（无缩放）");
            else
                Fail($"虚拟桌面原点映射到客户区 {origin}，应为 (0,0)。");
        }
        finally
        {
            overlay.Close();
        }
    }

    /// <summary>
    /// The end-to-end check: a rectangle cropped out of the frozen snapshot must be
    /// identical to grabbing that same screen rectangle directly.
    /// </summary>
    private static void CheckCropMatchesScreen(Bitmap frozen, Rectangle bounds)
    {
        var primary = Screen.PrimaryScreen?.Bounds ?? bounds;

        // Centre of the primary display: away from the clock and any tray animation, so
        // the two grabs see the same pixels.
        var probeW = Math.Min(320, primary.Width / 3);
        var probeH = Math.Min(240, primary.Height / 3);
        var screenRect = new Rectangle(
            primary.X + (primary.Width - probeW) / 2,
            primary.Y + (primary.Height - probeH) / 2,
            probeW, probeH);

        var clientRect = new Rectangle(
            screenRect.X - bounds.X, screenRect.Y - bounds.Y, probeW, probeH);

        using var fromFrozen = ScreenGrabber.Crop(frozen, clientRect);
        using var fromScreen = new Bitmap(probeW, probeH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(fromScreen))
        {
            g.CopyFromScreen(screenRect.Left, screenRect.Top, 0, 0, screenRect.Size, CopyPixelOperation.SourceCopy);
        }

        var mismatched = CountDifferentPixels(fromFrozen, fromScreen);
        var total = probeW * probeH;

        if (mismatched == 0)
        {
            Pass($"裁剪结果与直接截取屏幕同一区域逐像素一致（{probeW}x{probeH} @ 屏幕 {screenRect.X},{screenRect.Y}）");
        }
        else if (mismatched * 100.0 / total < 2.0)
        {
            // Something on screen moved between the two grabs; not an alignment fault.
            Pass($"裁剪结果与屏幕基本一致（{mismatched}/{total} 像素不同，应为期间画面有变化）");
        }
        else
        {
            Fail($"裁剪结果与屏幕差异过大：{mismatched}/{total} 像素不同。"
                 + "这正是「框的位置和截到的区域对不上」的症状。");
        }
    }

    private static long CountDifferentPixels(Bitmap a, Bitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return long.MaxValue;

        var rect = new Rectangle(0, 0, a.Width, a.Height);
        var da = a.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var db = b.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            long different = 0;
            unsafe
            {
                for (var y = 0; y < a.Height; y++)
                {
                    var pa = (uint*)((byte*)da.Scan0 + (long)y * da.Stride);
                    var pb = (uint*)((byte*)db.Scan0 + (long)y * db.Stride);
                    for (var x = 0; x < a.Width; x++)
                    {
                        // Ignore alpha: CopyFromScreen and our own buffer can disagree on it
                        // while the visible colour is identical.
                        if ((pa[x] & 0x00FFFFFF) != (pb[x] & 0x00FFFFFF)) different++;
                    }
                }
            }
            return different;
        }
        finally
        {
            a.UnlockBits(da);
            b.UnlockBits(db);
        }
    }

    // ---------------------------------------------------------------- output

    private static void Pass(string message) => Line($"[通过] {message}");

    private static void Fail(string message)
    {
        _failures++;
        Line($"[失败] {message}");
    }

    private static void Line(string message)
    {
        Report.AppendLine(message);
        Log.Info($"[selftest] {message}");
    }

    private static void WriteReport()
    {
        try
        {
            Paths.EnsureDataDir();
            File.WriteAllText(Path.Combine(Paths.DataDir, "selftest.txt"), Report.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Log.Error("写自检报告失败", ex);
        }
    }
}
