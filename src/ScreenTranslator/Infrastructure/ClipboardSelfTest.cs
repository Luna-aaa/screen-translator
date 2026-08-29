using System.Diagnostics;
using System.IO;
using System.Text;

namespace ScreenTranslator.Infrastructure;

/// <summary>
/// Headless check of copying:
/// <code>ScreenTranslator.exe --testclipboard</code>
///
/// Exists because the copy bug was reported twice and "fixed" once without ever being
/// reproduced — the first fix made it slower and still wrong. The thing that has to be
/// measured is not whether copying works on an idle machine (it always did) but what
/// happens while another process is holding the clipboard open: does the app say it
/// succeeded, does the text actually arrive, and how long is the UI frozen for.
///
/// Run it on its own for the happy path, or run it while something else holds the
/// clipboard to reproduce the real complaint. Writes %APPDATA%\ScreenTranslator\testclipboard.txt.
/// </summary>
internal static class ClipboardSelfTest
{
    public static int Run(string[] arguments)
    {
        var report = new StringBuilder();
        void Line(string text)
        {
            report.AppendLine(text);
            Log.Info($"[testclipboard] {text}");
        }

        Line("=== 复制到剪贴板自检 ===");
        Line($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        var marker = arguments.Length > 0 && arguments[0].Length > 0
            ? arguments[0]
            : $"ScreenTranslator-clipboard-test-{DateTime.Now:HHmmss}";

        Line($"要复制的内容：{marker}");
        Line("");

        var sw = Stopwatch.StartNew();
        var ok = ClipboardHelper.TrySetText(marker);
        sw.Stop();

        Line($"报告的结果：{(ok ? "成功" : "失败")}");
        Line($"耗时：{sw.ElapsedMilliseconds} ms");

        // Half a second is where a click starts to feel like it stalled. A failure
        // necessarily costs the whole retry window, so only a success is expected to be
        // quick — that is the case the user actually sits through.
        Line(sw.ElapsedMilliseconds <= 600
            ? "耗时判定：可以接受（没有明显卡顿）"
            : "耗时判定：**太慢了**，用户会感觉到卡");

        Line("");
        Line("剪贴板里现在是什么，交给调用方在本进程之外去读——");
        Line("在本进程里读会再开一次剪贴板，占用还没解除时同样会失败，测不出东西。");

        Write(report);
        return ok ? 0 : 1;
    }

    private static void Write(StringBuilder report)
    {
        try
        {
            Paths.EnsureDataDir();
            File.WriteAllText(Path.Combine(Paths.DataDir, "testclipboard.txt"), report.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Log.Error("写剪贴板自检报告失败", ex);
        }
    }
}
