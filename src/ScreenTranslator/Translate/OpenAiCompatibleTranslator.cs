using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ScreenTranslator.Config;
using ScreenTranslator.Infrastructure;

namespace ScreenTranslator.Translate;

/// <summary>
/// Talks to any service that speaks OpenAI's /chat/completions shape — DeepSeek, DashScope,
/// GLM, Kimi, OpenAI itself, and most relays. One implementation covers all of them because
/// switching vendor is only ever a different base URL, model name and key.
/// </summary>
public sealed class OpenAiCompatibleTranslator : ITranslator, IModelCatalog
{
    /// <summary>
    /// Goes through the system proxy, which is usually required to reach services hosted
    /// outside the country.
    ///
    /// Shared on purpose: a new HttpClient per request leaks sockets in TIME_WAIT. The
    /// per-call deadline comes from a CancellationToken instead of HttpClient.Timeout,
    /// which cannot vary per request on a shared instance.
    /// </summary>
    private static readonly HttpClient Proxied = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>
    /// Used for loopback endpoints. A locally hosted model (Ollama, LM Studio, llama.cpp)
    /// is a realistic thing to point this at, and sending 127.0.0.1 through a configured
    /// system proxy gets it swallowed - the proxy answers 502 and the user is told the
    /// translation service is broken, which is nonsense and impossible to diagnose.
    /// </summary>
    private static readonly HttpClient Direct = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        UseProxy = false,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private static HttpClient ClientFor(Uri uri) => uri.IsLoopback ? Direct : Proxied;

    private readonly IChatServiceSettings _settings;
    private readonly string _apiKey;
    private readonly int _timeoutSeconds;

    /// <summary>
    /// Send the picture instead of text. The two modes share this class because they
    /// differ only in the prompt and the shape of the user message — the endpoint,
    /// streaming reader, salvage rules and error mapping are identical, and keeping one
    /// copy of those is worth more than keeping the two routes textually separate.
    /// </summary>
    private readonly bool _vision;

    /// <summary>Longest edge of the image actually sent. Ignored in text mode.</summary>
    private readonly int _maxImageEdge;

    /// <summary>
    /// Which endpoints have rejected which optional field, remembered for the life of the
    /// process and keyed by address.
    ///
    /// Static on purpose. A translator is built fresh for every single translation, so a
    /// per-instance memory would forget immediately and every translation would pay for
    /// the same rejected request again — one guaranteed wasted round trip per capture,
    /// forever, for anyone whose service does not know the field.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> Unsupported = new();

    private const string FieldStreamUsage = "stream_options";
    private const string FieldNoThinking = "enable_thinking";

    private bool Supports(string field) => !Unsupported.ContainsKey(Key(field));

    private void MarkUnsupported(string field) => Unsupported.TryAdd(Key(field), 0);

    private string Key(string field) => $"{NormalizeBase(_settings.BaseUrl)}|{_settings.Model}|{field}";

    /// <param name="vision">
    /// Whether to put the picture in the message. Comes from the caller rather than from
    /// the settings type, because 全屏翻译 and 看图翻译 are different routes that both send
    /// images, while 识文翻译 sends none.
    /// </param>
    public OpenAiCompatibleTranslator(RouteSettings settings, bool vision)
    {
        _settings = settings;
        _apiKey = SecureStore.Unprotect(settings.ApiKeyProtected);
        _timeoutSeconds = Math.Clamp(settings.TimeoutSeconds, 5, 300);
        _vision = vision;
        _maxImageEdge = settings.MaxImageEdge;
    }

    public string Id => _vision ? VisionTranslatorId : OpenAiSettings.TranslatorId;

    public const string VisionTranslatorId = "openai-compatible-vision";

    public string DisplayName => _vision ? "看图直翻（多模态大模型）" : "大模型（OpenAI 兼容接口）";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_settings.BaseUrl)
        && !string.IsNullOrWhiteSpace(_settings.Model)
        && !string.IsNullOrWhiteSpace(_apiKey);

    // ------------------------------------------------------------------ public

    public Task<TranslationOutcome> TranslateAsync(
        TranslationRequest request,
        IProgress<string>? onPartial = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) return Task.FromResult(TranslationOutcome.NotConfigured());

        // Batch first: it is the only mode that works on both routes, with or without a
        // picture, so it must not be shadowed by the vision guard below.
        if (request.Segments is { Count: > 0 } segments)
        {
            var numbered = BatchFormat.BuildInput(segments);
            var prompt = BuildBatchPrompt(request, segments.Count);

            return _vision && request.Image is not null
                ? SendVisionAsync(request, prompt, numbered, onPartial, cancellationToken)
                : SendAsync(prompt, numbered, onPartial, cancellationToken);
        }

        if (_vision)
        {
            if (request.Image is null)
            {
                return Task.FromResult(TranslationOutcome.Error(TranslationStatus.Failed,
                    "看图直翻需要图片，但这次请求没有带上截图。"));
            }

            return SendVisionAsync(request, BuildVisionPrompt(request), "", onPartial, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(request.Text))
            return Task.FromResult(TranslationOutcome.Success("", 0));

        return SendAsync(BuildSystemPrompt(request), request.Text, onPartial, cancellationToken);
    }

    public Task<TranslationOutcome> TestAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) return Task.FromResult(TranslationOutcome.NotConfigured());

        // A real but minimal translation: exercises address, key, and model in one go.
        // Deliberately asks for a streamed reply even though it throws the pieces away, so
        // that "测试连接" fails on a service that rejects stream:true instead of passing
        // here and then breaking on the first real capture.
        if (!_vision)
        {
            return SendAsync("You are a translator. Reply with the Simplified Chinese translation only.",
                "hello", NullProgress.Instance, cancellationToken);
        }

        // The vision test has to include a real picture of text. A model that cannot see
        // will happily answer the prompt anyway, so a text-only probe would report success
        // for a model that is about to fail on every capture.
        using var probe = ImageEncoder.BuildProbeImage();
        return SendAsync(
            "读出图片里的文字，翻译成简体中文，只输出译文。",
            "", NullProgress.Instance, cancellationToken, Encode(probe));
    }

    /// <summary>
    /// Instructions for translating many blocks at once. The output format is the whole
    /// game here: the caller has to be able to put every translation back over the exact
    /// words it came from, so a reply that merges two blocks or renumbers them is worse
    /// than useless. Hence one line per block, numbered, and an explicit count.
    /// </summary>
    private string BuildBatchPrompt(TranslationRequest request, int count)
    {
        var target = DescribeLanguage(request.TargetLanguage);

        var sb = new StringBuilder();
        sb.Append($"下面是从一张屏幕截图里识别出来的 {count} 段文字，每段前面有一个方括号编号。");
        sb.Append($"把每一段分别翻译成{target}。");

        if (_vision)
        {
            // The picture is the authority. OCR on a game font or a coloured background
            // mangles characters, and a model that can see will fix them silently.
            sb.Append("附带的图片就是这些文字所在的屏幕，识别结果可能有错字或断行，以图片为准。");
        }
        else
        {
            sb.Append("这些文字来自文字识别，可能有个别错字或多余空格，请结合上下文合理推断原意。");
        }

        sb.Append($"严格按「[编号] 译文」的格式逐行输出，一共 {count} 行，编号从 1 到 {count}，");
        sb.Append("顺序不能变、不能合并、不能漏、不能多。");
        sb.Append("每一行只写译文本身，不要解释、不要重复原文、不要加引号、不要用 Markdown。");

        // Spelled out rather than left to "translate everything": on a screen full of UI
        // the model starts treating short foreign labels as names and echoing them back,
        // and the user sees a page that is half translated for no visible reason.
        sb.Append($"**不管某一段是英文、日文、韩文还是别的什么语言，都必须翻译成{target}**，");
        sb.Append("包括只有一两个词的短句、菜单项、按钮文字和标题。");
        sb.Append($"只有两种情况可以原样输出：那一段本来就是{target}，或者它是纯数字、纯符号、网址、文件名这类没有可翻译内容的东西。");
        sb.Append("拿不准的时候一律翻译。");
        sb.Append("译文尽量简短，因为它要放回原文占的位置上。");

        if (request.InsistOnTranslating)
        {
            sb.Append("注意：下面这些是上一轮你原样返回、没有翻译的段落。");
            sb.Append($"请重新认真翻译，把它们全部译成{target}，不要再原样返回。");
        }

        if (!string.IsNullOrWhiteSpace(request.ExtraPrompt))
        {
            sb.Append("额外要求：").Append(request.ExtraPrompt.Trim());
        }

        return sb.ToString();
    }

    /// <summary>
    /// Encoding runs off the UI thread: a full-screen crop is several megapixels, and both
    /// the PNG pass and the base64 of its result are long enough to be felt as a stutter
    /// right at the moment the popup is supposed to appear.
    /// </summary>
    private async Task<TranslationOutcome> SendVisionAsync(
        TranslationRequest request, string prompt, string userText,
        IProgress<string>? onPartial, CancellationToken cancellationToken)
    {
        var image = request.Image!;
        var encoded = await Task.Run(() => Encode(image), cancellationToken).ConfigureAwait(false);
        return await SendAsync(prompt, userText, onPartial, cancellationToken, encoded).ConfigureAwait(false);
    }

    private EncodedImage Encode(System.Drawing.Bitmap image) => ImageEncoder.Encode(image, _maxImageEdge);

    /// <summary>Asks for a stream without keeping the pieces.</summary>
    private sealed class NullProgress : IProgress<string>
    {
        public static readonly NullProgress Instance = new();
        public void Report(string value) { }
    }

    // ------------------------------------------------------------------ prompt

    private static string BuildSystemPrompt(TranslationRequest request)
    {
        var target = DescribeLanguage(request.TargetLanguage);

        var sb = new StringBuilder();
        sb.Append($"你是一个翻译引擎。把用户给出的文本翻译成{target}。");
        // The input came from screen OCR, so telling the model that is worth real accuracy:
        // it will reinterpret obvious misrecognitions instead of translating them literally.
        sb.Append("这段文本来自屏幕截图的文字识别，可能有个别错字、断行或多余空格，请结合上下文合理推断原意。");
        sb.Append("只输出译文本身，不要解释、不要加引号、不要重复原文。保留原有的换行结构。");
        sb.Append($"如果文本已经是{target}，原样返回。");

        if (!string.IsNullOrWhiteSpace(request.ExtraPrompt))
        {
            sb.Append("额外要求：").Append(request.ExtraPrompt.Trim());
        }

        return sb.ToString();
    }

    /// <summary>
    /// Instructions for the picture route. Everything goes in the user message rather than
    /// a system message: compatible vision endpoints disagree about whether a system role
    /// is allowed alongside an image, and this shape is accepted by all of them.
    /// </summary>
    private static string BuildVisionPrompt(TranslationRequest request)
    {
        var target = DescribeLanguage(request.TargetLanguage);

        var sb = new StringBuilder();
        sb.Append($"这是用户从电脑屏幕上框选的一块区域。请读出图里的所有文字，并翻译成{target}。");
        // The whole reason this route exists: one engine reading the picture handles mixed
        // scripts in one pass, where the OCR route has to pick a single language and gets
        // the rest wrong.
        sb.Append("图里可能同时有好几种语言，全部都要翻译，不要漏掉任何一段。");
        sb.Append($"已经是{target}的部分原样输出。");
        sb.Append("只输出译文本身：不要描述图片、不要输出原文、不要解释、不要加引号、不要用 Markdown。");
        sb.Append("尽量保留原来的换行和段落顺序，按人阅读的顺序从上到下、从左到右输出。");
        sb.Append("如果图里没有任何文字，就只回答「没有找到文字」。");

        if (request.WantOriginal) sb.Append(VisionReply.Instruction);

        if (!string.IsNullOrWhiteSpace(request.ExtraPrompt))
        {
            sb.Append("额外要求：").Append(request.ExtraPrompt.Trim());
        }

        return sb.ToString();
    }

    private static string DescribeLanguage(string tag) => TargetLanguages.Describe(tag);

    // ------------------------------------------------------------------- http

    /// <param name="image">
    /// Non-null puts the picture in the user message and <paramref name="systemPrompt"/>
    /// alongside it as text. Null keeps the original two-message text shape byte for byte,
    /// so the route that was already verified sends exactly what it always sent.
    /// </param>
    private async Task<TranslationOutcome> SendAsync(
        string systemPrompt, string userText, IProgress<string>? onPartial, CancellationToken cancellationToken,
        EncodedImage? image = null)
    {
        var sw = Stopwatch.StartNew();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

        var endpoint = BuildEndpoint(_settings.BaseUrl);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return TranslationOutcome.Error(TranslationStatus.EndpointNotFound,
                $"接口地址不是有效的网址：{_settings.BaseUrl}\n应该以 http:// 或 https:// 开头。");
        }

        var streaming = onPartial is not null;

        // Streamed replies carry no usage unless asked for, and the flag is not universal:
        // a service that does not know it can reject the whole request with a 400. So it
        // goes out optimistically and is dropped on the one error it could have caused —
        // knowing the token count is never worth losing the translation.
        var askForUsage = streaming && Supports(FieldStreamUsage);
        var askNoThinking = Supports(FieldNoThinking);

        // Lives outside the try so every failure path can still hand back whatever already
        // arrived. Half a translation the user has been watching appear is worth more than
        // an error message that wipes it off the screen.
        var received = new StringBuilder();

        try
        {
            // Built as a dictionary rather than an anonymous type so that optional fields
            // are simply absent instead of being sent as null. A service that does not
            // know "stream_options" should never have to see it at all.
            var payload = new Dictionary<string, object?>
            {
                ["model"] = _settings.Model,
                ["temperature"] = 0.2,
                ["stream"] = streaming,
            };

            payload["messages"] = image is null
                ? new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userText },
                }
                : new object[]
                {
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new { type = "image_url", image_url = new { url = image.DataUri } },
                            new
                            {
                                type = "text",
                                // One text part, not two: a vision message that also
                                // carried a system role is accepted by some services and
                                // rejected by others, so the instructions and the payload
                                // travel together.
                                text = userText.Length == 0
                                    ? systemPrompt
                                    : systemPrompt + "\n\n" + userText,
                            },
                        },
                    },
                };

            if (askForUsage) payload["stream_options"] = new { include_usage = true };

            // Reasoning models spend most of their time thinking, and thinking does not
            // get shorter when the answer does: one measured batch produced 188 characters
            // after burning 3595 completion tokens, and took 42 seconds. Translation does
            // not benefit from deliberation, so it is switched off where the vendor
            // supports the switch — and dropped again if the vendor rejects the field.
            if (askNoThinking) payload["enable_thinking"] = false;

            using var message = new HttpRequestMessage(HttpMethod.Post, uri);
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            message.Content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            // ResponseHeadersRead is what makes a stream actually stream: the default waits
            // for the entire body before returning, which would defeat the whole point.
            using var response = await ClientFor(uri).SendAsync(
                message,
                streaming ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead,
                timeout.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

                // Both extras are optional niceties, and a 400 is the one error they could
                // have caused. They are dropped one at a time and the request goes out
                // again; neither is ever worth losing a translation over.
                if (response.StatusCode == HttpStatusCode.BadRequest && (askNoThinking || askForUsage))
                {
                    if (askNoThinking)
                    {
                        MarkUnsupported(FieldNoThinking);
                        Log.Info($"{_settings.Model} 不认识 enable_thinking，去掉它重发一次（以后不再带）");
                    }
                    else
                    {
                        MarkUnsupported(FieldStreamUsage);
                        Log.Info($"{_settings.Model} 不认识 stream_options，改成不要 token 用量后重发一次（以后不再带）");
                    }

                    return await SendAsync(systemPrompt, userText, onPartial, cancellationToken, image)
                        .ConfigureAwait(false);
                }

                return MapHttpError(response.StatusCode, errorBody);
            }

            // Asking for a stream is not the same as getting one: compatible services do
            // accept stream:true and then reply with an ordinary JSON body. Trusting the
            // request instead of the reply would leave those users at an empty popup, so
            // the content type decides which reader runs.
            var isEventStream = string.Equals(
                response.Content.Headers.ContentType?.MediaType, "text/event-stream",
                StringComparison.OrdinalIgnoreCase);

            if (streaming && isEventStream)
                return await ReadEventStreamAsync(response, timeout, onPartial!, received, sw).ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

            var usage = ExtractUsage(body);
            var text = ExtractContent(body);
            if (text is null)
            {
                Log.Warn($"翻译返回内容无法解析：{Truncate(body, 400)}");
                return TranslationOutcome.Error(TranslationStatus.Failed,
                    "服务返回了看不懂的内容，可能这个地址不是标准的 OpenAI 兼容接口。",
                    Truncate(body, 400));
            }

            text = text.Trim();
            if (streaming) Log.Info("服务没有按流式返回，已按整段处理");

            // Reported even when it arrived in one piece, so the caller's display path is
            // identical either way and never has to ask whether streaming happened.
            onPartial?.Report(text);

            Log.Info($"翻译成功，{text.Length} 字，用时 {sw.ElapsedMilliseconds}ms{DescribeUsage(usage)}");
            return TranslationOutcome.Success(text, sw.ElapsedMilliseconds, usage: usage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return TranslationOutcome.Error(TranslationStatus.Cancelled, "已取消。");
        }
        catch (OperationCanceledException)
        {
            if (Salvage(received, sw, "超时") is { } partial) return partial;

            Log.Warn($"翻译超时（{_timeoutSeconds} 秒）");
            return TranslationOutcome.Error(TranslationStatus.Timeout,
                $"等了 {_timeoutSeconds} 秒还没回应。可以在设置里把超时调长，或者换个更快的模型。");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            if (Salvage(received, sw, "连接中断") is { } partial) return partial;

            Log.Error("翻译请求网络失败", ex);
            return TranslationOutcome.Error(TranslationStatus.NetworkError,
                "连不上翻译服务。检查一下网络，以及设置里的「接口地址」有没有写错。",
                ex.Message);
        }
        catch (Exception ex)
        {
            if (Salvage(received, sw, ex.GetType().Name) is { } partial) return partial;

            Log.Error("翻译请求失败", ex);
            return TranslationOutcome.Error(TranslationStatus.Failed, ex.Message, ex.ToString());
        }
    }

    /// <summary>
    /// Turns a mid-stream failure into a usable-but-flagged result. Returns null when
    /// nothing had arrived yet, in which case the caller reports the failure normally.
    /// </summary>
    private static TranslationOutcome? Salvage(StringBuilder received, Stopwatch sw, string reason)
    {
        var text = received.ToString().Trim();
        if (text.Length == 0) return null;

        Log.Warn($"流式输出中断（{reason}），保留已收到的 {text.Length} 字");
        return TranslationOutcome.Success(text, sw.ElapsedMilliseconds, truncated: true);
    }

    /// <summary>
    /// Reads an OpenAI-style server-sent-event stream: a run of "data: {json}" lines
    /// ending with "data: [DONE]", each carrying the next few characters of the answer.
    /// </summary>
    private async Task<TranslationOutcome> ReadEventStreamAsync(
        HttpResponseMessage response, CancellationTokenSource timeout,
        IProgress<string> onPartial, StringBuilder received, Stopwatch sw)
    {
        var truncated = false;
        var sawDone = false;
        var chunks = 0;
        var usage = default(TokenUsage);

        var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);

            while (true)
            {
                var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                if (line is null) break;

                // Every line restarts the clock. The timeout has to mean "the service went
                // quiet", not "the answer is long" - a whole-request budget sized for a
                // one-line caption would cut a paragraph off halfway through.
                timeout.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

                if (line.Length == 0) continue;
                // Skips ": keep-alive" comments and any other field (event:, id:, retry:).
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

                var chunk = line[5..].Trim();
                if (chunk.Length == 0) continue;
                if (chunk == "[DONE]") { sawDone = true; break; }

                // The usage-bearing chunk arrives last and has no content of its own.
                if (ReadUsage(chunk) is { HasValue: true } chunkUsage) usage = chunkUsage;

                var (delta, finishReason) = ReadStreamChunk(chunk);
                if (string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase)) truncated = true;
                if (string.IsNullOrEmpty(delta)) continue;

                received.Append(delta);
                chunks++;
                onPartial.Report(received.ToString());
            }
        }

        var text = received.ToString().Trim();
        if (text.Length == 0)
        {
            Log.Warn("流式响应里没有任何译文内容");
            return TranslationOutcome.Error(TranslationStatus.Failed,
                "服务接受了请求，但一个字也没返回。换个模型试试，或者把设置里的「边翻译边显示」关掉。");
        }

        // Not an error by itself - some services just close the connection instead of
        // sending the terminator - but worth a log line when output looks cut off.
        if (!sawDone) Log.Warn("流式响应没有收到结束标记就断开了");

        Log.Info($"翻译成功（流式），{text.Length} 字 / {chunks} 段，用时 {sw.ElapsedMilliseconds}ms"
                 + (truncated ? "，被模型的输出长度上限截断" : "") + DescribeUsage(usage));
        return TranslationOutcome.Success(text, sw.ElapsedMilliseconds, truncated, usage);
    }

    /// <summary>Pulls the incremental text out of one streamed chunk. Never throws.</summary>
    private static (string? Delta, string? FinishReason) ReadStreamChunk(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, null);

            if (!root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0) return (null, null);

            var first = choices[0];
            if (first.ValueKind != JsonValueKind.Object) return (null, null);

            var finishReason = first.TryGetProperty("finish_reason", out var finish)
                               && finish.ValueKind == JsonValueKind.String
                ? finish.GetString()
                : null;

            // "delta" carries the new characters. Reasoning models also emit
            // "reasoning_content" here; that is the model thinking out loud rather than the
            // translation, so it is deliberately ignored.
            if (!first.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
                return (null, finishReason);
            if (!delta.TryGetProperty("content", out var content)) return (null, finishReason);

            return (ReadContentValue(content), finishReason);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Accepts either the base ("https://api.deepseek.com/v1") or a full endpoint pasted
    /// straight from the vendor's docs.
    /// </summary>
    private static string BuildEndpoint(string baseUrl) => NormalizeBase(baseUrl) + "/chat/completions";

    /// <summary>The address with any pasted endpoint suffix stripped back off.</summary>
    private static string NormalizeBase(string baseUrl)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        const string suffix = "/chat/completions";
        return trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^suffix.Length]
            : trimmed;
    }

    // ---------------------------------------------------------- model catalog

    /// <summary>
    /// Asks the service what it offers, via the OpenAI-compatible GET /models. This exists
    /// so the model name never has to be guessed from documentation — a wrong one is the
    /// single most common reason a correctly-configured key still fails.
    /// </summary>
    public async Task<ModelListOutcome> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.BaseUrl) || string.IsNullOrWhiteSpace(_apiKey))
            return ModelListOutcome.Error("要先填好「接口地址」和「API Key」才能拉取模型列表。");

        var url = NormalizeBase(_settings.BaseUrl) + "/models";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return ModelListOutcome.Error($"接口地址不是有效的网址：{_settings.BaseUrl}");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, uri);
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            using var response = await ClientFor(uri).SendAsync(
                message, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var mapped = MapHttpError(response.StatusCode, body);
                return ModelListOutcome.Error(mapped.Message);
            }

            var models = ExtractModelIds(body);
            if (models.Count == 0)
            {
                Log.Warn($"模型列表解析不出内容：{Truncate(body, 300)}");
                return ModelListOutcome.Error(
                    "服务返回了模型列表，但里面没有能识别的模型名。可能这家不支持标准的 /models 接口，手动填模型名即可。");
            }

            Log.Info($"拉取到 {models.Count} 个模型：{string.Join(", ", models.Take(12))}");
            return ModelListOutcome.Success(models);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ModelListOutcome.Error("已取消。");
        }
        catch (OperationCanceledException)
        {
            return ModelListOutcome.Error($"等了 {_timeoutSeconds} 秒还没回应。");
        }
        catch (HttpRequestException ex)
        {
            Log.Error("拉取模型列表网络失败", ex);
            return ModelListOutcome.Error("连不上服务。检查网络，以及「接口地址」有没有写错。");
        }
        catch (Exception ex)
        {
            Log.Error("拉取模型列表失败", ex);
            return ModelListOutcome.Error(ex.Message);
        }
    }

    /// <summary>
    /// Reads model ids out of whatever shape the service replied with. The OpenAI form is
    /// {"data":[{"id":"..."}]}, but compatible services vary enough that the alternatives
    /// are worth handling rather than telling the user their service is broken.
    /// </summary>
    private static List<string> ExtractModelIds(string body)
    {
        var ids = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            var array = root.ValueKind switch
            {
                JsonValueKind.Array => root,
                JsonValueKind.Object when root.TryGetProperty("data", out var data)
                    && data.ValueKind == JsonValueKind.Array => data,
                JsonValueKind.Object when root.TryGetProperty("models", out var models)
                    && models.ValueKind == JsonValueKind.Array => models,
                _ => default,
            };

            if (array.ValueKind != JsonValueKind.Array) return ids;

            foreach (var item in array.EnumerateArray())
            {
                var id = item.ValueKind switch
                {
                    JsonValueKind.String => item.GetString(),
                    JsonValueKind.Object when item.TryGetProperty("id", out var idProp)
                        && idProp.ValueKind == JsonValueKind.String => idProp.GetString(),
                    JsonValueKind.Object when item.TryGetProperty("name", out var nameProp)
                        && nameProp.ValueKind == JsonValueKind.String => nameProp.GetString(),
                    _ => null,
                };

                if (!string.IsNullOrWhiteSpace(id)) ids.Add(id!);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return ids;
        }

        return ids.Distinct(StringComparer.OrdinalIgnoreCase)
                  .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                  .ToList();
    }

    private TranslationOutcome MapHttpError(HttpStatusCode status, string body)
    {
        var detail = ExtractErrorMessage(body) ?? Truncate(body, 300);
        Log.Warn($"翻译服务返回 {(int)status} {status}：{Truncate(body, 400)}");

        var (kind, message) = status switch
        {
            HttpStatusCode.Unauthorized => (TranslationStatus.AuthFailed,
                "API Key 不对或者已经失效了。到设置 → 翻译里重新粘一次。"),

            HttpStatusCode.Forbidden => (TranslationStatus.AuthFailed,
                "这个 API Key 没有访问权限，可能是没开通对应的模型，或者 Key 被禁用了。"),

            HttpStatusCode.PaymentRequired => (TranslationStatus.QuotaExceeded,
                "账户余额不足，需要去服务商那边充值。"),

            HttpStatusCode.NotFound => (TranslationStatus.EndpointNotFound,
                "接口地址不对，服务器上没有这个路径。地址一般填到 /v1 这一层。"),

            HttpStatusCode.TooManyRequests => (TranslationStatus.RateLimited,
                "请求太频繁被限流了，也可能是免费额度用完了。等一会儿再试。"),

            HttpStatusCode.BadRequest => (TranslationStatus.BadRequest,
                $"服务不接受这个请求，最常见的原因是「模型」名字写错了（当前填的是 {_settings.Model}）。"),

            // A gateway status often comes from a proxy in between rather than the service
            // itself, so this must not confidently blame the service.
            HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout
                or HttpStatusCode.ServiceUnavailable => (TranslationStatus.ServerError,
                "请求没能送到翻译服务。可能是服务暂时不可用，也可能是中间的代理／VPN 拦下了它 —— "
                + "如果你在用代理，检查一下它是否正常，以及这个地址是否需要走代理。"),

            >= HttpStatusCode.InternalServerError => (TranslationStatus.ServerError,
                "翻译服务自己出错了，跟你的配置无关，过一会儿再试。"),

            _ => (TranslationStatus.Failed, $"翻译服务返回了 {(int)status}。"),
        };

        // The service's own explanation is usually more specific than ours; show both.
        if (!string.IsNullOrWhiteSpace(detail))
            message = $"{message}\n\n服务返回：{detail}";

        return TranslationOutcome.Error(kind, message, detail);
    }

    // ------------------------------------------------------------------- json

    // Both readers below must be total: they run on whatever a third-party service replied
    // with, and an exception escaping here would surface a raw CLR message instead of the
    // plain-language one the caller already worked out. JsonElement is unforgiving -
    // TryGetProperty throws on a non-object and GetString throws on a non-string - so every
    // access is guarded by ValueKind and the catch covers InvalidOperationException too.

    private static string? ExtractContent(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (!root.TryGetProperty("choices", out var choices)) return null;
            if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) return null;

            var first = choices[0];
            if (first.ValueKind != JsonValueKind.Object) return null;
            if (!first.TryGetProperty("message", out var messageElement)
                || messageElement.ValueKind != JsonValueKind.Object) return null;
            if (!messageElement.TryGetProperty("content", out var content)) return null;

            return ReadContentValue(content);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a message's "content", which is a plain string on every text service but can
    /// be a list of typed parts on a vision one. Returning null for the list form would
    /// show "服务返回了看不懂的内容" for a reply that is perfectly fine.
    /// </summary>
    private static string? ReadContentValue(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String) return content.GetString();
        if (content.ValueKind != JsonValueKind.Array) return null;

        var sb = new StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String) { sb.Append(part.GetString()); continue; }
            if (part.ValueKind != JsonValueKind.Object) continue;
            if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                sb.Append(text.GetString());
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    /// <summary>Reads the usage block from a whole response body. Absent is normal.</summary>
    private static TokenUsage ExtractUsage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return ReadUsageElement(document.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return default;
        }
    }

    /// <summary>Same, for one streamed chunk — the last one usually carries it.</summary>
    private static TokenUsage ReadUsage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return ReadUsageElement(document.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return default;
        }
    }

    private static TokenUsage ReadUsageElement(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return default;
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return default;

        return new TokenUsage(Read("prompt_tokens"), Read("completion_tokens"));

        int? Read(string name) =>
            usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                ? value.GetInt32()
                : null;
    }

    private static string DescribeUsage(TokenUsage usage) =>
        usage.HasValue ? $"，{usage.PromptTokens ?? 0}+{usage.CompletionTokens ?? 0} token" : "";

    private static string? ExtractErrorMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String) return error.GetString();
                if (error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.String)
                {
                    return message.GetString();
                }
            }

            // Some Chinese vendors return a flat {"message": "..."} or {"msg": "..."}.
            if (root.TryGetProperty("message", out var flat) && flat.ValueKind == JsonValueKind.String)
                return flat.GetString();
            if (root.TryGetProperty("msg", out var msg) && msg.ValueKind == JsonValueKind.String)
                return msg.GetString();

            return null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) ? "" : value.Length <= max ? value : value[..max] + "…";
}
