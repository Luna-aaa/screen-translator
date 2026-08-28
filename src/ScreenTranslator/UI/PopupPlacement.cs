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
    /// <param name="avoid">
    /// Rectangles already spoken for - pinned popups. Landing on top of one would undo the
    /// very thing pinning was for, and it happens easily: two captures a few lines apart
    /// produce two popups placed by the same rule, in nearly the same place.
    /// </param>
    public static Point Compute(Rectangle selection, Size popup, IReadOnlyList<Rectangle>? avoid = null)
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
        var point = new Point(x, y);

        return avoid is null || avoid.Count == 0 ? point : StepAside(point, popup, work, avoid);
    }

    /// <summary>
    /// Nudges the popup off anything it would cover: to the right first, since that keeps
    /// it at the same height as the text it belongs to, and below only when the screen has
    /// run out of width. Gives up rather than going off-screen — overlapping something is
    /// annoying, being half off the monitor is worse.
    /// </summary>
    private static Point StepAside(Point start, Size popup, Rectangle work, IReadOnlyList<Rectangle> avoid)
    {
        var point = start;

        // Bounded: each step clears one obstacle, and past a handful of pinned popups the
        // screen itself is the problem, not the placement rule.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var blocker = FirstOverlap(new Rectangle(point, popup), avoid);
            if (blocker is null) return point;

            var toTheRight = blocker.Value.Right + Gap;
            if (toTheRight + popup.Width <= work.Right)
            {
                point = new Point(toTheRight, point.Y);
                continue;
            }

            var below = blocker.Value.Bottom + Gap;
            if (below + popup.Height <= work.Bottom)
            {
                point = new Point(Clamp(start.X, work.Left, work.Right - popup.Width), below);
                continue;
            }

            break;
        }

        return point;
    }

    private static Rectangle? FirstOverlap(Rectangle candidate, IReadOnlyList<Rectangle> avoid)
    {
        foreach (var rect in avoid)
        {
            if (rect.Width > 0 && rect.Height > 0 && rect.IntersectsWith(candidate)) return rect;
        }
        return null;
    }

    private static int Clamp(int value, int min, int max) =>
        max < min ? min : Math.Clamp(value, min, max);
}
