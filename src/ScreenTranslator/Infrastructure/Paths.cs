using System.IO;

namespace ScreenTranslator.Infrastructure;

/// <summary>
/// Every on-disk location the app touches. Kept in one place so the "where is my
/// config / log / screenshot" question has exactly one answer.
/// </summary>
public static class Paths
{
    public const string AppFolderName = "ScreenTranslator";

    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppFolderName);

    public static string LogDir { get; } = Path.Combine(DataDir, "logs");

    public static string ConfigFile { get; } = Path.Combine(DataDir, "config.json");

    /// <summary>
    /// Where cropped screenshots go when the user has not chosen a folder.
    ///
    /// Deliberately NOT the Pictures folder: on most machines it is redirected into
    /// OneDrive, so every capture would get uploaded. A second fixed drive is the safer
    /// default; only if there is none do we fall back to Pictures.
    /// </summary>
    public static string DefaultCaptureDir { get; } = ComputeDefaultCaptureDir();

    /// <summary>Resolves the configured capture folder, falling back to the default.</summary>
    public static string ResolveCaptureDir(string? configured) =>
        string.IsNullOrWhiteSpace(configured) ? DefaultCaptureDir : configured.Trim();

    private static string ComputeDefaultCaptureDir()
    {
        try
        {
            var systemDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));

            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                if (string.Equals(drive.Name, systemDrive, StringComparison.OrdinalIgnoreCase)) continue;
                return Path.Combine(drive.Name, AppFolderName, "Captures");
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
