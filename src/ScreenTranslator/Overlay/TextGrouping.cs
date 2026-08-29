using System.Drawing;
using System.Text;
using ScreenTranslator.Ocr;

namespace ScreenTranslator.Overlay;

/// <summary>
/// A run of recognized lines that reads as one piece of text, plus where it sits.
/// </summary>
/// <param name="Text">The lines joined back into a sentence, for the translator.</param>
/// <param name="Bounds">Union of the member lines, in frozen-bitmap pixels.</param>
/// <param name="Lines">The member lines, kept because line height drives the font size.</param>
public sealed record TextGroup(string Text, RectangleF Bounds, IReadOnlyList<OcrLineBox> Lines)
{
    /// <summary>Typical height of one line here — the starting point for the drawn font size.</summary>
    public float LineHeight => Lines.Count == 0 ? Bounds.Height : Bounds.Height / Lines.Count;
}

/// <summary>
/// Turns OCR's per-line output into paragraphs.
///
/// This is the step that decides whether the result reads like language or like a stack of
/// fragments. A sentence that wrapped across three lines has to be translated as one
/// sentence — translating each line on its own produces three grammatical half-thoughts
/// that are individually plausible and collectively wrong. Everything here is in service
/// of that one join.
/// </summary>
internal static class TextGrouping
{
    /// <summary>Two lines belong together only if the gap between them is small relative to their height.</summary>
    private const float MaxGapRatio = 0.85f;

    /// <summary>…and if their heights are comparable. A heading above a paragraph is not part of it.</summary>
    private const float MaxHeightRatio = 1.6f;

    /// <summary>Horizontal overlap, as a fraction of the narrower line, for two lines to be one block.</summary>
    private const float MinOverlapRatio = 0.35f;

    /// <summary>Beyond this a "paragraph" is almost certainly two things that happened to line up.</summary>
    private const int MaxLinesPerGroup = 14;

    public static List<TextGroup> Build(IReadOnlyList<OcrLineBox> lines)
    {
        var ordered = lines
            .Where(l => !string.IsNullOrWhiteSpace(l.Text))
            .OrderBy(l => l.Bounds.Top)
            .ThenBy(l => l.Bounds.Left)
            .ToList();

        var groups = new List<TextGroup>();
        var current = new List<OcrLineBox>();

        foreach (var line in ordered)
        {
            if (current.Count == 0 || BelongsWith(current[^1], line, current.Count))
            {
                current.Add(line);
                continue;
            }

            groups.Add(Finish(current));
            current = new List<OcrLineBox> { line };
        }

        if (current.Count > 0) groups.Add(Finish(current));
        return groups;
    }

    private static bool BelongsWith(OcrLineBox previous, OcrLineBox next, int countSoFar)
    {
        if (countSoFar >= MaxLinesPerGroup) return false;

        var pb = previous.Bounds;
        var nb = next.Bounds;

        var height = Math.Max(1f, Math.Min(pb.Height, nb.Height));
        var taller = Math.Max(pb.Height, nb.Height);
        if (taller / height > MaxHeightRatio) return false;

        // Vertical: the next line has to start soon after this one ends. A negative gap
        // means they overlap vertically, i.e. they sit side by side — different columns.
        var gap = nb.Top - pb.Bottom;
        if (gap > height * MaxGapRatio) return false;
        if (gap < -height * 0.5f) return false;

        // Horizontal: the two must actually sit above each other. Without this, two
        // separate columns of subtitles or a two-column PDF get stitched into nonsense.
        var overlap = Math.Min(pb.Right, nb.Right) - Math.Max(pb.Left, nb.Left);
        var narrower = Math.Max(1f, Math.Min(pb.Width, nb.Width));
        return overlap / narrower >= MinOverlapRatio;
    }

    private static TextGroup Finish(List<OcrLineBox> lines)
    {
        var bounds = lines[0].Bounds;
        foreach (var line in lines.Skip(1)) bounds = RectangleF.Union(bounds, line.Bounds);

        return new TextGroup(JoinLines(lines), bounds, lines.ToList());
    }

    /// <summary>
    /// Rejoins wrapped lines. A break between two CJK characters was only ever the edge of
    /// the box, so it closes up with nothing; a break between Latin words was a real word
    /// boundary and keeps its space.
    /// </summary>
    private static string JoinLines(List<OcrLineBox> lines)
    {
        var sb = new StringBuilder();

        foreach (var line in lines)
        {
            var text = line.Text.Trim();
            if (text.Length == 0) continue;

            if (sb.Length > 0)
            {
                var left = sb[^1];
                var right = text[0];
                if (!(LanguageScorer.IsCjkLike(left) && LanguageScorer.IsCjkLike(right)))
                    sb.Append(' ');
            }

            sb.Append(text);
        }

        return sb.ToString();
    }
}
