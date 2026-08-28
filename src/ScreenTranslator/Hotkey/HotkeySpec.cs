using System.Diagnostics.CodeAnalysis;
using System.Text;
using Keys = System.Windows.Forms.Keys;

namespace ScreenTranslator.Hotkey;

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
}

/// <summary>
/// A global hotkey as stored in config ("Ctrl+Alt+Q") and as RegisterHotKey wants it
/// (modifier bitmask + virtual-key code).
/// </summary>
public sealed record HotkeySpec(HotkeyModifiers Modifiers, uint VirtualKey)
{
    public static readonly HotkeySpec Default =
        new(HotkeyModifiers.Control | HotkeyModifiers.Alt, (uint)Keys.Q);

    /// <summary>
    /// Rejects combinations that would swallow ordinary typing. Shift alone is not
    /// enough; a bare function key is, since those are rarely used for text entry.
    /// </summary>
    public bool IsUsable
    {
        get
        {
            if (VirtualKey == 0) return false;
            if (IsModifierKey((Keys)VirtualKey)) return false;

            var hasHardModifier = Modifiers.HasFlag(HotkeyModifiers.Control)
                                  || Modifiers.HasFlag(HotkeyModifiers.Alt)
                                  || Modifiers.HasFlag(HotkeyModifiers.Win);
            if (hasHardModifier) return true;

            var key = (Keys)VirtualKey;
            return key >= Keys.F1 && key <= Keys.F24;
        }
    }

    public static bool IsModifierKey(Keys key) => key
        is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey
        or Keys.Menu or Keys.LMenu or Keys.RMenu
        or Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey
        or Keys.LWin or Keys.RWin
        or Keys.None;

    public static bool TryParse(string? text, [NotNullWhen(true)] out HotkeySpec? spec)
    {
        spec = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var mods = HotkeyModifiers.None;
        uint vk = 0;

        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    mods |= HotkeyModifiers.Control;
                    continue;
                case "alt":
                    mods |= HotkeyModifiers.Alt;
                    continue;
                case "shift":
                    mods |= HotkeyModifiers.Shift;
                    continue;
                case "win":
                case "windows":
                case "meta":
                    mods |= HotkeyModifiers.Win;
                    continue;
            }

            if (!TryParseKey(raw, out var key)) return false;
            if (vk != 0) return false; // more than one non-modifier key
            vk = (uint)key;
        }

        if (vk == 0) return false;
        spec = new HotkeySpec(mods, vk);
        return true;
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) sb.Append("Ctrl+");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) sb.Append("Alt+");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) sb.Append("Shift+");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) sb.Append("Win+");
        sb.Append(KeyName((Keys)VirtualKey));
        return sb.ToString();
    }

    private static bool TryParseKey(string token, out Keys key)
    {
        foreach (var (name, k) in SpecialNames)
        {
            if (string.Equals(name, token, StringComparison.OrdinalIgnoreCase))
            {
                key = k;
                return true;
            }
        }

        if (token.Length == 1 && token[0] >= '0' && token[0] <= '9')
        {
            key = Keys.D0 + (token[0] - '0');
            return true;
        }

        return Enum.TryParse(token, ignoreCase: true, out key) && Enum.IsDefined(typeof(Keys), key);
    }

    private static string KeyName(Keys key)
    {
        foreach (var (name, k) in SpecialNames)
        {
            if (k == key) return name;
        }
        if (key >= Keys.D0 && key <= Keys.D9) return ((char)('0' + (key - Keys.D0))).ToString();
        return key.ToString();
    }

    /// <summary>Punctuation and pad keys whose enum names are unreadable.</summary>
    private static readonly (string Name, Keys Key)[] SpecialNames =
    {
        ("`", Keys.Oemtilde),
        ("-", Keys.OemMinus),
        ("=", Keys.Oemplus),
        ("[", Keys.OemOpenBrackets),
        ("]", Keys.OemCloseBrackets),
        ("\\", Keys.OemPipe),
        (";", Keys.OemSemicolon),
        ("'", Keys.OemQuotes),
        (",", Keys.Oemcomma),
        (".", Keys.OemPeriod),
        ("/", Keys.OemQuestion),
        ("Space", Keys.Space),
        ("Enter", Keys.Enter),
        ("Tab", Keys.Tab),
        ("Esc", Keys.Escape),
        ("Backspace", Keys.Back),
        ("Ins", Keys.Insert),
        ("Del", Keys.Delete),
        ("Home", Keys.Home),
        ("End", Keys.End),
        ("PgUp", Keys.PageUp),
        ("PgDn", Keys.PageDown),
        ("↑", Keys.Up),
        ("↓", Keys.Down),
        ("←", Keys.Left),
        ("→", Keys.Right),
        ("Num0", Keys.NumPad0),
        ("Num1", Keys.NumPad1),
        ("Num2", Keys.NumPad2),
        ("Num3", Keys.NumPad3),
        ("Num4", Keys.NumPad4),
        ("Num5", Keys.NumPad5),
        ("Num6", Keys.NumPad6),
        ("Num7", Keys.NumPad7),
        ("Num8", Keys.NumPad8),
        ("Num9", Keys.NumPad9),
    };
}
