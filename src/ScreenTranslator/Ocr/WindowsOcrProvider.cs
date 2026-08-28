using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using ScreenTranslator.Infrastructure;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace ScreenTranslator.Ocr;

/// <summary>
/// Text recognition through the engine built into Windows 10/11: free, offline, fast,
/// no API key. Its weakness is that it only recognizes languages whose packs are
/// installed, and it cannot detect which language it is looking at — see
/// <see cref="LanguageScorer"/> for how "auto" is resolved.
/// </summary>
public sealed class WindowsOcrProvider : IOcrProvider
{
    public string Id => "windows";
    public string DisplayName => "Windows 内置识别";

    public async Task<OcrOutcome> RecognizeAsync(
        Bitmap image, OcrRequest request, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            var wanted = request.ResolveTargets();
            if (wanted.Count == 0)
                return OcrOutcome.Failed("没有指定任何识别语言。");

            var installed = LanguagePackHelper.InstalledTags();

            // Resolve each wanted language to the tag Windows actually reports, and build
            // the engine from that. The two differ in the wild - a machine may register
            // Japanese as plain "ja" while the config asks for "ja-JP" - and
            // TryCreateFromLanguage is not guaranteed to bridge the gap.
            var usable = new List<string>();
            foreach (var tag in wanted)
            {
                var match = installed.FirstOrDefault(i => OcrLanguages.TagsMatch(i, tag));
                if (match is not null && !usable.Contains(match)) usable.Add(match);
            }

            if (usable.Count == 0)
            {
                var missing = LanguagePackHelper.Missing(wanted);
                Log.Warn($"缺少 OCR 语言包：{string.Join(", ", wanted)}；系统已装：{string.Join(", ", installed)}");
                return OcrOutcome.MissingLanguagePack(missing);
            }

            using var softwareBitmap = await ToSoftwareBitmapAsync(image).ConfigureAwait(true);

            string bestText = "";
            string bestTag = usable[0];
            double bestScore = double.NegativeInfinity;

            foreach (var tag in usable)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var engine = TryCreateEngine(tag);
                if (engine is null) continue;

                var result = await engine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken).ConfigureAwait(true);
                var text = BuildText(result);
                var score = LanguageScorer.Score(text, tag);

                Log.Info($"OCR 尝试 {tag}：{text.Length} 字，得分 {score:F1}");

                if (score > bestScore)
                {
                    bestScore = score;
                    bestText = text;
                    bestTag = tag;
                }
            }

            var elapsed = sw.ElapsedMilliseconds;
            var tried = usable.ToList();

            if (string.IsNullOrWhiteSpace(bestText))
            {
                Log.Info($"OCR 未识别到文字，用时 {elapsed}ms");
                return OcrOutcome.NoText(bestTag).WithTelemetry(elapsed, tried);
            }

            // A short preview only: enough to diagnose a bad recognition from the log
            // without writing the user's screen contents to disk wholesale.
            var preview = bestText.Replace('\n', '/');
            if (preview.Length > 60) preview = preview[..60] + "…";
            Log.Info($"OCR 选用 {bestTag}，{bestText.Length} 字，用时 {elapsed}ms，预览：{preview}");
            return OcrOutcome.Success(bestText, bestTag).WithTelemetry(elapsed, tried);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error("文字识别失败", ex);
            return OcrOutcome.Failed(ex.Message).WithTelemetry(sw.ElapsedMilliseconds, Array.Empty<string>());
        }
    }

    private static OcrEngine? TryCreateEngine(string tag)
    {
        try
        {
            return OcrEngine.TryCreateFromLanguage(new Language(tag));
        }
        catch (Exception ex)
        {
            Log.Warn($"创建 {tag} 识别引擎失败：{ex.Message}");
            return null;
        }
    }

    // ------------------------------------------------------------ conversion

    /// <summary>
    /// Hands the crop to WinRT. Goes through a PNG round-trip rather than poking at
    /// SoftwareBitmap's raw buffer: the buffer route needs COM interop that is fragile
    /// across .NET/CsWinRT versions, and encoding a screen-sized crop costs only a few ms.
    /// </summary>
    private static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(Bitmap source)
    {
        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            source.Save(ms, ImageFormat.Png);
            bytes = ms.ToArray();
        }

        using var stream = new InMemoryRandomAccessStream();
        var writer = new DataWriter(stream);
        try
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        finally
        {
            writer.Dispose();
        }

        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);

        var (width, height) = ChooseSize(source.Width, source.Height);
        Log.Info($"OCR 输入 {source.Width}x{source.Height} → {width}x{height}"
                 + $"（引擎单边上限 {OcrEngine.MaxImageDimension}）");

        var transform = new BitmapTransform
        {
            ScaledWidth = width,
            ScaledHeight = height,
            InterpolationMode = BitmapInterpolationMode.Cubic,
        };

        return await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
    }

    /// <summary>
    /// Small crops get upscaled before recognition — a single line of subtitle grabbed at
    /// 40px tall recognizes far better at 3x. Also clamps to the engine's hard size limit,
    /// which a full-screen selection can otherwise exceed.
    /// </summary>
    private static (uint Width, uint Height) ChooseSize(int width, int height)
    {
        var smallest = Math.Min(width, height);
        var scale = smallest >= 200 ? 1.0 : smallest >= 100 ? 2.0 : 3.0;

        var max = (double)OcrEngine.MaxImageDimension;
        var largest = Math.Max(width, height);
        if (largest * scale > max) scale = max / largest;

        var w = (uint)Math.Max(1, Math.Round(width * scale));
        var h = (uint)Math.Max(1, Math.Round(height * scale));
        return (w, h);
    }

    // ------------------------------------------------------------------ text

    private static string BuildText(OcrResult result)
    {
        var sb = new StringBuilder();
        foreach (var line in result.Lines)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(CollapseCjkSpaces(line.Text));
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Windows OCR separates every "word" with a space, and for Chinese/Japanese it treats
    /// nearly every character as its own word — so raw output reads like "这 是 一 段 话".
    /// Spaces sitting between two CJK characters are dropped; spaces around Latin text are
    /// real word separators and stay.
    /// </summary>
    private static string CollapseCjkSpaces(string line)
    {
        if (line.Length < 3) return line;

        var sb = new StringBuilder(line.Length);
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == ' ' && i > 0 && i + 1 < line.Length
                && LanguageScorer.IsCjkLike(line[i - 1])
                && LanguageScorer.IsCjkLike(line[i + 1]))
            {
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }
}
