using System.Diagnostics;
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
        TranslationRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) return Task.FromResult(TranslationOutcome.NotConfigured());
        if (string.IsNullOrWhiteSpace(request.Text))
            return Task.FromResult(TranslationOutcome.Success("", 0));

        return SendAsync(BuildSystemPrompt(request), request.Text, cancellationToken);
    }

    public Task<TranslationOutcome> TestAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) return Task.FromResult(TranslationOutcome.NotConfigured());

        // A real but minimal translation: exercises address, key, and model in one go.
        return SendAsync("You are a translator. Reply with the Simplified Chinese translation only.",
            "hello", cancellationToken);
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
        string systemPrompt, string userText, CancellationToken cancellationToken)
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
                stream = false,
            };

            using var message = new HttpRequestMessage(HttpMethod.Post, uri);
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            message.Content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var response = await ClientFor(uri).SendAsync(
                message, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return MapHttpError(response.StatusCode, body);

            var text = ExtractContent(body);
            if (text is null)
            {
                Log.Warn($"翻译返回内容无法解析：{Truncate(body, 400)}");
                return TranslationOutcome.Error(TranslationStatus.Failed,
                    "服务返回了看不懂的内容，可能这个地址不是标准的 OpenAI 兼容接口。",
                    Truncate(body, 400));
            }

            Log.Info($"翻译成功，{text.Length} 字，用时 {sw.ElapsedMilliseconds}ms");
            return TranslationOutcome.Success(text.Trim(), sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return TranslationOutcome.Error(TranslationStatus.Cancelled, "已取消。");
        }
        catch (OperationCanceledException)
        {
            Log.Warn($"翻译超时（{_timeoutSeconds} 秒）");
            return TranslationOutcome.Error(TranslationStatus.Timeout,
                $"等了 {_timeoutSeconds} 秒还没回应。可以在设置里把超时调长，或者换个更快的模型。");
        }
        catch (HttpRequestException ex)
        {
            Log.Error("翻译请求网络失败", ex);
            return TranslationOutcome.Error(TranslationStatus.NetworkError,
                "连不上翻译服务。检查一下网络，以及设置里的「接口地址」有没有写错。",
                ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error("翻译请求失败", ex);
            return TranslationOutcome.Error(TranslationStatus.Failed, ex.Message, ex.ToString());
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

            var array = root.ValueKind == JsonValueKind.Array ? root
                : root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array ? data
                : root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array ? models
                : default;

            if (array.ValueKind != JsonValueKind.Array) return ids;

            foreach (var item in array.EnumerateArray())
            {
                var id = item.ValueKind switch
                {
                    JsonValueKind.String => item.GetString(),
                    JsonValueKind.Object when item.TryGetProperty("id", out var idProp) => idProp.GetString(),
                    JsonValueKind.Object when item.TryGetProperty("name", out var nameProp) => nameProp.GetString(),
                    _ => null,
                };

                if (!string.IsNullOrWhiteSpace(id)) ids.Add(id!);
            }
        }
        catch (JsonException)
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

    private static string? ExtractContent(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("choices", out var choices)) return null;
            if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) return null;

            var first = choices[0];
            if (!first.TryGetProperty("message", out var messageElement)) return null;
            if (!messageElement.TryGetProperty("content", out var content)) return null;

            return content.GetString();
        }
        catch (JsonException)
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

            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String) return error.GetString();
                if (error.TryGetProperty("message", out var message)) return message.GetString();
            }

            // Some Chinese vendors return a flat {"message": "..."} or {"msg": "..."}.
            if (root.TryGetProperty("message", out var flat)) return flat.GetString();
            if (root.TryGetProperty("msg", out var msg)) return msg.GetString();

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) ? "" : value.Length <= max ? value : value[..max] + "…";
}
