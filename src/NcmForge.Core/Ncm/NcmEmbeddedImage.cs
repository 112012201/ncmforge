namespace NcmForge.Core.Ncm;

/// <summary>How the audio payload was located inside a container.</summary>
public enum NcmPayloadLayout
{
    /// <summary>The documented shape: <c>[metadata][CRC32 + 5 reserved][audio]</c>.</summary>
    Standard,

    /// <summary>
    /// Immersive shape: <c>[metadata][CRC32 + 5 reserved][uint32 cover length][cover image][audio]</c>.
    /// Seen on Audio Vivid / immersive downloads, which embed the cover in the clear.
    /// </summary>
    AfterEmbeddedImage,

    /// <summary>The layout was not recognised and the payload start was found by signature scan.</summary>
    Scanned,
}

/// <summary>Cover art carried inside the container itself, stored in the clear.</summary>
/// <param name="Offset">Absolute offset of the image's first byte in the container.</param>
/// <param name="DeclaredLength">Block length declared by the container's <c>uint32</c> field.</param>
/// <param name="ImageLength">Length of the image up to and including its own end marker.</param>
/// <param name="Format">Sniffed image format.</param>
public sealed record NcmEmbeddedImage(long Offset, int DeclaredLength, int ImageLength, ImageFormat Format);

/// <summary>Image container formats an .ncm may embed.</summary>
public enum ImageFormat
{
    Unknown = 0,
    Jpeg,
    Png,
    Gif,
    Bmp,
    WebP,
}
