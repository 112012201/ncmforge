using NcmForge.Core.Audio;

namespace NcmForge.Core.Pipeline;

/// <summary>Target container/codec for a conversion.</summary>
public enum OutputFormat
{
    /// <summary>MPEG-1 Layer III. Stereo only, so multichannel sources are downmixed.</summary>
    Mp3,

    /// <summary>FLAC. Lossless, limited to 8 channels by the format specification.</summary>
    Flac,

    /// <summary>
    /// Let the tool pick a codec that widespread players can decode, at the source's quality tier
    /// (lossless stays lossless, lossy stays lossy).
    /// </summary>
    Compatible,

    /// <summary>
    /// Copy the decrypted payload out byte-for-byte under its native container.
    /// This is the correct outcome for codecs no local decoder implements.
    /// </summary>
    LosslessCopy,
}

/// <summary>What to do when the source carries more channels than the target can hold.</summary>
public enum SurroundMapping
{
    /// <summary>Keep the native channel count. Only valid when the target supports it.</summary>
    Preserve,

    /// <summary>Fold everything down to stereo.</summary>
    DownmixToStereo,

    /// <summary>Fold down to 5.1 (6 channels).</summary>
    DownmixTo51,

    /// <summary>Fold down to 7.1 (8 channels), the widest layout FLAC can store.</summary>
    DownmixTo71,
}

/// <summary>How cover art is chosen.</summary>
public enum CoverMode
{
    /// <summary>Write no artwork.</summary>
    None,

    /// <summary>Use only artwork carried inside the container.</summary>
    Embedded,

    /// <summary>Use only online sources; write nothing if they all fail.</summary>
    Online,

    /// <summary>Try online sources first, then fall back to the container's own artwork.</summary>
    OnlineThenEmbedded,
}

/// <summary>What gets handed to an external decoder.</summary>
public enum ExternalDecoderInput
{
    /// <summary>The decrypted payload exactly as the container holds it.</summary>
    Payload,

    /// <summary>
    /// The raw elementary stream pulled out of an MP4 payload. Required by reference decoders that
    /// read a bare <c>.av3a</c> stream rather than a container.
    /// </summary>
    ElementaryStream,
}

/// <summary>How lyrics are obtained.</summary>
public enum LyricsMode
{
    /// <summary>Write no lyrics.</summary>
    None,

    /// <summary>Use the <c>.lrc</c> file that sits beside the container.</summary>
    Sidecar,
}

/// <summary>Everything that controls one conversion.</summary>
public sealed class ConversionOptions
{
    public OutputFormat Format { get; set; } = OutputFormat.Mp3;

    /// <summary>
    /// Re-encode even when the payload could simply be remuxed.
    /// </summary>
    /// <remarks>
    /// Off by default, because a remux is faster and leaves the audio bit-exact. Turn it on to
    /// change the codec — for example to move an exotic payload onto something every player
    /// decodes. The hardware-acceleration choice below only applies while this is on.
    /// </remarks>
    public bool ForceReencode { get; set; }

    /// <summary>Which encoder family to prefer while re-encoding.</summary>
    public HardwareAcceleration Hardware { get; set; } = HardwareAcceleration.Disabled;

    public SurroundMapping Surround { get; set; } = SurroundMapping.Preserve;

    public CoverMode Cover { get; set; } = CoverMode.OnlineThenEmbedded;

    public LyricsMode Lyrics { get; set; } = LyricsMode.Sidecar;

    /// <summary>Bitrate for lossy targets (MP3, AAC), in kbps.</summary>
    public int BitrateKbps { get; set; } = 320;

    public int FlacCompressionLevel { get; set; } = 5;

    /// <summary>Where output files are written. Defaults to the source file's directory.</summary>
    public string? OutputDirectory { get; set; }

    /// <summary>Explicit ffmpeg location; otherwise discovered from PATH.</summary>
    public string? FfmpegPath { get; set; }

    /// <summary>
    /// Transcode even when the ffmpeg capability probe does not recognise the codec. Set this when
    /// using a custom ffmpeg build whose decoder names the probe cannot predict.
    /// </summary>
    public bool ForceFfmpegDecode { get; set; }

    /// <summary>
    /// Command that decodes a codec ffmpeg cannot handle, producing a WAV file.
    /// Placeholders: <c>{input}</c>, <c>{output}</c>, <c>{channels}</c>, <c>{sampleRate}</c>.
    /// </summary>
    public string? ExternalDecoderCommand { get; set; }

    /// <summary>Whether the external decoder receives the payload or a raw elementary stream.</summary>
    public ExternalDecoderInput ExternalDecoderInput { get; set; } = ExternalDecoderInput.ElementaryStream;

    /// <summary>
    /// When the payload is an ISO base media file, extract the raw elementary stream (for example
    /// <c>.av3a</c>) instead of copying the whole container.
    /// </summary>
    public bool ExtractElementaryStream { get; set; }

    /// <summary>Keep intermediate files. Useful when diagnosing a failing conversion.</summary>
    public bool KeepTemporaryFiles { get; set; }

    /// <summary>Overwrite existing output files.</summary>
    public bool Overwrite { get; set; } = true;
}

/// <summary>What the pipeline decided to do, before it does it.</summary>
/// <param name="Action">The chosen route.</param>
/// <param name="OutputExtension">File extension for the result, including the dot.</param>
/// <param name="Reason">Why this route was chosen, for logging and for the UI.</param>
public sealed record ConversionPlan(string Action, string OutputExtension, string Reason);

/// <summary>The outcome of a completed conversion.</summary>
/// <param name="Warnings">
/// Anything the caller should surface to the user — a fallback that cost channels or quality, for
/// instance. Empty when the first choice worked.
/// </param>
public sealed record ConversionResult(
    string SourcePath,
    string OutputPath,
    string Codec,
    int SourceChannels,
    ConversionPlan Plan,
    Tagging.CoverArt? Cover,
    long OutputBytes,
    IReadOnlyList<string>? Warnings = null)
{
    public bool HasWarnings => Warnings is { Count: > 0 };
}

/// <summary>Raised when a conversion cannot proceed.</summary>
public sealed class ConversionException : Exception
{
    public ConversionException(string message) : base(message)
    {
    }
}
