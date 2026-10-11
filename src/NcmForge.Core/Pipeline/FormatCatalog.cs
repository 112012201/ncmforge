namespace NcmForge.Core.Pipeline;

/// <summary>
/// One entry in the output-format chooser.
/// </summary>
/// <param name="Label">Text shown to the user, carrying the format's trade-offs.</param>
/// <param name="Format">The high-level mode this entry maps to.</param>
/// <param name="CodecKey">
/// Set when the entry names one codec from <see cref="CodecLadder"/>; null for the modes that are
/// not a single codec, such as "automatic" or "keep the original".
/// </param>
public sealed record FormatOption(string Label, OutputFormat Format, string? CodecKey = null);

/// <summary>
/// The output formats offered to a user, in the order they should appear.
/// </summary>
/// <remarks>
/// Lives in the core library rather than the UI so the command line, the desktop window and the
/// tests all read one list. A second copy in the UI would drift the moment a codec is added.
/// </remarks>
public static class FormatCatalog
{
    /// <summary>Every format a user can choose, in display order.</summary>
    public static IReadOnlyList<FormatOption> All { get; } =
    [
        new("自动兼容（同级编码，按兼容性逐档尝试）", OutputFormat.Compatible),

        // These two are modes rather than codecs: they remux when the payload is already in that
        // format, so re-encoding an MP3 into an MP3 — losing quality for nothing — does not happen
        // when it need not. That is why the codec list below skips their keys.
        new("MP3（已是 MP3 则直接重封装）", OutputFormat.Mp3),
        new("FLAC（已是 FLAC 则直接重封装）", OutputFormat.Flac),

        .. CodecLadder.Rungs
            .Where(r => r.Key is not ("mp3" or "flac"))
            .Select(r => new FormatOption(CodecLadder.Describe(r), OutputFormat.Compatible, r.Key)),

        new("原样无损提取（不改编码，保留原始容器与声道）", OutputFormat.LosslessCopy),
    ];

    /// <summary>Finds the entry that names a given codec.</summary>
    public static FormatOption? ForCodec(string key)
        => All.FirstOrDefault(o => o.CodecKey is not null && string.Equals(o.CodecKey, key, StringComparison.OrdinalIgnoreCase));
}
