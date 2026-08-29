using ScreenTranslator.Translate;
using Xunit;

namespace ScreenTranslator.Tests;

/// <summary>
/// Splitting a vision reply that carries the transcription after the translation.
///
/// The streaming half is the subtle one: the marker arrives a fragment at a time, so a
/// plain IndexOf lets "==" and "===ORIG" flash on the end of the translation before the
/// full marker completes the match.
/// </summary>
public class VisionReplyTests
{
    [Fact]
    public void Split_separates_the_translation_from_the_transcription()
    {
        var reply = $"早上好。\n{VisionReply.Marker}\nGood morning.";

        var (translation, original) = VisionReply.Split(reply);

        Assert.Equal("早上好。", translation);
        Assert.Equal("Good morning.", original);
    }

    [Fact]
    public void Split_treats_a_reply_without_the_marker_as_all_translation()
    {
        // Models do drop the instruction. Losing the original is much better than showing
        // half of it as though it were the translation.
        var (translation, original) = VisionReply.Split("早上好。天空是蓝色的。");

        Assert.Equal("早上好。天空是蓝色的。", translation);
        Assert.Equal("", original);
    }

    [Fact]
    public void Split_of_empty_is_empty()
    {
        var (translation, original) = VisionReply.Split("");

        Assert.Equal("", translation);
        Assert.Equal("", original);
    }

    [Fact]
    public void TranslationSoFar_passes_text_through_before_the_marker_appears()
    {
        Assert.Equal("早上好。", VisionReply.TranslationSoFar("早上好。"));
    }

    [Theory]
    [InlineData("=")]
    [InlineData("==")]
    [InlineData("===")]
    [InlineData("===ORIG")]
    [InlineData("===ORIGINAL=")]
    public void TranslationSoFar_hides_a_half_arrived_marker(string fragment)
    {
        // Without this the marker's characters appear on the end of the translation for a
        // fraction of a second, once per streamed reply.
        var partial = "早上好。" + fragment;

        Assert.Equal("早上好。", VisionReply.TranslationSoFar(partial));
    }

    [Fact]
    public void TranslationSoFar_cuts_everything_from_a_complete_marker_onwards()
    {
        var partial = $"早上好。{VisionReply.Marker}\nGood mor";

        Assert.Equal("早上好。", VisionReply.TranslationSoFar(partial));
    }

    [Fact]
    public void TranslationSoFar_does_not_trim_text_that_merely_ends_in_a_marker_character()
    {
        // "1+1=" ends with a prefix of the marker, so it is trimmed while streaming and
        // restored the moment more text arrives. Only a real marker survives to Split,
        // which is the function that decides the final answer.
        Assert.Equal("1+1", VisionReply.TranslationSoFar("1+1="));
        Assert.Equal("1+1=2", VisionReply.TranslationSoFar("1+1=2"));
    }
}
