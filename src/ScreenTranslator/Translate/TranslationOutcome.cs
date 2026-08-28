namespace ScreenTranslator.Translate;

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

    public bool IsSuccess => Status == TranslationStatus.Success;

    public static TranslationOutcome Success(string text, long elapsedMs) => new()
    {
        Status = TranslationStatus.Success,
        Text = text,
        ElapsedMs = elapsedMs,
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
