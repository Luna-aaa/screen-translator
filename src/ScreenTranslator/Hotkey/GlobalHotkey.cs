using System.Runtime.InteropServices;
using System.Windows.Interop;
using ScreenTranslator.Infrastructure;

namespace ScreenTranslator.Hotkey;

/// <summary>Thrown when RegisterHotKey refuses the combination (usually already taken).</summary>
public sealed class HotkeyRegistrationException : Exception
{
    public HotkeySpec Spec { get; }
    public int NativeErrorCode { get; }

    public HotkeyRegistrationException(HotkeySpec spec, int nativeErrorCode)
        : base(BuildMessage(spec, nativeErrorCode))
    {
        Spec = spec;
        NativeErrorCode = nativeErrorCode;
    }

    private const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

    private static string BuildMessage(HotkeySpec spec, int code) => code switch
    {
        ERROR_HOTKEY_ALREADY_REGISTERED =>
            $"快捷键 {spec} 已经被别的程序占用了，请在设置里换一个组合。",
        _ => $"快捷键 {spec} 注册失败（系统错误码 {code}）。请在设置里换一个组合。",
    };
}

/// <summary>
/// Owns the system-wide hotkey. RegisterHotKey needs a window to post WM_HOTKEY to,
/// so we keep one hidden, never-shown top-level window around for the app's lifetime.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyId = 0xB731;
    private const uint MOD_NOREPEAT = 0x4000;

    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private readonly HwndSource _source;
    private bool _registered;
    private bool _disposed;

    public HotkeySpec? Current { get; private set; }

    /// <summary>Raised on the UI thread when the hotkey fires.</summary>
    public event Action? Pressed;

    public GlobalHotkey()
    {
        var parameters = new HwndSourceParameters("ScreenTranslator.HotkeySink")
        {
            Width = 1,
            Height = 1,
            PositionX = -32000,
            PositionY = -32000,
            WindowStyle = WS_POPUP,             // no WS_VISIBLE: never appears on screen
            ExtendedWindowStyle = WS_EX_TOOLWINDOW, // and never appears in Alt+Tab
            ParentWindow = IntPtr.Zero,
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    /// <exception cref="HotkeyRegistrationException">The combination was refused.</exception>
    public void Register(HotkeySpec spec)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Unregister();

        // MOD_NOREPEAT: holding the keys down fires once, not a stream of captures.
        var mods = (uint)spec.Modifiers | MOD_NOREPEAT;
        if (!RegisterHotKey(_source.Handle, HotkeyId, mods, spec.VirtualKey))
        {
            var err = Marshal.GetLastWin32Error();
            Log.Warn($"注册快捷键 {spec} 失败，错误码 {err}");
            throw new HotkeyRegistrationException(spec, err);
        }

        _registered = true;
        Current = spec;
        Log.Info($"已注册全局快捷键 {spec}");
    }

    public void Unregister()
    {
        if (!_registered) return;
        UnregisterHotKey(_source.Handle, HotkeyId);
        _registered = false;
        Current = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            try
            {
                Pressed?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Error("处理快捷键时出错", ex);
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unregister();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
