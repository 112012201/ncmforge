namespace NcmForge.Core.Pipeline;

/// <summary>Which encoder implementation the re-encode should ask ffmpeg for.</summary>
/// <remarks>
/// The families mirror the ones ffmpeg itself exposes. Only <see cref="MediaFoundation"/> has
/// hardware/system-accelerated <em>audio</em> encoders (<c>aac_mf</c>, <c>mp3_mf</c>,
/// <c>ac3_mf</c>); every other family here is video-only, which is why the catalog reports
/// per-family audio coverage rather than pretending otherwise.
/// </remarks>
public enum HardwareAcceleration
{
    /// <summary>Use a CPU encoder. Always available and the quality reference.</summary>
    Disabled = 0,

    /// <summary>Use an accelerated encoder when one exists for the target codec, else CPU.</summary>
    Auto,

    /// <summary>Windows Media Foundation (<c>*_mf</c>). The only family with audio encoders.</summary>
    MediaFoundation,

    /// <summary>NVIDIA NVENC. Video only.</summary>
    Nvenc,

    /// <summary>NVIDIA CUDA / NVDEC-NVENC pipeline. Video only.</summary>
    NvCuda,

    /// <summary>Intel Quick Sync Video. Video only.</summary>
    Qsv,

    /// <summary>AMD AMF (VCE / UVD). Video only.</summary>
    Amf,

    /// <summary>Linux VA-API. Video only.</summary>
    VaApi,

    /// <summary>Apple VideoToolbox. Video only.</summary>
    VideoToolbox,

    /// <summary>Android MediaCodec. Video only.</summary>
    MediaCodec,

    /// <summary>Embedded VPU via V4L2 Memory-to-Memory (Adreno, Rockchip, ...). Video only.</summary>
    V4L2,
}
