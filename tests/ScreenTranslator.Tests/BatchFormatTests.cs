using ScreenTranslator.Translate;
using Xunit;

namespace ScreenTranslator.Tests;

/// <summary>
/// The numbered wire format for whole-screen translation.
///
/// Every translation has to land back on the exact words it came from, so a misparse
/// draws the wrong sentence over the wrong paragraph — confidently, and completely wrong.
/// Models decorate this format in every way imaginable, so the parser is written to be
/// tolerant and these tests are the record of what "tolerant" has to mean.
/// </summary>
public class BatchFormatTests
{
    [Fact]
    public void BuildInput_numbers_from_one_and_flattens_newlines()
    {
        var input = BatchFormat.BuildInput(new[] { "hello", "two\nlines" });

        Assert.Equal("[1] hello\n[2] two lines", input);
    }

    [Theory]
    [InlineData("[1] 你好", "你好")]
    [InlineData("1. 你好", "你好")]
    [InlineData("【1】你好", "你好")]
    [InlineData("(1) 你好", "你好")]
    [InlineData("1: 你好", "你好")]
    [InlineData("  [1]   你好  ", "你好")]
    public void Parse_accepts_every_numbering_style_models_actually_emit(string line, string expected)
    {
        var result = BatchFormat.Parse(line, 1);

        Assert.Equal(expected, result[0]);
    }

    [Fact]
    public void Parse_ignores_the_code_fence_models_like_to_wrap_lists_in()
    {
        var reply = "```\n[1] 一\n[2] 二\n```";

        var result = BatchFormat.Parse(reply, 2);

        Assert.Equal(new[] { "一", "二" }, result);
    }

    [Fact]
    public void Parse_folds_a_wrapped_line_into_the_block_above_it()
    {
        var reply = "[1] 这一段很长\n所以模型换了行继续写\n[2] 第二段";

        var result = BatchFormat.Parse(reply, 2);

        Assert.Equal("这一段很长 所以模型换了行继续写", result[0]);
        Assert.Equal("第二段", result[1]);
    }

    [Fact]
    public void Parse_leaves_a_skipped_block_null_rather_than_shifting_the_rest_up()
    {
        // The whole point of keying on the echoed number: a missing answer must not slide
        // the following translations onto the wrong paragraphs.
        var reply = "[1] 一\n[3] 三";

        var result = BatchFormat.Parse(reply, 3);

        Assert.Equal("一", result[0]);
        Assert.Null(result[1]);
        Assert.Equal("三", result[2]);
    }

    [Fact]
    public void Parse_drops_numbers_outside_the_range_it_asked_about()
    {
        var reply = "[1] 一\n[9] 越界的";

        var result = BatchFormat.Parse(reply, 2);

        Assert.Equal("一", result[0]);
        Assert.Null(result[1]);
    }

    [Fact]
    public void Parse_returns_all_nulls_for_prose_so_the_caller_can_retry_the_batch()
    {
        // This is the real failure that once turned 45 blocks into 4: the model answered
        // in prose instead of a list. It has to be detectable, not silently half-parsed.
        var result = BatchFormat.Parse("好的，这些文字大意是关于界面的说明。", 3);

        Assert.All(result, Assert.Null);
    }

    [Fact]
    public void Parse_of_empty_reply_is_all_nulls()
    {
        Assert.All(BatchFormat.Parse("", 2), Assert.Null);
        Assert.All(BatchFormat.Parse("   ", 2), Assert.Null);
    }
}
