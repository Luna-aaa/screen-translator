using System.Diagnostics;
using ScreenTranslator.Infrastructure;
using Windows.Media.Ocr;

namespace ScreenTranslator.Ocr;

/// <summary>
/// Windows ships text recognition for free, but only for the language packs actually
/// installed on the machine — and a fresh Windows usually has just the display language.
/// Silently returning "no text found" when the pack is simply missing is the single most
/// confusing failure this tool can have, so detection and a way out live here.
/// </summary>
public static class LanguagePackHelper
{
    /// <summary>Recognizer tags Windows can use right now. Re-queried every call, so an install takes effect without a restart.</summary>
    public static IReadOnlyList<string> InstalledTags()
    {
        try
        {
            return OcrEngine.AvailableRecognizerLanguages
                .Select(l => l.LanguageTag)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Error("枚举系统 OCR 语言失败", ex);
            return Array.Empty<string>();
        }
    }

    public static bool IsInstalled(string tag) =>
        InstalledTags().Any(installed => OcrLanguages.TagsMatch(installed, tag));

    /// <summary>Of the wanted tags, the ones Windows cannot recognize yet.</summary>
    public static IReadOnlyList<OcrLanguage> Missing(IEnumerable<string> wantedTags)
    {
        var installed = InstalledTags();
        return wantedTags
            .Select(OcrLanguages.Find)
            .Where(l => l is not null)
            .Select(l => l!)
            .Where(l => !installed.Any(i => OcrLanguages.TagsMatch(i, l.Tag)))
            .DistinctBy(l => l.Tag)
            .ToList();
    }

    /// <summary>
    /// Opens Settings → Language. Needs no admin rights, and is the route that still works
    /// on editions and managed machines where Add-WindowsCapability is blocked.
    /// </summary>
    public static void OpenLanguageSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:regionlanguage") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("打开系统语言设置失败", ex);
        }
    }

    /// <summary>
    /// Installs the recognizer via an elevated PowerShell. Triggers a UAC prompt; the user
    /// can decline, which is not an error.
    /// </summary>
    /// <returns>True when the recognizer is available afterwards.</returns>
    public static async Task<(bool Installed, string Message)> TryInstallAsync(OcrLanguage language)
    {
        var command =
            $"Write-Host '正在安装 {language.DisplayName} 文字识别语言包，请稍候…'; " +
            $"$r = Add-WindowsCapability -Online -Name '{language.CapabilityName}'; " +
            "if ($?) { Write-Host '完成。' } else { Write-Host '失败。'; Start-Sleep -Seconds 8 }";

        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"",
            UseShellExecute = true,
            Verb = "runas",   // UAC
        };

        try
        {
            Log.Info($"请求安装语言包 {language.CapabilityName}");
            using var process = Process.Start(startInfo);
            if (process is null) return (false, "没能启动安装程序。");

            await process.WaitForExitAsync().ConfigureAwait(true);
            Log.Info($"语言包安装进程退出，代码 {process.ExitCode}");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED - the user dismissed the UAC prompt.
            return (false, "安装被取消了（需要管理员权限）。也可以点「打开系统设置」手动添加。");
        }
        catch (Exception ex)
        {
            Log.Error("安装语言包失败", ex);
            return (false, $"安装失败：{ex.Message}。可以点「打开系统设置」手动添加。");
        }

        if (IsInstalled(language.Tag))
        {
            return (true, $"{language.DisplayName}语言包已装好，可以直接用了。");
        }

        return (false,
            $"装完之后系统仍然报告没有{language.DisplayName}识别能力。"
            + "有些版本的 Windows 需要在「设置 → 时间和语言 → 语言和区域」里把该语言添加为显示语言后才会带上识别组件。");
    }
}
