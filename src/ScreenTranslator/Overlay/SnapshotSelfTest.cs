using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Text;
using ScreenTranslator.Capture;
using ScreenTranslator.Config;
using ScreenTranslator.Infrastructure;
using ScreenTranslator.Ocr;
using ScreenTranslator.Translate;

namespace ScreenTranslator.Overlay;

/// <summary>
/// Headless check of the whole-screen snapshot:
/// <code>
/// ScreenTranslator.exe --testsnapshot          用当前配置真跑一遍
/// ScreenTranslator.exe --testsnapshot --dry    不联网，只验证识别 / 分段 / 排版
/// ScreenTranslator.exe --testsnapshot &lt;url&gt; &lt;model&gt; &lt;key&gt;
/// </code>
///
/// It runs the real pipeline over whatever is on screen right now and writes the result
/// to disk instead of showing it. Two things make that worth having over just pressing
/// the hotkey: nothing takes the foreground or grabs the keyboard, and the output is a
/// pair of PNGs that can be looked at side by side afterwards — the boxes OCR found, and
/// what the finished overlay would have looked like.
///
/// <c>--dry</c> is the mode to reach for first. Layout is where this feature goes wrong,
/// and a dry run answers "are the boxes right and does the text fit them" with no network,
/// no cost, and no dependence on what a model happened to reply.
/// </summary>
internal static class SnapshotSelfTest
{
    public static async Task<int> RunAsync(string[] arguments)
    {
        var report = new StringBuilder();
        void Line(string text)
        {
            report.AppendLine(text);
            Log.Info($"[testsnapshot] {text}");
        }

        var sw = Stopwatch.StartNew();
        Line("=== 整屏原位覆盖自检 ===");
        Line($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        var dry = arguments.Any(a => string.Equals(a, "--dry", StringComparison.OrdinalIgnoreCase));
        var overrides = arguments.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();

        var config = ConfigStore.Load();
        Bitmap? frozen = null;

        try
        {
            var bounds = VirtualDesktop.GetBounds();
            Line(VirtualDesktop.Describe());

            frozen = ScreenGrabber.Grab(bounds);
            Line($"截屏：{frozen.Width}×{frozen.Height}");

            ITranslator? translator = null;
            var withImage = false;

            if (dry)
            {
                Line("模式：空跑（不联网，译文用等长占位文字）");
            }
            else if (overrides.Length >= 3)
            {
                // Overrides never touch the saved config, so running this cannot disturb
                // what the user has set up.
                var settings = new SnapshotSettings
                {
                    BaseUrl = overrides[0],
                    Model = overrides[1],
                    ApiKeyProtected = SecureStore.Protect(overrides[2]),
                    MaxImageEdge = config.Snapshot.MaxImageEdge,
                    TimeoutSeconds = config.Snapshot.TimeoutSeconds,
                };
                translator = new OpenAiCompatibleTranslator(settings, vision: true);
                withImage = true;
                Line($"模式：临时参数　{overrides[0]}　{overrides[1]}");
            }
            else
            {
                (translator, withImage) = SnapshotPipeline.ChooseTranslator(config);
                Line($"模式：当前配置　{translator.DisplayName}"
                     + $"　{(withImage ? "带图（识别错字由模型纠正）" : "纯文字（没配看图服务）")}");
            }

            var result = await SnapshotPipeline
                .RunAsync(frozen, config, new WindowsOcrProvider(), translator, withImage,
                          s => Line($"进度：{s}"), CancellationToken.None)
                .ConfigureAwait(false);

            Line("");
            Line($"识别引擎：{(string.IsNullOrEmpty(result.LanguageTag) ? "(无)" : result.LanguageTag)}"
                 + $"　用时 {result.OcrMs} ms");
            Line($"状态：{result.Status}");

            if (!result.IsSuccess)
            {
                Line($"说明：{result.Message}");
                Write(report);
                return 1;
            }

            UsageStore.Add(UsageStore.RouteSnapshot, result.PromptTokens, result.CompletionTokens);

            Line($"分段：{result.Blocks.Count} 段，分 {result.BatchCount} 批发出，"
                 + $"其中 {result.Answered} 段有译文"
                 + (result.TranslateMs > 0 ? $"　翻译用时 {result.TranslateMs} ms" : "")
                 + (result.TotalTokens > 0 ? $"　{result.PromptTokens}+{result.CompletionTokens} token" : ""));

            var blocksPath = Path.Combine(Paths.DataDir, "testsnapshot-blocks.png");
            var renderPath = Path.Combine(Paths.DataDir, "testsnapshot.png");

            using (var boxes = DrawBlockMap(frozen, result.Blocks, result.BatchOfBlock))
            {
                boxes.Save(blocksPath, ImageFormat.Png);
            }

            using (var rendered = OverlayRenderer.Render(frozen, result.Blocks))
            {
                rendered.Save(renderPath, ImageFormat.Png);
            }

            Line("");
            Line($"分段示意图：{blocksPath}");
            Line($"覆盖效果图：{renderPath}");
            Line("");
            Line("--- 每段的位置、原文、译文 ---");

            for (var i = 0; i < result.Blocks.Count; i++)
            {
                var block = result.Blocks[i];
                var b = block.Group.Bounds;
                Line($"[{i + 1}] 第{(i < result.BatchOfBlock.Count ? result.BatchOfBlock[i] + 1 : 1)}批"
                     + $"　({b.X:F0},{b.Y:F0}) {b.Width:F0}×{b.Height:F0}"
                     + $"　{block.Group.Lines.Count} 行　行高 {block.Group.LineHeight:F0}px");
                Line($"    原：{Clip(block.Group.Text)}");
                Line($"    译：{(block.Translation is null ? "(没翻出来，保留原文)" : Clip(block.Translation))}");
            }

            if (!string.IsNullOrEmpty(result.RawReply))
            {
                Line("");
                Line("--- 模型原始回复 ---");
                Line(Clip(result.RawReply, 4000));
            }

            Line("");
            Line($"总用时：{sw.ElapsedMilliseconds} ms");
            Line("结果：跑通。**要看那两张 PNG 才算数**——译文有没有盖在对应的原文上、字有没有小到看不清，");
            Line("      这两件事报告里的数字看不出来。");
            Write(report);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("整屏自检失败", ex);
            Line($"结果：出错 - {ex.Message}");
            Write(report);
            return 3;
        }
        finally
        {
            frozen?.Dispose();
        }
    }

    /// <summary>
    /// The original screen with every block outlined and numbered. This is the picture that
    /// shows whether line grouping worked: one box around a whole paragraph is right, three
    /// boxes around its three lines means the translation will come back as fragments.
    /// </summary>
    /// <summary>
    /// One colour per batch, cycling. Seeing the batches is the point: if a batch's blocks
    /// are scattered all over the screen, its crop covers the whole desktop and the
    /// splitting has bought nothing.
    /// </summary>
    private static readonly Color[] BatchColors =
    {
        Color.FromArgb(220, 255, 90, 90),
        Color.FromArgb(220, 120, 200, 255),
        Color.FromArgb(220, 255, 200, 80),
        Color.FromArgb(220, 140, 240, 150),
        Color.FromArgb(220, 230, 140, 255),
    };

    private static Bitmap DrawBlockMap(
        Bitmap frozen, IReadOnlyList<RenderBlock> blocks, IReadOnlyList<int> batchOfBlock)
    {
        var map = new Bitmap(frozen.Width, frozen.Height, PixelFormat.Format32bppPArgb);
        try
        {
            using var g = Graphics.FromImage(map);
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImageUnscaled(frozen, 0, 0);

            g.CompositingMode = CompositingMode.SourceOver;
            g.SmoothingMode = SmoothingMode.None;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            using var label = new Font("Consolas", 12f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var labelBack = new SolidBrush(Color.FromArgb(230, 20, 22, 30));
            using var labelInk = new SolidBrush(Color.FromArgb(120, 220, 140));
            for (var i = 0; i < blocks.Count; i++)
            {
                var batch = i < batchOfBlock.Count ? batchOfBlock[i] : 0;
                using var edge = new SolidBrush(BatchColors[batch % BatchColors.Length]);

                var r = Rectangle.Round(blocks[i].Group.Bounds);

                // FillRectangle, never DrawRectangle: a 1px pen under PixelOffsetMode.Half
                // lands on the neighbouring row and leaves the outline outside any region
                // that gets repainted.
                g.FillRectangle(edge, r.X, r.Y, r.Width, 1);
                g.FillRectangle(edge, r.X, r.Bottom - 1, r.Width, 1);
                g.FillRectangle(edge, r.X, r.Y, 1, r.Height);
                g.FillRectangle(edge, r.Right - 1, r.Y, 1, r.Height);

                var tag = $"{i + 1}/#{batch + 1}";
                var size = g.MeasureString(tag, label);
                var box = new Rectangle(r.X, Math.Max(0, r.Y - (int)size.Height - 1),
                    (int)size.Width + 6, (int)size.Height + 2);
                g.FillRectangle(labelBack, box);
                g.DrawString(tag, label, labelInk, box.X + 3, box.Y + 1);
            }

            return map;
        }
        catch
        {
            map.Dispose();
            throw;
        }
    }

    private static string Clip(string value, int max = 160)
    {
        var flat = value.Replace("\r", " ").Replace("\n", " / ").Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    private static void Write(StringBuilder report)
    {
        try
        {
            Paths.EnsureDataDir();
            File.WriteAllText(Path.Combine(Paths.DataDir, "testsnapshot.txt"), report.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Log.Error("写整屏自检报告失败", ex);
        }
    }
}
