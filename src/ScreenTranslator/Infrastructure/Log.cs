using System.IO;
using System.Text;

namespace ScreenTranslator.Infrastructure;

/// <summary>
/// Dead-simple daily rolling file log. Logging must never be the reason the app
/// dies, so every method swallows IO failures.
/// </summary>
public static class Log
{
    private const int KeepDays = 7;

    private static readonly object Gate = new();
    private static DateTime _fileDate = DateTime.MinValue;
    private static string _file = "";
    private static bool _ready;

    public static void Init()
    {
        try
        {
            Paths.EnsureDataDir();
            RollIfNeeded();
            _ready = true;
            PruneOldLogs(KeepDays);
            Info("==== ScreenTranslator 启动 ====");
            Info($"exe   = {Paths.ExecutablePath}");
            Info($"data  = {Paths.DataDir}");
            Info($"os    = {Environment.OSVersion.VersionString}, 64bit={Environment.Is64BitOperatingSystem}");
        }
        catch
        {
            _ready = false;
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null)
    {
        Write("ERROR", ex is null ? message : $"{message}{Environment.NewLine}{ex}");
    }

    private static void Write(string level, string message)
    {
        if (!_ready) return;
        try
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} [{level,-5}] {message}{Environment.NewLine}";
            lock (Gate)
            {
                RollIfNeeded();
                File.AppendAllText(_file, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Never let logging take the app down.
        }
    }

    /// <summary>
    /// Points at today's file, switching over at midnight. Checked on every write rather
    /// than only at startup: this app is launched at login and left running for days, so a
    /// name chosen once would collect a whole week under the date the app happened to start,
    /// and the retention sweep would never run at all.
    /// </summary>
    private static void RollIfNeeded()
    {
        var today = DateTime.Now.Date;
        if (_fileDate == today && _file.Length > 0) return;

        var previous = _fileDate;
        _fileDate = today;
        _file = Path.Combine(Paths.LogDir, $"{today:yyyy-MM-dd}.log");

        if (previous != DateTime.MinValue) PruneOldLogs(KeepDays);
    }

    private static void PruneOldLogs(int keepDays)
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-keepDays);
            foreach (var f in Directory.EnumerateFiles(Paths.LogDir, "*.log"))
            {
                if (File.GetLastWriteTime(f) < cutoff) File.Delete(f);
            }
        }
        catch
        {
            // ignored
        }
    }
}
