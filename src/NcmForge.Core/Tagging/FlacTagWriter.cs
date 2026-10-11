using System.Buffers.Binary;
using System.Text;
using NcmForge.Core.Audio;

namespace NcmForge.Core.Tagging;

/// <summary>
/// Rewrites a FLAC file's metadata section to carry Vorbis comments and an embedded cover.
/// </summary>
/// <remarks>
/// FLAC stores tags as metadata blocks ahead of the audio frames, so tagging means rebuilding that
/// block chain and copying the frames through untouched. This is preferred over shelling out to a
/// tagger because it keeps the pipeline dependency-free and preserves the original frame data
/// byte-for-byte.
/// <para>
/// SEEKTABLE entries stay valid across the rewrite: FLAC defines their offsets relative to the
/// first frame, not to the start of the file.
/// </para>
/// </remarks>
public static class FlacTagWriter
{
    private const byte StreamInfoType = 0;
    private const byte PaddingType = 1;
    private const byte ApplicationType = 2;
    private const byte SeekTableType = 3;
    private const byte VorbisCommentType = 4;
    private const byte CueSheetType = 5;
    private const byte PictureType = 6;

    private const int MaxBlockLength = 0xFFFFFF;
    private const int PaddingBytes = 8192;
    private const string Vendor = "NcmForge 1.0";

    /// <summary>Validates the FLAC signature; returns false for anything else.</summary>
    public static bool IsFlac(Stream stream)
    {
        if (!stream.CanSeek || stream.Length < 4)
        {
            return false;
        }

        long original = stream.Position;
        stream.Position = 0;
        Span<byte> magic = stackalloc byte[4];
        bool ok = ReadFully(stream, magic) == 4 && magic.SequenceEqual("fLaC"u8);
        stream.Position = original;
        return ok;
    }

    /// <summary>Lists the metadata blocks without rewriting anything.</summary>
    public static IReadOnlyList<(byte Type, int Length, bool IsLast)> ListBlocks(Stream flac)
    {
        var blocks = new List<(byte, int, bool)>();
        long original = flac.Position;
        flac.Position = 0;

        Span<byte> magic = stackalloc byte[4];
        if (ReadFully(flac, magic) != 4 || !magic.SequenceEqual("fLaC"u8))
        {
            flac.Position = original;
            return blocks;
        }

        Span<byte> header = stackalloc byte[4];
        while (flac.Position + 4 <= flac.Length)
        {
            if (ReadFully(flac, header) != 4)
            {
                break;
            }

            bool isLast = (header[0] & 0x80) != 0;
            byte type = (byte)(header[0] & 0x7F);
            int length = (header[1] << 16) | (header[2] << 8) | header[3];
            blocks.Add((type, length, isLast));

            if (flac.Position + length > flac.Length)
            {
                break;
            }

            flac.Position += length;
            if (isLast)
            {
                break;
            }
        }

        flac.Position = original;
        return blocks;
    }

    /// <summary>Writes <paramref name="flac"/> to <paramref name="destination"/> with fresh tags.</summary>
    public static void WriteTagged(Stream flac, Stream destination, TrackTags tags)
    {
        if (!flac.CanSeek)
        {
            throw new ArgumentException("The FLAC source must be seekable.", nameof(flac));
        }

        flac.Position = 0;
        Span<byte> magic = stackalloc byte[4];
        if (ReadFully(flac, magic) != 4 || !magic.SequenceEqual("fLaC"u8))
        {
            throw new InvalidDataException("Not a FLAC stream: the fLaC marker is missing.");
        }

        byte[] streamInfo = [];
        var preserved = new List<(byte Type, byte[] Data)>();
        long firstFrameOffset = -1;

        Span<byte> header = stackalloc byte[4];
        while (flac.Position + 4 <= flac.Length)
        {
            if (ReadFully(flac, header) != 4)
            {
                break;
            }

            bool isLast = (header[0] & 0x80) != 0;
            byte type = (byte)(header[0] & 0x7F);
            int length = (header[1] << 16) | (header[2] << 8) | header[3];

            if (length < 0 || flac.Position + length > flac.Length)
            {
                throw new InvalidDataException($"FLAC metadata block {type} declares length {length}, which runs past the end of the stream.");
            }

            var data = new byte[length];
            ReadFully(flac, data);

            switch (type)
            {
                case StreamInfoType:
                    streamInfo = data;
                    break;

                // Existing comments and pictures are replaced; padding is dropped so the file does
                // not keep carrying dead space from a previous tagger.
                case VorbisCommentType:
                case PictureType:
                case PaddingType:
                    break;

                case ApplicationType:
                case SeekTableType:
                case CueSheetType:
                    preserved.Add((type, data));
                    break;
            }

            if (isLast)
            {
                firstFrameOffset = flac.Position;
                break;
            }
        }

        if (streamInfo.Length == 0)
        {
            throw new InvalidDataException("FLAC stream has no STREAMINFO block, so it cannot be rewritten.");
        }

        if (firstFrameOffset < 0)
        {
            throw new InvalidDataException("FLAC metadata chain has no terminating last-block flag.");
        }

        var blocks = new List<(byte Type, byte[] Data)>
        {
            (StreamInfoType, streamInfo),
        };

        blocks.AddRange(preserved);
        blocks.Add((VorbisCommentType, BuildVorbisComment(tags)));

        if (tags.Cover is { } cover)
        {
            blocks.Add((PictureType, BuildPicture(cover)));
        }

        blocks.Add((PaddingType, new byte[PaddingBytes]));

        destination.Write("fLaC"u8);

        Span<byte> outHeader = stackalloc byte[4];
        for (var i = 0; i < blocks.Count; i++)
        {
            (byte type, byte[] data) = blocks[i];
            if (data.Length > MaxBlockLength)
            {
                throw new InvalidDataException($"Metadata block {type} is {data.Length} bytes, exceeding the FLAC limit of {MaxBlockLength}.");
            }

            bool isLast = i == blocks.Count - 1;
            outHeader[0] = (byte)((isLast ? 0x80 : 0x00) | type);
            outHeader[1] = (byte)((data.Length >> 16) & 0xFF);
            outHeader[2] = (byte)((data.Length >> 8) & 0xFF);
            outHeader[3] = (byte)(data.Length & 0xFF);

            destination.Write(outHeader);
            destination.Write(data, 0, data.Length);
        }

        // Frames are copied verbatim, so the audio is untouched.
        flac.Position = firstFrameOffset;
        flac.CopyTo(destination);
    }

    /// <summary>Builds a Vorbis comment block. Its lengths are little-endian, unlike the rest of FLAC.</summary>
    private static byte[] BuildVorbisComment(TrackTags tags)
    {
        var pairs = new List<(string Key, string Value)>();

        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                pairs.Add((key, value!));
            }
        }

        Add("TITLE", tags.Title);
        Add("ARTIST", tags.Artist);
        Add("ALBUM", tags.Album);
        Add("ALBUMARTIST", tags.AlbumArtist ?? tags.Artist);
        Add("GENRE", tags.Genre);

        if (tags.Year is { } year)
        {
            Add("DATE", year.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (tags.TrackNumber is { } track)
        {
            Add("TRACKNUMBER", track.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (tags.TrackTotal is { } total)
        {
            Add("TRACKTOTAL", total.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (tags.DiscNumber is { } disc)
        {
            Add("DISCNUMBER", disc.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (tags.MusicId is { } musicId)
        {
            Add("NETEASE_MUSICID", musicId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        Add("COMMENT", tags.Comment);

        // LYRICS carries the timestamped text that players synchronise against;
        // UNSYNCEDLYRICS carries the same words without timestamps for plain display.
        Add("LYRICS", tags.Lyrics);
        Add("UNSYNCEDLYRICS", tags.PlainLyrics);

        Add("ENCODER", Vendor);

        using var buffer = new MemoryStream();
        Span<byte> int32 = stackalloc byte[4];

        byte[] vendor = Encoding.UTF8.GetBytes(Vendor);
        BinaryPrimitives.WriteInt32LittleEndian(int32, vendor.Length);
        buffer.Write(int32);
        buffer.Write(vendor, 0, vendor.Length);

        BinaryPrimitives.WriteInt32LittleEndian(int32, pairs.Count);
        buffer.Write(int32);

        foreach ((string key, string value) in pairs)
        {
            byte[] entry = Encoding.UTF8.GetBytes($"{key}={value}");
            BinaryPrimitives.WriteInt32LittleEndian(int32, entry.Length);
            buffer.Write(int32);
            buffer.Write(entry, 0, entry.Length);
        }

        return buffer.ToArray();
    }

    /// <summary>Builds a PICTURE block tied to the front cover.</summary>
    private static byte[] BuildPicture(CoverArt cover)
    {
        using var buffer = new MemoryStream();

        WriteInt32BigEndian(buffer, 3); // picture type: front cover

        byte[] mime = Encoding.ASCII.GetBytes(cover.MimeType);
        WriteInt32BigEndian(buffer, mime.Length);
        buffer.Write(mime, 0, mime.Length);

        byte[] description = Encoding.UTF8.GetBytes(cover.Description ?? string.Empty);
        WriteInt32BigEndian(buffer, description.Length);
        buffer.Write(description, 0, description.Length);

        ImageInfo? info = ImageProbe.Read(cover.Data, cover.Format);

        WriteInt32BigEndian(buffer, info?.Width ?? 0);
        WriteInt32BigEndian(buffer, info?.Height ?? 0);
        WriteInt32BigEndian(buffer, info?.DepthBits ?? 0);
        WriteInt32BigEndian(buffer, 0); // indexed-colour palette size; 0 for non-indexed images

        WriteInt32BigEndian(buffer, cover.Data.Length);
        buffer.Write(cover.Data, 0, cover.Data.Length);

        return buffer.ToArray();
    }

    private static void WriteInt32BigEndian(Stream destination, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        destination.Write(bytes);
    }

    private static void WriteInt32LittleEndian(Stream destination, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        destination.Write(bytes);
    }

    private static int ReadFully(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer[total..]);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
