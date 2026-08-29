using System.Drawing;
using ScreenTranslator.Ocr;
using ScreenTranslator.Overlay;
using Xunit;

namespace ScreenTranslator.Tests;

/// <summary>
/// Turning OCR's per-line output into paragraphs.
///
/// This is the step that decides whether the whole-screen result reads like language or
/// like a stack of fragments: a sentence that wrapped across three lines has to be
/// translated as one sentence. The three conditions (line gap, height ratio, horizontal
/// overlap) are all load-bearing, and each test below is one of them.
/// </summary>
public class TextGroupingTests
{
    private static OcrLineBox Line(string text, float x, float y, float w = 300, float h = 20) =>
        new(text, new RectangleF(x, y, w, h));

    [Fact]
    public void Consecutive_lines_of_one_paragraph_become_one_group()
    {
        var lines = new[]
        {
            Line("For nearly two hundred years the lighthouse", 20, 100),
            Line("stood at the mouth of the harbour, and for", 20, 126),
            Line("nearly two hundred years the fishermen", 20, 152),
        };

        var groups = TextGrouping.Build(lines);

        Assert.Single(groups);
        Assert.Equal(3, groups[0].Lines.Count);
        Assert.Contains("lighthouse stood", groups[0].Text);
    }

    [Fact]
    public void A_blank_line_between_paragraphs_starts_a_new_group()
    {
        var lines = new[]
        {
            Line("first paragraph line one", 20, 100),
            Line("first paragraph line two", 20, 126),
            Line("second paragraph starts here", 20, 210),   // a paragraph gap below
        };

        var groups = TextGrouping.Build(lines);

        Assert.Equal(2, groups.Count);
        Assert.Equal(2, groups[0].Lines.Count);
        Assert.Single(groups[1].Lines);
    }

    [Fact]
    public void Two_side_by_side_columns_are_not_stitched_together()
    {
        // Only the vertical gap would merge these; the horizontal-overlap test is what
        // keeps a two-column PDF from turning into interleaved nonsense.
        var lines = new[]
        {
            Line("left column line", 20, 100, w: 200),
            Line("right column line", 400, 104, w: 200),
        };

        var groups = TextGrouping.Build(lines);

        Assert.Equal(2, groups.Count);
    }

    [Fact]
    public void A_heading_is_not_swallowed_by_the_paragraph_under_it()
    {
        var lines = new[]
        {
            Line("The Lighthouse", 20, 100, h: 40),   // much taller type
            Line("body text follows here", 20, 148, h: 18),
        };

        var groups = TextGrouping.Build(lines);

        Assert.Equal(2, groups.Count);
    }

    [Fact]
    public void Wrapped_cjk_lines_join_without_a_space_but_latin_keeps_one()
    {
        var cjk = TextGrouping.Build(new[] { Line("今天天气", 20, 100), Line("非常好", 20, 124) });
        Assert.Equal("今天天气非常好", cjk[0].Text);

        var latin = TextGrouping.Build(new[] { Line("hello", 20, 100), Line("world", 20, 124) });
        Assert.Equal("hello world", latin[0].Text);
    }

    [Fact]
    public void Blank_lines_are_dropped_entirely()
    {
        var groups = TextGrouping.Build(new[] { Line("   ", 20, 100), Line("real", 20, 200) });

        Assert.Single(groups);
        Assert.Equal("real", groups[0].Text);
    }

    [Fact]
    public void Bounds_of_a_group_cover_all_its_lines()
    {
        var groups = TextGrouping.Build(new[]
        {
            Line("one", 20, 100, w: 100, h: 20),
            Line("two", 20, 124, w: 260, h: 20),
        });

        var bounds = groups[0].Bounds;
        Assert.Equal(20, bounds.Left);
        Assert.Equal(100, bounds.Top);
        Assert.Equal(280, bounds.Right);     // the wider second line
        Assert.Equal(144, bounds.Bottom);
    }
}
