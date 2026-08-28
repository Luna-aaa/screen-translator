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

    private readonly OpenAiSettings _settings;
    private readonly string _apiKey;
    private readonly int _timeoutSeconds;

    public OpenAiCompatibleTranslator(OpenAiSettings settings, int timeoutSeconds)
    {
        _settings = settings;
        _apiKey = SecureStore.Unprotect(settings.ApiKeyProtected);
        _timeoutSeconds = Math.Clamp(timeoutSeconds, 5, 300);
    }

    public string Id => OpenAiSettings.TranslatorId;
    public string DisplayName => "大模型（OpenAI 兼容接口）";

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
        return SendAsync("You are a translator. Reply with the Simplified Chinese translation only.",
            "hello", NullProgress.Instance, cancellationToken);
    }

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

    private static string DescribeLanguage(string tag) => tag switch
    {
        "zh-Hans" or "zh-Hans-CN" or "zh-CN" => "简体中文",
        "zh-Hant" or "zh-TW" => "繁体中文",
        "en" or "en-US" => "英文",
        "ja" or "ja-JP" => "日文",
        "ko" or "ko-KR" => "韩文",
        _ => tag,
    };

    // ------------------------------------------------------------------- http

    private async Task<TranslationOutcome> SendAsync(
        string systemPrompt, string userText, IProgress<string>? onPartial, CancellationToken cancellationToken)
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

        // Lives outside the try so every failure path can still hand back whatever already
        // arrived. Half a translation the user has been watching appear is worth more than
        // an error message that wipes it off the screen.
        var received = new StringBuilder();

        try
        {
            var payload = new
            {
                model = _settings.Model,
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userText },
                },
                temperature = 0.2,
                stream = streaming,
            };

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

            Log.Info($"翻译成功，{text.Length} 字，用时 {sw.ElapsedMilliseconds}ms");
            return TranslationOutcome.Success(text, sw.ElapsedMilliseconds);
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
                 + (truncated ? "，被模型的输出长度上限截断" : ""));
        return TranslationOutcome.Success(text, sw.ElapsedMilliseconds, truncated);
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
            if (!delta.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
                return (null, finishReason);

            return (content.GetString(), finishReason);
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

            return content.ValueKind == JsonValueKind.String ? content.GetString() : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

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
