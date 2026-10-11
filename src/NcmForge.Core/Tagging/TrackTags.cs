using NcmForge.Core.Ncm;

namespace NcmForge.Core.Tagging;

/// <summary>Cover art to be embedded into an output file.</summary>
public sealed class CoverArt
{
    public CoverArt(byte[] data, ImageFormat format, string? source = null, string? description = null)
    {
        Data = data;
        Format = format;
        MimeType = Audio.ImageSignature.MimeType(format);
        Source = source;
        Description = description;
    }

    public byte[] Data { get; }

    public ImageFormat Format { get; }

    public string MimeType { get; }

    /// <summary>Where the image came from, for reporting ("embedded", "netease", "itunes", ...).</summary>
    public string? Source { get; }

    public string? Description { get; }

    public int Length => Data.Length;
}

/// <summary>The tag values written into an output file.</summary>
public sealed class TrackTags
{
    public string? Title { get; set; }

    public string? Artist { get; set; }

    public string? Album { get; set; }

    public string? AlbumArtist { get; set; }

    public string? Genre { get; set; }

    public string? Comment { get; set; }

    public int? TrackNumber { get; set; }

    public int? TrackTotal { get; set; }

    public int? DiscNumber { get; set; }

    public int? Year { get; set; }

    public CoverArt? Cover { get; set; }

    /// <summary>Full lyrics in LRC form, timestamps included.</summary>
    public string? Lyrics { get; set; }

    /// <summary>Lyrics text with timestamps stripped, for players that only display plain text.</summary>
    public string? PlainLyrics { get; set; }

    /// <summary>NetEase song id, written as a comment so the source stays traceable.</summary>
    public long? MusicId { get; set; }

    public static TrackTags FromMetadata(NcmMetadata metadata)
    {
        return new TrackTags
        {
            Title = metadata.MusicName,
            Artist = metadata.ArtistDisplay,
            Album = metadata.Album,
            MusicId = metadata.MusicId,
        };
    }
}
