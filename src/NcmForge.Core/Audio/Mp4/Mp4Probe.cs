using System.Buffers.Binary;
using System.Text;

namespace NcmForge.Core.Audio.Mp4;

/// <summary>Audio track description read from an ISO base media (MP4/M4A) container.</summary>
/// <param name="CodecFourCc">The sample-entry coding name, e.g. <c>av3a</c>, <c>ec-3</c>, <c>mp4a</c>.</param>
/// <param name="CodecName">Human-readable codec name.</param>
public sealed record Mp4AudioTrack(
    string CodecFourCc,
    string CodecName,
    int Channels,
    int SampleSize,
    int SampleRate,
    TimeSpan? Duration);

/// <summary>
/// Minimal ISO-BMFF reader that recovers the audio sample entry's coding name.
/// </summary>
/// <remarks>
/// This exists because the codec tag is the only trustworthy statement about what an immersive
/// .ncm payload actually holds: the container's own metadata calls every store product "m4a", and
/// ffmpeg reports <c>codec_name=unknown</c> for tags it does not implement (AVS3-P3, for example).
/// </remarks>
public static class Mp4Probe
{
    private const int MaxDepth = 8;

    private static readonly Dictionary<string, string> KnownCodecs = new(StringComparer.Ordinal)
    {
        ["av3a"] = "AVS3-P3 (Audio Vivid / 菁彩声)",
        ["ec-3"] = "Dolby Digital Plus (E-AC-3)",
        ["ac-3"] = "Dolby Digital (AC-3)",
        ["dac3"] = "Dolby Digital (AC-3)",
        ["dec3"] = "Dolby Digital Plus (E-AC-3)",
        ["mp4a"] = "MPEG-4 Audio (AAC)",
        ["alac"] = "Apple Lossless (ALAC)",
        ["fLaC"] = "FLAC",
        ["flac"] = "FLAC",
        ["Opus"] = "Opus",
        ["opus"] = "Opus",
        ["twos"] = "PCM (big-endian)",
        ["sowt"] = "PCM (little-endian)",
        ["lpcm"] = "PCM",
        ["samr"] = "AMR",
        ["dtsc"] = "DTS",
        ["dtsh"] = "DTS-HD",
        ["dtsl"] = "DTS-HD Master Audio",
        ["mlpa"] = "Dolby TrueHD",
    };

    /// <summary>Reads the first audio track, or <c>null</c> when the stream is not ISO-BMFF.</summary>
    public static Mp4AudioTrack? Probe(Stream stream)
    {
        if (!TryFindBox(stream, 0, stream.Length, "moov", out long moovStart, out long moovEnd))
        {
            return null;
        }

        TimeSpan? duration = ReadMovieDuration(stream, moovStart, moovEnd);

        long cursor = moovStart;
        while (TryNextBox(stream, ref cursor, moovEnd, out string type, out long payloadStart, out long payloadEnd))
        {
            if (type != "trak")
            {
                continue;
            }

            Mp4AudioTrack? track = ProbeTrack(stream, payloadStart, payloadEnd, duration);
            if (track is not null)
            {
                return track;
            }
        }

        return null;
    }

    private static Mp4AudioTrack? ProbeTrack(Stream stream, long trakStart, long trakEnd, TimeSpan? movieDuration)
    {
        if (!TryFindBox(stream, trakStart, trakEnd, "mdia", out long mdiaStart, out long mdiaEnd))
        {
            return null;
        }

        // Only audio tracks are interesting.
        if (TryFindBox(stream, mdiaStart, mdiaEnd, "hdlr", out long hdlrStart, out _))
        {
            Span<byte> handler = stackalloc byte[4];
            if (ReadAt(stream, hdlrStart + 8, handler) == 4
                && !handler.SequenceEqual("soun"u8))
            {
                return null;
            }
        }

        if (!TryFindBox(stream, mdiaStart, mdiaEnd, "minf", out long minfStart, out long minfEnd)
            || !TryFindBox(stream, minfStart, minfEnd, "stbl", out long stblStart, out long stblEnd)
            || !TryFindBox(stream, stblStart, stblEnd, "stsd", out long stsdStart, out _))
        {
            return null;
        }

        // stsd payload: version(1)+flags(3) = 4 bytes, entry_count(4), then the sample entry boxes.
        Span<byte> entryCountBytes = stackalloc byte[4];
        if (ReadAt(stream, stsdStart + 4, entryCountBytes) != 4
            || BinaryPrimitives.ReadInt32BigEndian(entryCountBytes) < 1)
        {
            return null;
        }

        long entryStart = stsdStart + 8;
        Span<byte> entryHeader = stackalloc byte[8];
        if (ReadAt(stream, entryStart, entryHeader) != 8)
        {
            return null;
        }

        string fourCc = Encoding.ASCII.GetString(entryHeader[4..8]);

        // AudioSampleEntry: channelcount at +24, samplesize at +26, samplerate (16.16) at +32.
        Span<byte> audio = stackalloc byte[12];
        if (ReadAt(stream, entryStart + 24, audio) != audio.Length)
        {
            return null;
        }

        int channels = BinaryPrimitives.ReadUInt16BigEndian(audio[..2]);
        int sampleSize = BinaryPrimitives.ReadUInt16BigEndian(audio[2..4]);
        int sampleRate = (int)(BinaryPrimitives.ReadUInt32BigEndian(audio[8..12]) >> 16);

        TimeSpan? duration = ReadTrackDuration(stream, mdiaStart, mdiaEnd) ?? movieDuration;

        return new Mp4AudioTrack(
            fourCc,
            KnownCodecs.TryGetValue(fourCc, out string? name) ? name : $"unknown ({fourCc})",
            channels,
            sampleSize,
            sampleRate,
            duration);
    }

    private static TimeSpan? ReadMovieDuration(Stream stream, long moovStart, long moovEnd)
    {
        if (!TryFindBox(stream, moovStart, moovEnd, "mvhd", out long start, out _))
        {
            return null;
        }

        Span<byte> header = stackalloc byte[4];
        if (ReadAt(stream, start, header) != 4)
        {
            return null;
        }

        long payload = start + (header[0] == 1 ? 20 : 12);
        Span<byte> values = stackalloc byte[8];
        if (ReadAt(stream, payload, values) != 8)
        {
            return null;
        }

        uint timescale = BinaryPrimitives.ReadUInt32BigEndian(values[..4]);
        uint duration = BinaryPrimitives.ReadUInt32BigEndian(values[4..]);
        return timescale == 0 ? null : TimeSpan.FromSeconds((double)duration / timescale);
    }

    private static TimeSpan? ReadTrackDuration(Stream stream, long mdiaStart, long mdiaEnd)
    {
        if (!TryFindBox(stream, mdiaStart, mdiaEnd, "mdhd", out long start, out _))
        {
            return null;
        }

        Span<byte> header = stackalloc byte[4];
        if (ReadAt(stream, start, header) != 4)
        {
            return null;
        }

        // version/flags(4) then, for version 0: creation(4) modification(4) timescale(4) duration(4).
        // Version 1 widens both timestamps to 8 bytes: timescale lands at +20 instead of +12.
        long payload = start + (header[0] == 1 ? 20 : 12);
        Span<byte> values = stackalloc byte[8];
        if (ReadAt(stream, payload, values) != 8)
        {
            return null;
        }

        uint timescale = BinaryPrimitives.ReadUInt32BigEndian(values[..4]);
        uint duration = BinaryPrimitives.ReadUInt32BigEndian(values[4..]);
        return timescale == 0 ? null : TimeSpan.FromSeconds((double)duration / timescale);
    }

    private static bool TryFindBox(Stream stream, long start, long end, string type, out long payloadStart, out long payloadEnd)
    {
        long cursor = start;
        var depthGuard = 0;

        while (cursor < end && depthGuard++ < 4096)
        {
            if (!TryReadBoxHeader(stream, cursor, end, out string current, out payloadStart, out payloadEnd, out long next))
            {
                break;
            }

            if (current == type)
            {
                return true;
            }

            // Descend into container boxes so nested searches work from an outer range.
            if (current is "moov" or "trak" or "mdia" or "minf" or "stbl" or "udta")
            {
                if (TryFindBox(stream, payloadStart, payloadEnd, type, out long nestedStart, out long nestedEnd))
                {
                    payloadStart = nestedStart;
                    payloadEnd = nestedEnd;
                    return true;
                }
            }

            cursor = next;
        }

        payloadStart = 0;
        payloadEnd = 0;
        return false;
    }

    private static bool TryNextBox(Stream stream, ref long cursor, long end, out string type, out long payloadStart, out long payloadEnd)
    {
        if (!TryReadBoxHeader(stream, cursor, end, out type, out payloadStart, out payloadEnd, out long next))
        {
            return false;
        }

        cursor = next;
        return true;
    }

    private static bool TryReadBoxHeader(Stream stream, long position, long end, out string type,
        out long payloadStart, out long payloadEnd, out long next)
    {
        type = string.Empty;
        payloadStart = 0;
        payloadEnd = 0;
        next = position;

        if (position < 0 || position + 8 > end)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[16];
        if (ReadAt(stream, position, header[..8]) != 8)
        {
            return false;
        }

        long size = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
        type = Encoding.ASCII.GetString(header[4..8]);

        long headerSize = 8;
        if (size == 1)
        {
            if (ReadAt(stream, position + 8, header[8..16]) != 8)
            {
                return false;
            }

            size = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..16]);
            headerSize = 16;
        }
        else if (size == 0)
        {
            size = end - position;
        }

        if (!IsPrintableType(type) || size < headerSize || position + size > end)
        {
            return false;
        }

        payloadStart = position + headerSize;
        payloadEnd = position + size;
        next = position + size;
        return true;
    }

    private static bool IsPrintableType(string type)
    {
        if (type.Length != 4)
        {
            return false;
        }

        foreach (char c in type)
        {
            if (c is < ' ' or > '~')
            {
                return false;
            }
        }

        return true;
    }

    private static int ReadAt(Stream stream, long position, Span<byte> destination)
    {
        if (position < 0 || position >= stream.Length)
        {
            return 0;
        }

        stream.Position = position;
        int total = 0;
        while (total < destination.Length)
        {
            int read = stream.Read(destination[total..]);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
