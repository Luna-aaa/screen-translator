using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace ScreenTranslator.UI;

/// <summary>
/// Watches for "Esc" and "clicked somewhere else" on behalf of a window that can never
/// have keyboard focus.
///
/// The result popup is deliberately WS_EX_NOACTIVATE so it cannot steal focus from a game
/// or editor, which also means it never receives a key message. Polling GetAsyncKeyState
/// is used rather than a low-level keyboard hook on purpose: a hook sits in the system
/// input path, and a stalled callback stalls everyone's typing. Polling can only ever
/// waste a few microseconds of our own timer tick, and unlike a hotkey registration it
/// does not swallow Esc from the app underneath.
/// </summary>
internal sealed class DismissWatcher : IDisposable
{
    private const int VK_ESCAPE = 0x1B;
    private const int VK_LBUTTON = 0x01;
    private const int VK_RBUTTON = 0x02;
    private const int VK_MBUTTON = 0x04;

    private readonly DispatcherTimer _timer;
    private readonly Func<RECT?> _getWindowRect;
    private readonly Action _dismiss;

    private bool _mouseWasDown;
    private bool _disposed;
    private bool _suspended;

    /// <summary>
    /// Stops watching while the popup deliberately hands control to something else — the
    /// elevated PowerShell that installs a language pack, for instance. Without this, the
    /// user clicking that console counts as "clicked elsewhere" and closes the popup out
    /// from under the operation still running inside it.
    /// </summary>
    public void Suspend() => _suspended = true;

    public void Resume()
    {
        // Re-seed, so a button still held from whatever the user was doing does not read as
        // a fresh click the moment watching resumes.
        _mouseWasDown = AnyMouseButtonDown();
        _suspended = false;
    }

    public DismissWatcher(Func<RECT?> getWindowRect, Action dismiss)
    {
        _getWindowRect = getWindowRect;
        _dismiss = dismiss;

        // Seed from the current state so a button still held from the selection drag is
        // not mistaken for a fresh click outside.
        _mouseWasDown = AnyMouseButtonDown();

        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(40) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_disposed || _suspended) return;

        if (IsDown(VK_ESCAPE))
        {
            _dismiss();
            return;
        }

        var down = AnyMouseButtonDown();
        var pressedNow = down && !_mouseWasDown;
        _mouseWasDown = down;

        if (!pressedNow) return;

        if (!GetCursorPos(out var point)) return;

        var rect = _getWindowRect();
        if (rect is null) return;

        var inside = point.X >= rect.Value.Left && point.X < rect.Value.Right
                     && point.Y >= rect.Value.Top && point.Y < rect.Value.Bottom;

        // Clicks inside are the popup's own buttons; anything else means "I'm done here".
        if (!inside) _dismiss();
    }

    private static bool AnyMouseButtonDown() =>
        IsDown(VK_LBUTTON) || IsDown(VK_RBUTTON) || IsDown(VK_MBUTTON);

    private static bool IsDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);
}
