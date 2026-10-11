using System.Buffers.Binary;
using System.Text;
using NcmForge.Core.Ncm;

namespace NcmForge.Core.Tagging;

/// <summary>
/// Writes an ID3v2.3 tag in front of an MPEG audio stream.
/// </summary>
/// <remarks>
/// ID3v2.3 with UTF-16 text is chosen over ID3v2.4/UTF-8 deliberately: it is the variant read
/// correctly by the widest range of players and by Windows' own tag readers, which matters because
/// NetEase titles and artists are frequently non-Latin.
/// </remarks>
public static class Id3v2Writer
{
    private const int HeaderLength = 10;

    /// <summary>Builds a complete ID3v2.3 tag for the supplied values.</summary>
    public static byte[] BuildTag(TrackTags tags)
    {
        var frames = new List<byte[]>();

        AddTextFrame(frames, "TIT2", tags.Title);
        AddTextFrame(frames, "TPE1", tags.Artist);
        AddTextFrame(frames, "TALB", tags.Album);
        AddTextFrame(frames, "TPE2", tags.AlbumArtist ?? tags.Artist);
        AddTextFrame(frames, "TCON", tags.Genre);

        if (tags.TrackNumber is { } track)
        {
            AddTextFrame(frames, "TRCK", tags.TrackTotal is { } total ? $"{track}/{total}" : track.ToString());
        }

        if (tags.DiscNumber is { } disc)
        {
            AddTextFrame(frames, "TPOS", disc.ToString());
        }

        if (tags.Year is { } year)
        {
            AddTextFrame(frames, "TYER", year.ToString());
        }

        if (tags.MusicId is { } musicId)
        {
            AddUserTextFrame(frames, "NCMFORGE_MUSICID", musicId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(tags.Comment))
        {
            AddCommentFrame(frames, tags.Comment!);
        }

        if (tags.Cover is { } cover)
        {
            AddPictureFrame(frames, cover);
        }

        if (!string.IsNullOrWhiteSpace(tags.Lyrics))
        {
            AddLyricsFrame(frames, tags.Lyrics!);
        }

        int bodyLength = frames.Sum(f => f.Length);

        var tag = new byte[HeaderLength + bodyLength];
        tag[0] = (byte)'I';
        tag[1] = (byte)'D';
        tag[2] = (byte)'3';
        tag[3] = 0x03; // version major: ID3v2.3
        tag[4] = 0x00; // version minor
        tag[5] = 0x00; // flags: no unsynchronisation, no extended header
        WriteSyncSafe(tag.AsSpan(6, 4), bodyLength);

        int offset = HeaderLength;
        foreach (byte[] frame in frames)
        {
            frame.CopyTo(tag, offset);
            offset += frame.Length;
        }

        return tag;
    }

    /// <summary>
    /// Writes <paramref name="audio"/> to <paramref name="destination"/> with a fresh tag, removing
    /// any tag the payload already carried so duplicate tags are never produced.
    /// </summary>
    public static void WriteTagged(Stream audio, Stream destination, TrackTags tags)
    {
        byte[] tag = BuildTag(tags);
        destination.Write(tag, 0, tag.Length);

        long start = audio.CanSeek ? SkipExistingTag(audio) : 0;
        if (audio.CanSeek)
        {
            audio.Position = start;
        }

        audio.CopyTo(destination);
    }

    /// <summary>Returns the offset of the first audio byte, skipping an existing ID3v2 tag.</summary>
    public static long SkipExistingTag(Stream audio)
    {
        if (!audio.CanSeek || audio.Length < HeaderLength)
        {
            return 0;
        }

        long original = audio.Position;
        audio.Position = 0;

        Span<byte> header = stackalloc byte[HeaderLength];
        int read = ReadFully(audio, header);

        bool looksLikeTag = read == HeaderLength
            && header[0] == (byte)'I'
            && header[1] == (byte)'D'
            && header[2] == (byte)'3'
            && header[3] != 0xFF
            && (header[6] | header[7] | header[8] | header[9]) < 0x80;

        if (!looksLikeTag)
        {
            audio.Position = original;
            return 0;
        }

        int size = ReadSyncSafe(header[6..10]) + HeaderLength;

        // A footer, when present, adds another 10 bytes.
        if ((header[5] & 0x10) != 0)
        {
            size += HeaderLength;
        }

        return Math.Min(size, audio.Length);
    }

    private static void AddTextFrame(List<byte[]> frames, string id, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        // Encoding byte 0x01 = UTF-16 with BOM.
        byte[] bom = Encoding.Unicode.GetPreamble();
        byte[] text = Encoding.Unicode.GetBytes(value);

        var payload = new byte[1 + bom.Length + text.Length];
        payload[0] = 0x01;
        bom.CopyTo(payload, 1);
        text.CopyTo(payload, 1 + bom.Length);

        frames.Add(BuildFrame(id, payload));
    }

    /// <summary>
    /// Writes a TXXX frame: <c>encoding, description, terminator, value</c>.
    /// </summary>
    /// <remarks>
    /// Each UTF-16 string carries its own BOM, and the description is terminated by a double NUL.
    /// Getting this wrong makes readers treat the description as the value and then trip over the
    /// leftover bytes.
    /// </remarks>
    private static void AddUserTextFrame(List<byte[]> frames, string description, string value)
    {
        byte[] bom = Encoding.Unicode.GetPreamble();
        byte[] desc = Encoding.Unicode.GetBytes(description);
        byte[] text = Encoding.Unicode.GetBytes(value);

        var payload = new byte[1 + bom.Length + desc.Length + 2 + bom.Length + text.Length];
        int p = 0;
        payload[p++] = 0x01;
        bom.CopyTo(payload, p);
        p += bom.Length;
        desc.CopyTo(payload, p);
        p += desc.Length;
        payload[p++] = 0x00;
        payload[p++] = 0x00;
        bom.CopyTo(payload, p);
        p += bom.Length;
        text.CopyTo(payload, p);

        frames.Add(BuildFrame("TXXX", payload));
    }

    private static void AddCommentFrame(List<byte[]> frames, string comment)
    {
        // COMM: encoding(1) language(3) short description(UTF-16 with BOM, terminated) text.
        // The empty description still needs its BOM: readers reject a UTF-16 string that does not
        // begin with one, and report "Incorrect BOM value" while skipping the whole frame.
        byte[] bom = Encoding.Unicode.GetPreamble();
        byte[] text = Encoding.Unicode.GetBytes(comment);

        var payload = new byte[1 + 3 + bom.Length + 2 + bom.Length + text.Length];
        int p = 0;
        payload[p++] = 0x01;
        payload[p++] = (byte)'e';
        payload[p++] = (byte)'n';
        payload[p++] = (byte)'g';
        bom.CopyTo(payload, p);
        p += bom.Length;
        payload[p++] = 0x00;
        payload[p++] = 0x00;
        bom.CopyTo(payload, p);
        p += bom.Length;
        text.CopyTo(payload, p);

        frames.Add(BuildFrame("COMM", payload));
    }

    /// <summary>
    /// Writes a USLT frame: <c>encoding, language, content descriptor, lyrics</c>.
    /// </summary>
    /// <remarks>
    /// The LRC text is stored with its timestamps intact, which is the convention Chinese players
    /// follow: they parse the timestamps for synchronised display, and players that cannot simply
    /// show the raw lines.
    /// </remarks>
    private static void AddLyricsFrame(List<byte[]> frames, string lyrics)
    {
        byte[] bom = Encoding.Unicode.GetPreamble();
        byte[] text = Encoding.Unicode.GetBytes(lyrics);

        // The empty descriptor still needs a BOM before its terminator, or readers reject the frame.
        var payload = new byte[1 + 3 + bom.Length + 2 + bom.Length + text.Length];
        int p = 0;
        payload[p++] = 0x01;

        // Readers expose the language as part of the tag name (lyrics-eng, lyrics-chi), so marking
        // it correctly is what makes the lyrics show up under a sensible key.
        byte[] language = Encoding.ASCII.GetBytes(DetectLanguage(lyrics));
        payload[p++] = language[0];
        payload[p++] = language[1];
        payload[p++] = language[2];

        bom.CopyTo(payload, p);
        p += bom.Length;
        payload[p++] = 0x00;
        payload[p++] = 0x00;
        bom.CopyTo(payload, p);
        p += bom.Length;
        text.CopyTo(payload, p);

        frames.Add(BuildFrame("USLT", payload));
    }

    private static void AddPictureFrame(List<byte[]> frames, CoverArt cover)
    {
        byte[] mime = Encoding.ASCII.GetBytes(cover.MimeType);

        // APIC: encoding(1) mime(term) picture type(1) description(term) data.
        // The description is left empty and the encoding is Latin-1, so no text encoding subtleties
        // can corrupt the image payload.
        int payloadLength = 1 + mime.Length + 1 + 1 + 1 + cover.Data.Length;
        var payload = new byte[payloadLength];

        int p = 0;
        payload[p++] = 0x00;
        mime.CopyTo(payload, p);
        p += mime.Length;
        payload[p++] = 0x00;
        payload[p++] = 0x03; // front cover
        payload[p++] = 0x00; // empty description terminator
        cover.Data.CopyTo(payload, p);

        frames.Add(BuildFrame("APIC", payload));
    }

    /// <summary>Picks an ISO-639-2 language code by looking for CJK ideographs.</summary>
    private static string DetectLanguage(string text)
    {
        foreach (char c in text)
        {
            if (c is >= '\u3400' and <= '\u4DBF' or >= '\u4E00' and <= '\u9FFF' or >= '\uF900' and <= '\uFAFF')
            {
                return "chi";
            }
        }

        return "eng";
    }

    private static byte[] BuildFrame(string id, byte[] payload)
    {
        var frame = new byte[10 + payload.Length];
        Encoding.ASCII.GetBytes(id).CopyTo(frame, 0);
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(4, 4), payload.Length);
        // Flags stay zero: no compression, encryption, grouping or unsynchronisation.
        payload.CopyTo(frame, 10);
        return frame;
    }

    private static void WriteSyncSafe(Span<byte> destination, int value)
    {
        destination[0] = (byte)((value >> 21) & 0x7F);
        destination[1] = (byte)((value >> 14) & 0x7F);
        destination[2] = (byte)((value >> 7) & 0x7F);
        destination[3] = (byte)(value & 0x7F);
    }

    private static int ReadSyncSafe(ReadOnlySpan<byte> source)
        => (source[0] << 21) | (source[1] << 14) | (source[2] << 7) | source[3];

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
