using NcmForge.Core.Audio;
using NcmForge.Core.Ncm;
using NcmForge.Core.Tools;

namespace NcmForge.Core.Pipeline;

/// <summary>
/// One rung of the re-encode fallback ladder: a codec to try, and the container it lands in.
/// </summary>
/// <param name="Label">Human-readable codec name for reports.</param>
/// <param name="Encoder">The ffmpeg encoder name, or <c>null</c> when the row is a placeholder.</param>
/// <param name="Extension">Output extension, including the dot.</param>
/// <param name="ExtraArguments">Encoder-specific ffmpeg arguments (bitrate, <c>-strict</c>, ...).</param>
/// <param name="MaxChannels">Most channels this codec can carry.</param>
/// <param name="Lossless">Whether the codec is lossless.</param>
/// <param name="SelfTagged">True when NcmForge's own writers handle this container's tags.</param>
/// <param name="LastResort">True for the final MP4/MP3 rungs, which warrant a warning when reached.</param>
/// <param name="Note">Caveats worth surfacing, especially where a name over-promises.</param>
public sealed record EncodeTarget(
    string Label,
    string? Encoder,
    string Extension,
    IReadOnlyList<string> ExtraArguments,
    int MaxChannels,
    bool Lossless,
    bool SelfTagged,
    bool LastResort,
    string Note);

/// <summary>
/// The ordered set of codecs a re-encode may fall back through.
/// </summary>
/// <remarks>
/// The order favours the formats that can carry a full immersive mix without discarding channels,
/// then degrades toward the ones every player understands. Two entries deliberately over-promise in
/// their conventional names and are corrected in <see cref="EncodeTarget.Note"/>:
/// ffmpeg has no <em>Dolby Atmos</em> renderer (E-AC-3 is the carrier, but the object metadata
/// cannot be authored here) and no <c>DTS-HD MA</c> encoder (`dca` produces a lossy DTS core).
/// <para>
/// Rungs whose encoder this build does not provide are removed by
/// <see cref="CodecLadder.For"/> rather than failing at encode time.
/// </para>
/// </remarks>
public static class CodecLadder
{
    /// <summary>The ladder in preference order.</summary>
    /// <remarks>
    /// The <see cref="EncodeTarget.MaxChannels"/> values are the counts these encoders were
    /// <em>measured</em> to produce, not the counts their formats theoretically allow. ffmpeg's
    /// TrueHD encoder errors above 6 channels, and its E-AC-3, DTS and ALAC encoders silently emit
    /// fewer channels than asked — so the honest ceilings are what keep a downmix deliberate
    /// instead of accidental.
    /// <para>
    /// WavPack leads because it is the only rung measured to carry a full 12-channel immersive mix
    /// losslessly. Every rung below it either caps at 8 channels or forces a downmix, so leading
    /// with it is what lets an AVS3-P3 file convert without discarding its height channels.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<EncodeTarget> Rungs { get; } =
    [
        new("WavPack", "wavpack", ".wv", [], 16, true, false, false,
            "实测可无损承载全部 12 声道（7.1.4），是本表中唯一不降混的无损选择"),

        new("Dolby TrueHD", "truehd", ".mka", ["-strict", "-2"], 6, true, false, false,
            "ffmpeg 的 TrueHD 编码器实测上限 6 声道（5.1）；需 Matroska 容器，Windows Media Player 不支持"),

        new("E-AC-3 (DD+)", "eac3", ".mka", [], 6, false, false, false,
            "ffmpeg 无法写入 Atmos 对象元数据，因此这是 DD+ 而非真正的 Atmos；实测上限 5.1"),

        new("DTS", "dca", ".mka", ["-strict", "-2"], 6, false, false, false,
            "ffmpeg 没有 DTS-HD MA 编码器，dca 产出的是有损 DTS 核心；实测上限 5.1"),

        new("FLAC", "flac", ".flac", [], 8, true, true, false,
            "上限 8 声道，超过需降混"),

        new("ALAC", "alac", ".m4a", [], 8, true, true, false,
            "实测上限 8 声道；Windows Media Player 不支持"),

        new("Opus", "libopus", ".opus", [], 8, false, false, false,
            "标准声道映射上限 8 声道"),

        // Final rungs: universally playable, hence the warning when reached.
        new("AAC-LC (MP4)", "aac", ".m4a", [], 6, false, true, true,
            "最后手段：兼容性最好，但会降混并有损"),

        new("MP3", "libmp3lame", ".mp3", [], 2, false, true, true,
            "最后手段：仅立体声"),
    ];

    /// <summary>
    /// Builds the ladder for one conversion: rungs this build cannot encode are dropped, and each
    /// remaining rung is told how many channels it should target.
    /// </summary>
    public static IReadOnlyList<EncodeTarget> For(
        int sourceChannels,
        int bitrateKbps,
        FfmpegTool? ffmpeg)
    {
        HashSet<string>? available = ffmpeg?.GetEncoderNames().ToHashSet(StringComparer.OrdinalIgnoreCase);

        var ladder = new List<EncodeTarget>();

        foreach (EncodeTarget rung in Rungs)
        {
            // A null encoder marks a rung that no build provides; skip it explicitly so the
            // ladder's intent stays readable.
            if (rung.Encoder is null)
            {
                continue;
            }

            // Without a probe, assume everything is available and let ffmpeg report otherwise.
            if (available is { Count: > 0 } && !available.Contains(rung.Encoder))
            {
                continue;
            }

            ladder.Add(rung with
            {
                ExtraArguments = WithBitrate(rung, bitrateKbps),
                MaxChannels = rung.MaxChannels,
            });
        }

        return ladder;
    }

    /// <summary>Applies the caller's bitrate to the lossy rungs, which default to no bitrate argument.</summary>
    private static IReadOnlyList<string> WithBitrate(EncodeTarget rung, int bitrateKbps)
    {
        if (rung.Lossless || bitrateKbps <= 0)
        {
            return rung.ExtraArguments;
        }

        // Multichannel needs proportionally more, but the codecs involved cap out; scaling by the
        // channel count keeps stereo from being handed a 5.1 bitrate and vice versa.
        int scaled = rung.MaxChannels >= 6 ? bitrateKbps * 2 : bitrateKbps;

        return [.. rung.ExtraArguments, "-b:a", $"{scaled}k"];
    }

    /// <summary>
    /// Reports the rung a conversion ended on, and whether the caller should be warned.
    /// </summary>
    public static bool NeedsWarning(EncodeTarget target, int sourceChannels, bool fellBack)
        => target.LastResort || fellBack || target.MaxChannels < sourceChannels;

    /// <summary>Explains, in one line, what the fallback cost.</summary>
    public static string DescribeDegradation(EncodeTarget target, int sourceChannels, int attemptIndex)
    {
        var reasons = new List<string>();

        if (attemptIndex > 0)
        {
            reasons.Add($"前 {attemptIndex} 种编码不可用");
        }

        if (target.MaxChannels < sourceChannels && sourceChannels > 0)
        {
            reasons.Add($"{sourceChannels} 声道降混为 {target.MaxChannels}");
        }

        if (target.LastResort)
        {
            reasons.Add("已降到兼容性兜底编码");
        }

        return reasons.Count == 0 ? string.Empty : string.Join("；", reasons);
    }

    /// <summary>True when the source tier means a lossless rung would not recover any quality.</summary>
    public static bool IsLosslessWasteful(AudioQualityTier tier, EncodeTarget target)
        => tier == AudioQualityTier.Lossy && target.Lossless;
}
