namespace ScreenTranslator.Translate;

/// <summary>
/// Splits a vision reply that carries the transcription along with the translation.
///
/// 看图翻译 has no OCR step, so there is nothing to put behind "显示原文" unless the model
/// hands it over. Asking for both in one reply costs a few extra output tokens and no extra
/// round trip. The order matters: the translation comes first so that streaming still shows
/// the useful half filling in immediately, and the marker tells the reader where to stop.
/// </summary>
internal static class VisionReply
{
    /// <summary>
    /// Deliberately ugly ASCII. It has to be something a Chinese translation will never
    /// contain by accident, and something a model will reproduce exactly.
    /// </summary>
    public const string Marker = "===ORIGINAL===";

    /// <summary>The instruction that produces the two-part reply.</summary>
    public const string Instruction =
        "译文写完后，另起一行原样输出一行 " + Marker + "，"
        + "然后把你从图里读到的原文照抄一遍（不要翻译、不要解释）。"
        + "这一行标记和后面的原文是给程序用的，必须一字不差。";

    /// <summary>
    /// Cuts a finished reply in two. A reply with no marker is all translation — models
    /// do drop the instruction sometimes, and losing the original is much better than
    /// showing half of it as the translation.
    /// </summary>
    public static (string Translation, string Original) Split(string reply)
    {
        if (string.IsNullOrEmpty(reply)) return ("", "");

        var index = reply.IndexOf(Marker, StringComparison.Ordinal);
        if (index < 0) return (reply.Trim(), "");

        var translation = reply[..index].TrimEnd();
        var original = reply[(index + Marker.Length)..].Trim();
        return (translation.Trim(), original);
    }

    /// <summary>
    /// What to show while the reply is still arriving.
    ///
    /// The marker turns up one fragment at a time — "==", then "===ORIG" — so a plain
    /// IndexOf would let those characters flash on screen at the end of the translation
    /// before the full marker completes the match. Any trailing prefix of the marker is
    /// trimmed too.
    /// </summary>
    public static string TranslationSoFar(string partial)
    {
        if (string.IsNullOrEmpty(partial)) return partial;

        var index = partial.IndexOf(Marker, StringComparison.Ordinal);
        if (index >= 0) return partial[..index].TrimEnd();

        for (var length = Math.Min(Marker.Length - 1, partial.Length); length > 0; length--)
        {
            if (partial.AsSpan(partial.Length - length).SequenceEqual(Marker.AsSpan(0, length)))
                return partial[..^length].TrimEnd();
        }

        return partial;
    }
}
