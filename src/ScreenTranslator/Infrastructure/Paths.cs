using System.IO;

namespace ScreenTranslator.Infrastructure;

/// <summary>
/// Every on-disk location the app touches. Kept in one place so the "where is my
/// config / log / screenshot" question has exactly one answer.
/// </summary>
public static class Paths
{
    public const string AppFolderName = "ScreenTranslator";

    /// <summary>What the crop folder was called before the modes were split up.</summary>
    private const string LegacyCropFolderName = "Captures";

    private const string CropFolderName = "框选翻译";
    private const string SnapshotFolderName = "全屏翻译";

    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppFolderName);

    public static string LogDir { get; } = Path.Combine(DataDir, "logs");

    public static string ConfigFile { get; } = Path.Combine(DataDir, "config.json");

    /// <summary>
    /// Where the app puts pictures when the user has not chosen a folder.
    ///
    /// Deliberately NOT the Pictures folder: on most machines it is redirected into
    /// OneDrive, so every capture would get uploaded. A second fixed drive is the safer
    /// default; only if there is none do we fall back to Pictures.
    /// </summary>
    private static string MediaRoot { get; } = ComputeMediaRoot();

    /// <summary>Default folder for 框选翻译 — the cropped region, as it was on screen.</summary>
    public static string DefaultCropDir { get; } = Path.Combine(MediaRoot, CropFolderName);

    /// <summary>Default folder for 全屏翻译 — the whole screen with the translations drawn on.</summary>
    public static string DefaultSnapshotDir { get; } = Path.Combine(MediaRoot, SnapshotFolderName);

    /// <summary>Resolves a configured folder, falling back to <paramref name="fallback"/>.</summary>
    public static string Resolve(string? configured, string fallback) =>
        string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();

    public static string ResolveCropDir(string? configured) => Resolve(configured, DefaultCropDir);

    public static string ResolveSnapshotDir(string? configured) => Resolve(configured, DefaultSnapshotDir);

    /// <summary>
    /// Renames the old "Captures" folder to its new Chinese name, once.
    ///
    /// Deliberately timid: it only ever runs when the old folder exists and the new one
    /// does not, so it can neither merge two folders nor overwrite anything. Any failure
    /// is logged and ignored — the app works fine either way, and losing a user's saved
    /// screenshots to a tidy-up would be far worse than leaving a folder with an old name.
    /// </summary>
    public static void MigrateLegacyCropFolder()
    {
        try
        {
            var legacy = Path.Combine(MediaRoot, LegacyCropFolderName);
            if (!Directory.Exists(legacy)) return;
            if (Directory.Exists(DefaultCropDir))
            {
                Log.Info($"「{CropFolderName}」已存在，保留旧的 {legacy} 不动");
                return;
            }

            Directory.Move(legacy, DefaultCropDir);
            Log.Info($"截图目录已改名：{legacy} → {DefaultCropDir}");
        }
        catch (Exception ex)
        {
            Log.Warn($"截图目录改名失败（不影响使用）：{ex.Message}");
        }
    }

    private static string ComputeMediaRoot()
    {
        try
        {
            var systemDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));

            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                if (string.Equals(drive.Name, systemDrive, StringComparison.OrdinalIgnoreCase)) continue;
                return Path.Combine(drive.Name, AppFolderName);
            }
        }
        catch
        {
            // Fall through to the Pictures folder.
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), AppFolderName);
    }

    /// <summary>
    /// The real .exe on disk. Environment.ProcessPath is correct for both a normal
    /// build and a single-file publish (it points at the host, not the extracted temp dll).
    /// </summary>
    public static string ExecutablePath { get; } =
        Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";

    public static void EnsureDataDir()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogDir);
    }
}
