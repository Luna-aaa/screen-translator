using System.Drawing;
using System.Windows.Forms;

namespace ScreenTranslator.UI;

/// <summary>
/// Decides where the result popup sits relative to the region the user framed.
/// All values are physical pixels, matching the capture path — the popup is positioned
/// with SetWindowPos rather than WPF's Left/Top for exactly that reason.
/// </summary>
internal static class PopupPlacement
{
    private const int Gap = 12;

    /// <param name="selection">Where the user drew the box, in screen pixels.</param>
    /// <param name="popup">Outer size of the popup window, in physical pixels.</param>
    public static Point Compute(Rectangle selection, Size popup)
    {
        var work = Screen.FromRectangle(selection).WorkingArea;

        // Below the selection, left edges aligned: the reading eye is already at the
        // bottom of what it just framed, and this never covers the original text.
        var x = selection.Left;
        var y = selection.Bottom + Gap;

        if (y + popup.Height > work.Bottom)
        {
            var above = selection.Top - Gap - popup.Height;
            y = above >= work.Top
                ? above
                : work.Bottom - popup.Height;   // taller than the free space on either side
        }

        x = Clamp(x, work.Left, work.Right - popup.Width);
        y = Clamp(y, work.Top, work.Bottom - popup.Height);
        return new Point(x, y);
    }

    private static int Clamp(int value, int min, int max) =>
        max < min ? min : Math.Clamp(value, min, max);
}
