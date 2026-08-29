using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ScreenTranslator.Infrastructure;

/// <summary>One day's tally for one route.</summary>
public sealed class UsageDay
{
    /// <summary>yyyy-MM-dd, local time.</summary>
    public string Date { get; set; } = "";

    /// <summary>识文 / 看图 / 全屏.</summary>
    public string Route { get; set; } = "";

    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public int Requests { get; set; }

    public long Total => PromptTokens + CompletionTokens;
}

/// <summary>
/// Keeps a running tally of what the translations cost, in tokens.
///
/// Tokens, never money. Prices differ per vendor and per model and they change; a table of
/// them baked into the app would start out right and quietly become wrong, which is worse
/// than showing nothing. The number here is the one thing that is unambiguously true, and
/// it is the same number the vendor's billing page is counting.
///
/// Stored separately from history.json because the two have different lifetimes: history
/// is capped at 30 entries and holds what was on screen, while this is a few numbers per
/// day that nobody would want truncated after thirty translations.
/// </summary>
public static class UsageStore
{
    public const string RouteClassic = "识文";
    public const string RouteVision = "看图";
    public const string RouteSnapshot = "全屏";

    /// <summary>Long enough to answer "how much am I using this", short enough to stay tiny.</summary>
    private const int KeepDays = 30;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly object Gate = new();
    private static List<UsageDay>? _entries;

    private static string File => Path.Combine(Paths.DataDir, "usage.json");

    public static void Add(string route, int? promptTokens, int? completionTokens)
    {
        if (promptTokens is null && completionTokens is null) return;

        lock (Gate)
        {
            Load();

            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var entry = _entries!.FirstOrDefault(e => e.Date == today && e.Route == route);
            if (entry is null)
            {
                entry = new UsageDay { Date = today, Route = route };
                _entries.Add(entry);
            }

            entry.PromptTokens += promptTokens ?? 0;
            entry.CompletionTokens += completionTokens ?? 0;
            entry.Requests++;

            Prune();
            Save();
        }
    }

    /// <summary>Totals for today and for everything still kept, ready to put on screen.</summary>
    public static (long Today, long Recent, int RecentRequests) Summary()
    {
        lock (Gate)
        {
            Load();
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            return (
                _entries!.Where(e => e.Date == today).Sum(e => e.Total),
                _entries.Sum(e => e.Total),
                _entries.Sum(e => e.Requests));
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            _entries = new List<UsageDay>();
            try
            {
                if (System.IO.File.Exists(File)) System.IO.File.Delete(File);
            }
            catch (Exception ex)
            {
                Log.Warn($"清空用量记录失败：{ex.Message}");
            }
        }
    }

    /// <summary>Renders a token count the way a person reads it: 1234 → "1.2k".</summary>
    public static string Format(long tokens) =>
        tokens >= 1_000_000 ? $"{tokens / 1_000_000.0:0.0}M"
        : tokens >= 1_000 ? $"{tokens / 1_000.0:0.0}k"
        : tokens.ToString();

    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_entries))]
    private static void Load()
    {
        if (_entries is not null) return;

        try
        {
            _entries = System.IO.File.Exists(File)
                ? JsonSerializer.Deserialize<List<UsageDay>>(System.IO.File.ReadAllText(File), Options) ?? new()
                : new List<UsageDay>();
        }
        catch (Exception ex)
        {
            // A broken tally is not worth bothering the user about; start a fresh one.
            Log.Warn($"读取用量记录失败，重新开始记：{ex.Message}");
            _entries = new List<UsageDay>();
        }
    }

    private static void Prune()
    {
        if (_entries is null) return;
        var cutoff = DateTime.Now.AddDays(-KeepDays).ToString("yyyy-MM-dd");
        _entries.RemoveAll(e => string.CompareOrdinal(e.Date, cutoff) < 0);
    }

    private static void Save()
    {
        try
        {
            Paths.EnsureDataDir();
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(_entries, Options));
        }
        catch (Exception ex)
        {
            Log.Warn($"保存用量记录失败：{ex.Message}");
        }
    }
}
