using System.Windows.Media;
// System.Drawing is in the implicit usings too, and it has its own Color/ColorConverter.
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace ScreenTranslator.UI;

/// <summary>
/// A colour scheme for the result popup.
///
/// Every colour the popup draws is named here rather than written into the XAML, because
/// the XAML looks them up as DynamicResource and the window swaps the whole set on
/// creation. Adding a scheme is one more entry in <see cref="PopupThemes.All"/>.
/// </summary>
public sealed record PopupTheme(
    string Id,
    string DisplayName,
    Color Window,
    Color Surface,
    Color Ink,
    Color Muted,
    Color Accent,
    Color Warn,
    Color Line,
    Color ButtonBackground,
    Color ButtonBorder,
    Color ButtonHover,
    Color PrimaryBackground,
    Color PrimaryInk,
    Color Selection)
{
    public override string ToString() => DisplayName;
}

public static class PopupThemes
{
    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static readonly PopupTheme Dark = new(
        "dark", "深色（默认）",
        Window: C("#FF191B23"),
        Surface: C("#FF12141B"),
        Ink: C("#FFE9EBF2"),
        Muted: C("#FF98A0B4"),
        Accent: C("#FF7C9BFF"),
        Warn: C("#FFFFC46B"),
        Line: C("#FF2C303C"),
        ButtonBackground: C("#FF262A36"),
        ButtonBorder: C("#FF383D4C"),
        ButtonHover: C("#FF333949"),
        PrimaryBackground: C("#FF3D5AFE"),
        PrimaryInk: Colors.White,
        Selection: C("#FF3D5AFE"));

    public static readonly PopupTheme Light = new(
        "light", "浅色",
        Window: C("#FFFCFCFD"),
        Surface: C("#FFF3F4F8"),
        Ink: C("#FF1B1F2A"),
        Muted: C("#FF6B7280"),
        Accent: C("#FF3D5AFE"),
        // Darker than the dark theme's amber: the same colour on white is unreadable.
        Warn: C("#FFB4530A"),
        Line: C("#FFE0E3EA"),
        ButtonBackground: C("#FFFFFFFF"),
        ButtonBorder: C("#FFD2D6DE"),
        ButtonHover: C("#FFEFF1F5"),
        PrimaryBackground: C("#FF3D5AFE"),
        PrimaryInk: Colors.White,
        Selection: C("#FF9CB4FF"));

    public static readonly PopupTheme Blue = new(
        "blue", "蓝色",
        Window: C("#FF102741"),
        Surface: C("#FF0A1C30"),
        Ink: C("#FFE7F1FF"),
        Muted: C("#FF95B4D8"),
        Accent: C("#FF74BCFF"),
        Warn: C("#FFFFCE7A"),
        Line: C("#FF1F4370"),
        ButtonBackground: C("#FF17355C"),
        ButtonBorder: C("#FF2A5486"),
        ButtonHover: C("#FF1F4675"),
        PrimaryBackground: C("#FF2E82E4"),
        PrimaryInk: Colors.White,
        Selection: C("#FF2E82E4"));

    public static readonly PopupTheme Pink = new(
        "pink", "粉色",
        Window: C("#FFFFF4F8"),
        Surface: C("#FFFDE8F1"),
        Ink: C("#FF3B2430"),
        Muted: C("#FF8E6277"),
        Accent: C("#FFD2408F"),
        Warn: C("#FFB03A18"),
        Line: C("#FFF4D2E1"),
        ButtonBackground: C("#FFFFFFFF"),
        ButtonBorder: C("#FFF0C6DA"),
        ButtonHover: C("#FFFCE9F2"),
        PrimaryBackground: C("#FFD93D91"),
        PrimaryInk: Colors.White,
        Selection: C("#FFF7A8CE"));

    public static readonly IReadOnlyList<PopupTheme> All = new[] { Dark, Light, Blue, Pink };

    /// <summary>Falls back to the dark scheme for an id this build does not know.</summary>
    public static PopupTheme Find(string? id) =>
        All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Dark;
}
