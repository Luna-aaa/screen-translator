using ScreenTranslator.Config;
using ScreenTranslator.Ocr;
using ScreenTranslator.Translate;
using Xunit;

namespace ScreenTranslator.Tests;

/// <summary>
/// How a crop is resized before it is sent. Both directions matter and they can conflict:
/// a subtitle strip is wide and short, so the rule that enlarges it fights the rule that
/// caps the long edge.
/// </summary>
public class ImageEncoderTests
{
    [Fact]
    public void A_large_screenshot_is_shrunk_to_the_cap()
    {
        var scale = ImageEncoder.ScaleFor(2560, 1600, maxEdge: 1600);

        Assert.Equal(1600.0 / 2560, scale, 5);
    }

    [Fact]
    public void A_comfortable_crop_is_left_alone()
    {
        Assert.Equal(1.0, ImageEncoder.ScaleFor(800, 600, maxEdge: 1600));
    }

    [Fact]
    public void A_thin_subtitle_strip_is_enlarged_so_the_model_has_something_to_read()
    {
        // These models tile their input at 28px; a 40px-tall strip is barely one tile.
        var scale = ImageEncoder.ScaleFor(900, 40, maxEdge: 1600);

        Assert.True(scale > 1.0, $"expected enlargement, got {scale}");
        Assert.True(900 * scale <= 1600, "enlarging must not push the long edge past the cap");
    }

    [Fact]
    public void Shrinking_wins_when_both_rules_apply()
    {
        // Very wide and very short: the short edge begs to be enlarged, but the long edge
        // is already over budget. The cap has to win, or the request blows past the limit.
        var scale = ImageEncoder.ScaleFor(4000, 50, maxEdge: 1600);

        Assert.Equal(1600.0 / 4000, scale, 5);
    }

    [Fact]
    public void Enlargement_is_bounded_so_a_tiny_crop_cannot_explode()
    {
        var scale = ImageEncoder.ScaleFor(30, 10, maxEdge: 1600);

        Assert.True(scale <= 3.0, $"expected the 3x ceiling, got {scale}");
    }

    [Fact]
    public void A_degenerate_size_does_not_throw_or_divide_by_zero()
    {
        Assert.Equal(1.0, ImageEncoder.ScaleFor(100, 0, maxEdge: 1600));
    }
}

/// <summary>
/// Which recognizer's output to believe when the source language is "auto".
///
/// The regression that matters: Chinese read by the Japanese engine comes back as
/// plausible-looking kanji, which once made the Japanese engine win on Chinese text and
/// produced a confidently wrong translation.
/// </summary>
public class LanguageScorerTests
{
    [Fact]
    public void Chinese_text_scores_higher_on_the_Chinese_engine_than_the_Japanese_one()
    {
        const string chinese = "这是一段中文文本，用来测试识别质量和语种判定是否正确。";

        var zh = LanguageScorer.Score(chinese, "zh-Hans-CN");
        var ja = LanguageScorer.Score(chinese, "ja");

        Assert.True(zh > ja, $"Chinese engine should win on Chinese text: zh={zh}, ja={ja}");
    }

    [Fact]
    public void Real_Japanese_scores_higher_on_the_Japanese_engine()
    {
        const string japanese = "おはようございます。今日はいい天気ですね。海の向こうに小さな灯台が見えます。";

        var ja = LanguageScorer.Score(japanese, "ja");
        var zh = LanguageScorer.Score(japanese, "zh-Hans-CN");

        Assert.True(ja > zh, $"Japanese engine should win on Japanese text: ja={ja}, zh={zh}");
    }

    [Fact]
    public void English_scores_higher_on_the_English_engine()
    {
        const string english = "The lighthouse stood at the mouth of the harbour for two hundred years.";

        Assert.True(LanguageScorer.Score(english, "en-US") > LanguageScorer.Score(english, "ja"));
    }

    [Fact]
    public void Empty_text_scores_zero_rather_than_winning_by_default()
    {
        Assert.Equal(0, LanguageScorer.Score("", "en-US"));
        Assert.Equal(0, LanguageScorer.Score("   ", "zh-Hans-CN"));
    }
}

/// <summary>The languages translations can come out in, and how they are named to the model.</summary>
public class TargetLanguageTests
{
    [Theory]
    [InlineData("zh-Hans", "简体中文")]
    [InlineData("zh-CN", "简体中文")]      // a regional variant a hand-edited config may hold
    [InlineData("en-US", "英文")]
    [InlineData("ja-JP", "日文")]
    public void Describe_names_known_tags_in_Chinese_for_the_prompt(string tag, string expected)
    {
        Assert.Equal(expected, TargetLanguages.Describe(tag));
    }

    [Fact]
    public void Describe_passes_an_unknown_tag_through_instead_of_pretending_it_is_Chinese()
    {
        // Somebody who hand-edits the config to "de" wants German, and a model understands
        // the tag well enough. Quietly substituting Chinese would be the worst answer.
        Assert.Equal("de", TargetLanguages.Describe("de"));
    }

    [Fact]
    public void Find_falls_back_to_the_default_so_the_dropdown_always_has_a_selection()
    {
        Assert.Equal(TargetLanguages.Default, TargetLanguages.Find("de"));
        Assert.Equal(TargetLanguages.Default, TargetLanguages.Find(null));
    }

    [Fact]
    public void IsKnown_rejects_what_Describe_passes_through()
    {
        Assert.True(TargetLanguages.IsKnown("en"));
        Assert.False(TargetLanguages.IsKnown("de"));
        Assert.False(TargetLanguages.IsKnown(""));
    }
}
