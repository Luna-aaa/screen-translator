using System.Drawing;
using ScreenTranslator.Config;
using ScreenTranslator.Infrastructure;
using ScreenTranslator.Ocr;
using ScreenTranslator.Translate;

namespace ScreenTranslator.Overlay;

public enum SnapshotStatus
{
    Success,
    NoText,
    MissingLanguagePack,
    NotConfigured,
    OcrFailed,
    TranslationFailed,
    Cancelled,
}

/// <summary>What one snapshot run produced, independent of how it will be shown.</summary>
public sealed class SnapshotResult
{
    public SnapshotStatus Status { get; init; }

    /// <summary>Plain-language explanation, ready to put in front of the user.</summary>
    public string Message { get; init; } = "";

    public IReadOnlyList<RenderBlock> Blocks { get; init; } = Array.Empty<RenderBlock>();

    /// <summary>Tag of the recognizer whose geometry was used.</summary>
    public string LanguageTag { get; init; } = "";

    /// <summary>Whether the screenshot was sent alongside the text, so OCR errors could be corrected.</summary>
    public bool UsedVision { get; init; }

    public long OcrMs { get; init; }
    public long TranslateMs { get; init; }

    /// <summary>Tokens across every batch, when the service reported them.</summary>
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public int TotalTokens => PromptTokens + CompletionTokens;

    /// <summary>The model's reply verbatim, for the diagnostic report. Empty in the popup path.</summary>
    public string RawReply { get; init; } = "";

    /// <summary>Which batch each block went out in, parallel to <see cref="Blocks"/>. For the block map.</summary>
    public IReadOnlyList<int> BatchOfBlock { get; init; } = Array.Empty<int>();

    /// <summary>How many requests the screen was split into.</summary>
    public int BatchCount { get; init; }

    public int Answered => Blocks.Count(b => b.Translation is not null);

    public bool IsSuccess => Status == SnapshotStatus.Success;
}

/// <summary>
/// The whole-screen translation, minus any opinion about how it gets displayed.
///
/// Split out from <see cref="SnapshotService"/> so the headless diagnostic
/// (<c>--testsnapshot</c>) exercises exactly the code the real feature runs. A self-test
/// that re-implements the pipeline it is checking tests only itself.
/// </summary>
internal static class SnapshotPipeline
{
    /// <summary>
    /// Ceiling on how many blocks go out in one request. Set high enough that an ordinary
    /// page is covered completely: a block that gets dropped leaves its original text
    /// showing in the middle of translated ones, which reads as a bug rather than as a
    /// limit. A dense IDE screen produces around eighty.
    /// </summary>
    public const int MaxBlocks = 110;

    /// <summary>Blocks shorter than this are icons, single letters and clock digits.</summary>
    private const int MinBlockChars = 2;

    /// <summary>
    /// Most segments a retry pass is worth doing for. Past this the model did not miss a
    /// few lines, it ignored the instruction wholesale, and a second identical request is
    /// unlikely to go better — better to show what came back than to spend twice for it.
    /// </summary>
    private const int MaxRetrySegments = 30;

    /// <param name="translator">
    /// Null runs everything except the network call, filling in placeholder text of a
    /// realistic length. That is what makes it possible to check the part most likely to
    /// be wrong — where the boxes are and whether the text fits them — without spending
    /// a request, and without any dependence on what a model happens to answer.
    /// </param>
    public static async Task<SnapshotResult> RunAsync(
        Bitmap frozen,
        AppConfig config,
        IOcrLayoutProvider ocr,
        ITranslator? translator,
        bool withImage,
        Action<string> status,
        CancellationToken cancellationToken,
        Action<IReadOnlyList<RenderBlock>>? onPartial = null)
    {
        status("正在找出屏幕上的文字…");

        var request = new OcrRequest(config.OcrSourceLanguage, config.OcrCandidateLanguages);
        var layout = await ocr.RecognizeLayoutAsync(frozen, request, cancellationToken).ConfigureAwait(true);
        if (cancellationToken.IsCancellationRequested)
            return new SnapshotResult { Status = SnapshotStatus.Cancelled };

        switch (layout.Status)
        {
            case OcrStatus.Success:
                break;
            case OcrStatus.NoText:
                return new SnapshotResult { Status = SnapshotStatus.NoText, Message = "这一屏上没有找到文字。" };
            case OcrStatus.MissingLanguagePack:
                var names = string.Join("、", layout.MissingLanguages.Select(l => l.DisplayName));
                return new SnapshotResult
                {
                    Status = SnapshotStatus.MissingLanguagePack,
                    Message = $"Windows 上还没装{names}的文字识别语言包，认不出来。到设置 → 文字识别里装一下。",
                };
            default:
                return new SnapshotResult
                {
                    Status = SnapshotStatus.OcrFailed,
                    Message = $"识别失败：{layout.Error ?? "未知错误，详情见日志。"}",
                };
        }

        var groups = SelectBlocks(layout.Lines);
        if (groups.Count == 0)
        {
            return new SnapshotResult
            {
                Status = SnapshotStatus.NoText,
                Message = "这一屏上没有成段的文字可翻。",
                LanguageTag = layout.LanguageTag,
                OcrMs = layout.ElapsedMs,
            };
        }

        if (translator is null)
        {
            var dry = groups.Select(g => new RenderBlock(g, Placeholder.For(g.Text))).ToList();
            return new SnapshotResult
            {
                Status = SnapshotStatus.Success,
                Blocks = dry,
                LanguageTag = layout.LanguageTag,
                OcrMs = layout.ElapsedMs,
                Message = "空跑：没有联网，译文是按真实长度估算的占位文字。",
                BatchOfBlock = DryBatchMap(groups.Count),
                BatchCount = (groups.Count + SnapshotBatching.BlocksPerBatch - 1) / SnapshotBatching.BlocksPerBatch,
            };
        }

        if (!translator.IsConfigured)
        {
            return new SnapshotResult
            {
                Status = SnapshotStatus.NotConfigured,
                Message = "翻译服务还没配置好。右键托盘图标 → 设置，把「翻译」或「看图直翻」任意一页填完整。",
                LanguageTag = layout.LanguageTag,
                OcrMs = layout.ElapsedMs,
            };
        }

        // Cropped here, on this thread, before anything is dispatched: every batch then
        // owns its own bitmap and nothing races the overlay repainting the frozen one.
        var batches = SnapshotBatching.Split(groups, frozen, withImage);
        status($"找到 {groups.Count} 段，分 {batches.Count} 批翻译…");

        var translations = new string?[groups.Count];
        var batchOf = new int[groups.Count];
        for (var b = 0; b < batches.Count; b++)
        {
            foreach (var index in batches[b].Indexes) batchOf[index] = b;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        TranslationOutcome? firstFailure = null;
        var done = 0;
        var replies = new string[batches.Count];
        var promptTokens = 0;
        var completionTokens = 0;

        try
        {
            using var slots = new SemaphoreSlim(SnapshotBatching.MaxParallel);
            var gate = new object();

            var running = batches.Select(async (batch, order) =>
            {
                await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var outcome = await TranslateBatchAsync(
                        batch, layout.LanguageTag, config, translator, withImage, cancellationToken)
                        .ConfigureAwait(false);

                    lock (gate)
                    {
                        if (!outcome.IsSuccess) firstFailure ??= outcome;
                        else replies[order] = outcome.Text;

                        promptTokens += outcome.PromptTokens ?? 0;
                        completionTokens += outcome.CompletionTokens ?? 0;
                        done++;
                    }
                }
                finally
                {
                    slots.Release();
                }
            }).ToList();

            // Progress is reported from the awaiting thread rather than from inside the
            // tasks, so the caller's callback stays on the UI thread and can repaint.
            while (running.Count > 0)
            {
                var finished = await Task.WhenAny(running).ConfigureAwait(true);
                running.Remove(finished);
                if (cancellationToken.IsCancellationRequested) break;

                await finished.ConfigureAwait(true);   // surface any exception

                ApplyReplies(batches, replies, translations);
                status($"已完成 {done}/{batches.Count} 批…");

                // Redraw with what has arrived so far, so the screen fills in piece by
                // piece instead of showing one line of status text for ten seconds.
                onPartial?.Invoke(BuildBlocks(groups, translations));
            }
        }
        catch
        {
            foreach (var batch in batches) batch.Dispose();
            throw;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            foreach (var batch in batches) batch.Dispose();
            return new SnapshotResult { Status = SnapshotStatus.Cancelled };
        }

        ApplyReplies(batches, replies, translations);

        // Only a total loss is reported as a failure. If some batches came back, showing
        // those beats throwing them away over the ones that did not.
        if (translations.All(t => t is null) && firstFailure is not null)
        {
            foreach (var batch in batches) batch.Dispose();
            return new SnapshotResult
            {
                Status = SnapshotStatus.TranslationFailed,
                Message = firstFailure.Message,
                LanguageTag = layout.LanguageTag,
                OcrMs = layout.ElapsedMs,
                UsedVision = withImage,
            };
        }

        try
        {
            await RetryUntranslatedAsync(
                batches, groups, translations, config, translator, withImage, status, cancellationToken)
                .ConfigureAwait(true);
        }
        finally
        {
            // Only now: the retry pass reuses a batch's crop rather than cutting a new
            // one, so disposing with the parallel loop left it encoding a freed bitmap.
            foreach (var batch in batches) batch.Dispose();
        }

        var blocks = BuildBlocks(groups, translations);
        var answered = blocks.Count(b => b.Translation is not null);

        Log.Info($"整屏翻译：{groups.Count} 段送出（{batches.Count} 批），{answered} 段有译文"
                 + $"，翻译用时 {sw.ElapsedMilliseconds}ms");

        return new SnapshotResult
        {
            Status = SnapshotStatus.Success,
            Blocks = blocks,
            LanguageTag = layout.LanguageTag,
            UsedVision = withImage,
            OcrMs = layout.ElapsedMs,
            TranslateMs = sw.ElapsedMilliseconds,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            RawReply = string.Join("\n---\n", replies.Where(r => !string.IsNullOrEmpty(r))),
            BatchOfBlock = batchOf,
            BatchCount = batches.Count,
        };
    }

    /// <summary>Sends one batch, and retries that batch alone if it comes back unparseable.</summary>
    private static async Task<TranslationOutcome> TranslateBatchAsync(
        SnapshotBatch batch,
        string languageTag,
        AppConfig config,
        ITranslator translator,
        bool withImage,
        CancellationToken cancellationToken)
    {
        var request = new TranslationRequest("", languageTag, config.TargetLanguage, ExtraPromptFor(config, withImage))
        {
            Segments = batch.Segments,
            Image = batch.Image,
        };

        // No streaming: a partial numbered list cannot be placed on screen, and half a
        // block drawn over its own original would be unreadable rather than reassuring.
        var outcome = await translator.TranslateAsync(request, null, cancellationToken).ConfigureAwait(false);
        if (!outcome.IsSuccess || cancellationToken.IsCancellationRequested) return outcome;

        // A batch that parses to nothing is the failure the old whole-screen request hid:
        // it looked like success, and the screen simply came back untranslated. Sending
        // just this batch again is cheap, and usually enough.
        var parsed = BatchFormat.Parse(outcome.Text, batch.Segments.Count);
        if (parsed.Any(t => t is not null)) return outcome;

        Log.Warn($"整屏翻译：有一批 {batch.Segments.Count} 段解析不出任何译文，重发这一批。"
                 + $"模型原话：{Truncate(outcome.Text, 300)}");

        return await translator.TranslateAsync(request with { InsistOnTranslating = true }, null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Maps each batch's numbered answers back onto the full block list.</summary>
    private static void ApplyReplies(
        IReadOnlyList<SnapshotBatch> batches, IReadOnlyList<string> replies, string?[] translations)
    {
        for (var b = 0; b < batches.Count; b++)
        {
            var reply = replies[b];
            if (string.IsNullOrEmpty(reply)) continue;

            var batch = batches[b];
            var parsed = BatchFormat.Parse(reply, batch.Segments.Count);
            for (var i = 0; i < batch.Indexes.Count; i++)
            {
                if (parsed[i] is not null) translations[batch.Indexes[i]] = parsed[i];
            }
        }
    }

    private static List<RenderBlock> BuildBlocks(IReadOnlyList<TextGroup> groups, string?[] translations) =>
        groups.Select((g, i) => new RenderBlock(g, translations[i])).ToList();

    private static int[] DryBatchMap(int count)
    {
        var map = new int[count];
        for (var i = 0; i < count; i++) map[i] = i / SnapshotBatching.BlocksPerBatch;
        return map;
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) ? "" : value.Length <= max ? value : value[..max] + "…";

    /// <summary>
    /// Sends back whatever came home untranslated, once.
    ///
    /// Models skip segments. On a screen full of interface text they start treating short
    /// foreign strings as names and echo them verbatim, which shows up as a page where a
    /// couple of English or Japanese lines sit untranslated among Chinese ones — the single
    /// most visible way this feature looks broken. A second request naming those specific
    /// segments fixes most of them, and costs a fraction of the first because it carries
    /// only the leftovers.
    /// </summary>
    private static async Task RetryUntranslatedAsync(
        IReadOnlyList<SnapshotBatch> batches,
        List<TextGroup> groups,
        string?[] translations,
        AppConfig config,
        ITranslator translator,
        bool withImage,
        Action<string> status,
        CancellationToken cancellationToken)
    {
        var pending = new List<int>();
        for (var i = 0; i < groups.Count; i++)
        {
            if (LooksUntranslated(groups[i].Text, translations[i])) pending.Add(i);
        }

        if (pending.Count == 0) return;
        if (pending.Count > MaxRetrySegments)
        {
            Log.Warn($"整屏翻译：{pending.Count} 段原样返回，超过重试上限 {MaxRetrySegments}，不再重试");
            return;
        }

        Log.Info($"整屏翻译：{pending.Count} 段原样返回，重试一次");
        status($"有 {pending.Count} 段没翻过来，正在重试…");

        // Reuses the crop of whichever batch the first pending block came from: the
        // leftovers are usually neighbours, and it saves cropping the screen again.
        var image = withImage
            ? batches.FirstOrDefault(b => b.Indexes.Contains(pending[0]))?.Image
            : null;

        var request = new TranslationRequest("", "", config.TargetLanguage, config.Snapshot.ExtraPrompt)
        {
            Segments = pending.Select(i => groups[i].Text).ToList(),
            Image = image,
            InsistOnTranslating = true,
        };

        var outcome = await translator.TranslateAsync(request, null, cancellationToken).ConfigureAwait(true);
        if (cancellationToken.IsCancellationRequested || !outcome.IsSuccess)
        {
            Log.Warn($"整屏翻译重试失败：{outcome.Message}");
            return;
        }

        var retried = BatchFormat.Parse(outcome.Text, pending.Count);
        var fixedCount = 0;

        for (var i = 0; i < pending.Count; i++)
        {
            var answer = retried[i];
            if (answer is null) continue;
            if (LooksUntranslated(groups[pending[i]].Text, answer)) continue;   // still the same

            translations[pending[i]] = answer;
            fixedCount++;
        }

        Log.Info($"整屏翻译重试：{fixedCount}/{pending.Count} 段这次翻出来了");
    }

    /// <summary>
    /// Whether a segment came back the way it went out, when it plainly should not have.
    ///
    /// Only foreign scripts count. A Chinese segment echoed back is the correct answer, and
    /// something that is all digits, punctuation or a URL has nothing to translate — asking
    /// again for either would burn a request on a right answer.
    /// </summary>
    private static bool LooksUntranslated(string original, string? translation)
    {
        if (translation is null) return false;

        var left = original.Trim();
        var right = translation.Trim();
        if (left.Length == 0 || !string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) return false;

        var foreign = 0;
        foreach (var c in left)
        {
            // Han characters mean it may well already be Chinese; kana and hangul never are.
            if (c is >= '一' and <= '鿿') return false;
            if (char.IsLetter(c)) foreign++;
        }

        // At least two letters, so "OK" survives but "x" and "12:30" are left alone.
        return foreign >= 2;
    }

    /// <summary>
    /// Drops what is not worth translating and caps the rest. Clock digits, single-letter
    /// icons and toolbar glyphs are most of what OCR finds on a desktop, and every one of
    /// them would cost a line in the request and leave a patch on the screen.
    /// </summary>
    public static List<TextGroup> SelectBlocks(IReadOnlyList<OcrLineBox> lines)
    {
        var groups = TextGrouping.Build(lines)
            .Where(g => g.Text.Trim().Length >= MinBlockChars)
            .Where(g => g.Bounds.Width >= 12 && g.Bounds.Height >= 8)
            .Where(g => g.Text.Any(char.IsLetter))
            .ToList();

        if (groups.Count <= MaxBlocks) return groups;

        // Keep the biggest ones: on an over-full screen, area is a good proxy for what the
        // user was actually reading.
        Log.Info($"整屏识别到 {groups.Count} 段，超过上限 {MaxBlocks}，只翻面积最大的那些");
        return groups
            .OrderByDescending(g => g.Bounds.Width * g.Bounds.Height)
            .Take(MaxBlocks)
            .OrderBy(g => g.Bounds.Top)
            .ThenBy(g => g.Bounds.Left)
            .ToList();
    }

    /// <summary>
    /// Prefers the vision service when one is set up, because handing the model the
    /// screenshot alongside the recognized text is what lets it correct OCR's mistakes.
    /// Falls back to the text service so the feature still works for someone who never
    /// configured the picture route.
    ///
    /// Takes the config rather than reading <c>App.Config</c>: the headless diagnostic
    /// loads its own copy, and a pipeline that quietly consulted the running app's static
    /// instead would report "还没配置" for a machine that is configured perfectly well.
    /// </summary>
    public static (ITranslator Translator, bool WithImage) ChooseTranslator(AppConfig config)
    {
        var snapshot = new OpenAiCompatibleTranslator(config.Snapshot, vision: true);
        if (snapshot.IsConfigured) return (snapshot, true);

        // Falling back to the text route keeps the feature usable for someone who has not
        // filled in the 全屏翻译 page yet, but says so: without the picture, nothing
        // corrects what OCR misread.
        Log.Info("全屏翻译：这一页还没配好服务，暂时改用「识文翻译」的文字模型（识别错的字就没人纠正了）");
        return (new OpenAiCompatibleTranslator(config.OpenAi, vision: false), false);
    }

    private static string ExtraPromptFor(AppConfig config, bool withImage) =>
        withImage ? config.Snapshot.ExtraPrompt : config.OpenAi.ExtraPrompt;

    /// <summary>
    /// Stand-in text for a dry run. Sized from the original rather than fixed, because the
    /// question a dry run answers is "will the real translation fit", and a constant-length
    /// placeholder would answer it identically for every block.
    /// </summary>
    private static class Placeholder
    {
        private const string Filler = "这是一段用来测量排版的占位文字它没有实际含义只是长度差不多";

        /// <summary>Chinese runs roughly this fraction of the character count of English prose.</summary>
        private const double LengthRatio = 0.55;

        public static string For(string original)
        {
            var target = Math.Max(2, (int)Math.Round(original.Trim().Length * LengthRatio));
            var sb = new System.Text.StringBuilder(target);
            while (sb.Length < target) sb.Append(Filler);
            return sb.ToString(0, target);
        }
    }
}
