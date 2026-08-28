using Microsoft.Win32;

namespace ScreenTranslator.Infrastructure;

/// <summary>
/// Run-at-login via HKCU\...\Run. Deliberately not a scheduled task: the Run key
/// needs no admin rights and is what users expect to find when they go looking.
/// </summary>
public static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ScreenTranslator";

    private static string CommandLine => $"\"{Paths.ExecutablePath}\"";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string s && !string.IsNullOrWhiteSpace(s);
        }
        catch (Exception ex)
        {
            Log.Error("读取开机自启设置失败", ex);
            return false;
        }
    }

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null) return false;

            if (enabled)
            {
                key.SetValue(ValueName, CommandLine, RegistryValueKind.String);
                Log.Info($"已启用开机自启：{CommandLine}");
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                Log.Info("已关闭开机自启");
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Error("写入开机自启设置失败", ex);
            return false;
        }
    }

    /// <summary>
    /// Makes the registry match the config, and refreshes the stored path when the
    /// .exe has been moved or renamed since it was last enabled.
    /// </summary>
    public static void Sync(bool desired)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var current = key?.GetValue(ValueName) as string;
            var present = !string.IsNullOrWhiteSpace(current);

            if (desired && (!present || !string.Equals(current, CommandLine, StringComparison.OrdinalIgnoreCase)))
            {
                SetEnabled(true);
            }
            else if (!desired && present)
            {
                SetEnabled(false);
            }
        }
        catch (Exception ex)
        {
            Log.Error("同步开机自启设置失败", ex);
        }
    }
}
