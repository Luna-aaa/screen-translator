using System.Runtime.InteropServices;
using System.Threading;
using Clipboard = System.Windows.Forms.Clipboard;

namespace ScreenTranslator.Infrastructure;

/// <summary>
/// One place to put text on the clipboard, because doing it naively both lies to the user
/// and freezes the app for several seconds while it does.
///
/// Windows lets exactly one process have the clipboard open at a time. Putting text on it
/// is really two steps — <c>OleSetClipboard</c>, then <c>OleFlushClipboard</c> — and a
/// clipboard-history tool, password manager or Office holding it for a moment makes the
/// second one fail with <c>CLIPBRD_E_CANT_OPEN</c>. The data is already there; only the
/// flush failed. So the exception is not the verdict.
///
/// Two things then have to be got right, and the obvious version of each is wrong:
///
///  · <b>Do not verify by reading the clipboard back.</b> Reading opens it too, so it fails
///    for exactly the same reason, and .NET's read path retries ten times at 100 ms before
///    giving up. Verifying that way turns a spurious failure into a four-second freeze and
///    still reports failure. <see cref="GetClipboardSequenceNumber"/> and
///    <see cref="GetClipboardOwner"/> answer the same question without opening anything.
///
///  · <b>Do not let the write retry internally.</b> The parameterless overloads retry ten
///    times at 100 ms inside a single call, which is where the visible stutter comes from.
///    The overload taking a retry count is told to try once; retrying is this class's job,
///    and it only bothers after establishing that the write really did not land.
/// </summary>
public static class ClipboardHelper
{
    /// <summary>
    /// Spread over about half a second. The contention that causes this is brief — a
    /// clipboard-history tool grabbing it for a moment after a change — so waiting it out
    /// is what actually works. Half a second is under the threshold where a click feels
    /// like it stalled, and far below the multi-second freeze the naive version produced.
    /// </summary>
    private const int Attempts = 8;
    private const int DelayMs = 60;

    /// <summary>Must be called on the UI (STA) thread. Never throws.</summary>
    public static bool TrySetText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        Exception? last = null;

        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var before = GetClipboardSequenceNumber();

            try
            {
                // copy: true so the text survives this process exiting.
                // retryTimes: 1 so a busy clipboard fails immediately instead of blocking
                // the UI thread for a second inside the call.
                Clipboard.SetDataObject(text, copy: true, retryTimes: 1, retryDelay: 0);
                return true;
            }
            catch (Exception ex)
            {
                last = ex;
            }

            if (Landed(before))
            {
                Log.Info($"复制时报了个错但内容已经进剪贴板了，忽略：{last.Message}");
                return true;
            }

            if (attempt < Attempts - 1) Thread.Sleep(DelayMs);
        }

        // Last resort: hand the text over without flushing it.
        //
        // The flush is the step that copies the data out of this process so it survives the
        // app exiting, and it is also the step that most often fails here. Skipping it
        // leaves the clipboard holding a promise from us instead — every paste works for as
        // long as the app is running, which for a tray app that runs all day is the whole
        // time the user cares about. A copy that works until reboot beats a copy that
        // failed.
        try
        {
            Clipboard.SetDataObject(text, copy: false, retryTimes: 1, retryDelay: 0);
            Log.Info("剪贴板忙，改用不脱离本进程的方式复制（本程序退出前都能粘贴）");
            return true;
        }
        catch (Exception ex)
        {
            last = ex;
        }

        Log.Warn($"复制到剪贴板失败：{last?.Message}");
        return false;
    }

    /// <summary>
    /// Whether the write actually took, asked without opening the clipboard.
    ///
    /// The sequence number ticks on every successful change, so a different value means
    /// something was written — and we are the only one who just tried. Ownership is the
    /// second signal: when the set succeeded but the flush did not, the clipboard is still
    /// owned by this process, holding our data for delayed rendering.
    /// </summary>
    private static bool Landed(uint sequenceBefore)
    {
        if (GetClipboardSequenceNumber() != sequenceBefore) return true;

        var owner = GetClipboardOwner();
        if (owner == IntPtr.Zero) return false;

        uint ownerProcess = 0;
        GetWindowThreadProcessId(owner, ref ownerProcess);
        return ownerProcess != 0 && ownerProcess == GetCurrentProcessId();
    }

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardOwner();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, ref uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();
}
