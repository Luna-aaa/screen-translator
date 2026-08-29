namespace ScreenTranslator.Translate;

/// <summary>What one request cost, as reported by the service. Default means "not reported".</summary>
public readonly record struct TokenUsage(int? PromptTokens, int? CompletionTokens)
{
    public bool HasValue => PromptTokens is not null || CompletionTokens is not null;

    public int Total => (PromptTokens ?? 0) + (CompletionTokens ?? 0);
}

/// <summary>
/// Why a translation did not happen. The point of splitting these out is that the popup
/// must never show "未知错误" — every failure the user can actually cause (wrong key,
/// wrong address, no balance, no network) has to name itself.
/// </summary>
public enum TranslationStatus
{
    Success,
    /// <summary>No API key / address configured yet.</summary>
    NotConfigured,
    /// <summary>401/403 — key missing, wrong, or revoked.</summary>
    AuthFailed,
    /// <summary>402 or an out-of-credit error body.</summary>
    QuotaExceeded,
    /// <summary>429 — too many requests.</summary>
    RateLimited,
    /// <summary>404, or a base URL that is not an OpenAI-compatible endpoint.</summary>
    EndpointNotFound,
    /// <summary>400 — usually a model name the service does not have.</summary>
    BadRequest,
    /// <summary>5xx.</summary>
    ServerError,
    /// <summary>DNS/TCP/TLS failure — offline, blocked, or a typo in the host.</summary>
    NetworkError,
    Timeout,
    Cancelled,
    Failed,
}

public sealed class TranslationOutcome
{
    public TranslationStatus Status { get; private init; }

    public string Text { get; private init; } = "";

    /// <summary>Plain-language message, ready to put in front of the user.</summary>
    public string Message { get; private init; } = "";

    /// <summary>Raw detail from the service, for the log and the tooltip.</summary>
    public string? Detail { get; private init; }

    public long ElapsedMs { get; private init; }

    /// <summary>
    /// What the service said this cost, when it said anything. Null is common and normal:
    /// a streamed reply only carries usage if the service was asked for it and agreed.
    /// </summary>
    public int? PromptTokens { get; private init; }

    public int? CompletionTokens { get; private init; }

    public int? TotalTokens => PromptTokens is null && CompletionTokens is null
        ? null
        : (PromptTokens ?? 0) + (CompletionTokens ?? 0);

    /// <summary>
    /// The text is usable but known to be incomplete — the model hit its output limit, or
    /// a streamed response ended early. Saying so matters: a translation that stops
    /// mid-sentence otherwise looks like the model's own (wrong) answer.
    /// </summary>
    public bool Truncated { get; private init; }

    public bool IsSuccess => Status == TranslationStatus.Success;

    public static TranslationOutcome Success(
        string text, long elapsedMs, bool truncated = false, TokenUsage usage = default) => new()
    {
        Status = TranslationStatus.Success,
        Text = text,
        ElapsedMs = elapsedMs,
        Truncated = truncated,
        PromptTokens = usage.PromptTokens,
        CompletionTokens = usage.CompletionTokens,
    };

    /// <summary>
    /// The same outcome with different text, for a caller that had to split the reply
    /// apart after the fact. Keeps the timing and truncation flag, which the popup shows.
    /// </summary>
    public TranslationOutcome WithText(string text) => new()
    {
        Status = Status,
        Text = text,
        Message = Message,
        Detail = Detail,
        ElapsedMs = ElapsedMs,
        Truncated = Truncated,
        PromptTokens = PromptTokens,
        CompletionTokens = CompletionTokens,
    };

    public static TranslationOutcome Error(TranslationStatus status, string message, string? detail = null) => new()
    {
        Status = status,
        Message = message,
        Detail = detail,
    };

    public static TranslationOutcome NotConfigured() => Error(
        TranslationStatus.NotConfigured,
        "还没配置翻译服务。右键托盘图标 → 设置 → 翻译，填上接口地址、API Key 和模型名。");
}
