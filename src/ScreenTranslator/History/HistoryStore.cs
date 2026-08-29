using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using ScreenTranslator.Infrastructure;

namespace ScreenTranslator.History;

/// <summary>
/// The recent-translations list, kept in memory and mirrored to
/// %APPDATA%\ScreenTranslator\history.json so it survives a restart.
///
/// Capped hard at <see cref="MaxEntries"/>. This file holds whatever happened to be on
/// screen, which is exactly why it is bounded, is written next to the config rather than
/// anywhere shared, and can be turned off and wiped from the settings window.
/// Like the log, it must never be the reason the app misbehaves: every operation here
/// swallows IO failures.
/// </summary>
public static class HistoryStore
{
    public const int MaxEntries = 30;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly object Gate = new();
    private static List<TranslationRecord>? _entries;

    private static string File_ => Path.Combine(Paths.DataDir, "history.json");

    /// <summary>Newest first. Never null; always a copy, so callers cannot mutate the store.</summary>
    public static IReadOnlyList<TranslationRecord> All()
    {
        lock (Gate)
        {
            Load();
            return _entries!.ToList();
        }
    }

    public static int Count
    {
        get { lock (Gate) { Load(); return _entries!.Count; } }
    }

    public static void Add(TranslationRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.Original) && string.IsNullOrWhiteSpace(record.Translation)) return;

        lock (Gate)
        {
            Load();

            // Re-translating the same capture, or framing the same thing twice, should
            // refresh the existing entry rather than fill the list with near-duplicates.
            //
            // The vision route has no original at all, and matching on an empty string
            // would make every new entry delete every previous one - history that quietly
            // never holds more than a single row.
            _entries!.RemoveAll(IsDuplicateOf(record));

            _entries.Insert(0, record);
            if (_entries.Count > MaxEntries) _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
            Save();
        }
    }

    private static Predicate<TranslationRecord> IsDuplicateOf(TranslationRecord record)
    {
        if (!string.IsNullOrEmpty(record.Original))
            return e => string.Equals(e.Original, record.Original, StringComparison.Ordinal);

        return e => string.IsNullOrEmpty(e.Original)
                    && string.Equals(e.Translation, record.Translation, StringComparison.Ordinal);
    }

    public static void Clear()
    {
        lock (Gate)
        {
            _entries = new List<TranslationRecord>();
            try
            {
                if (System.IO.File.Exists(File_)) System.IO.File.Delete(File_);
                Log.Info("翻译历史已清空");
            }
            catch (Exception ex)
            {
                Log.Error("删除历史文件失败", ex);
            }
        }
    }

    // ------------------------------------------------------------------ disk

    /// <summary>Call inside the lock. Reads the file once per process.</summary>
    private static void Load()
    {
        if (_entries is not null) return;

        try
        {
            if (!System.IO.File.Exists(File_))
            {
                _entries = new List<TranslationRecord>();
                return;
            }

            var json = System.IO.File.ReadAllText(File_);
            _entries = JsonSerializer.Deserialize<List<TranslationRecord>>(json, Options)
                       ?? new List<TranslationRecord>();

            // A file edited or truncated by hand should not be able to grow the list past
            // the cap the rest of the code assumes.
            if (_entries.Count > MaxEntries) _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
            Log.Info($"已加载翻译历史 {_entries.Count} 条");
        }
        catch (Exception ex)
        {
            // Losing history is a non-event; blocking startup over it would not be.
            Log.Error("读取翻译历史失败，从空列表开始", ex);
            _entries = new List<TranslationRecord>();
        }
    }

    /// <summary>Call inside the lock.</summary>
    private static void Save()
    {
        try
        {
            Paths.EnsureDataDir();
            var tmp = File_ + ".tmp";
            System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(_entries, Options));
            System.IO.File.Move(tmp, File_, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("保存翻译历史失败", ex);
        }
    }
}
