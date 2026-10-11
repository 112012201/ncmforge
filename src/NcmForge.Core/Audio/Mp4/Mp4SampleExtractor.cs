using System.Buffers.Binary;
using System.Text;

namespace NcmForge.Core.Audio.Mp4;

/// <summary>Summary of an extracted elementary stream.</summary>
/// <param name="CodecFourCc">Coding name taken from the sample description.</param>
/// <param name="SampleCount">Number of samples copied.</param>
/// <param name="ByteCount">Total payload bytes written.</param>
public sealed record Mp4ExtractionResult(string CodecFourCc, int SampleCount, long ByteCount);

/// <summary>
/// Pulls the coded audio samples out of an ISO base media file into a raw elementary stream.
/// </summary>
/// <remarks>
/// This is the bridge that makes an external decoder usable for codecs no local library
/// implements. The reference AVS3-P3 decoder, for example, consumes a bare <c>.av3a</c> stream
/// rather than an MP4, so the samples have to be walked out of the sample tables first.
/// </remarks>
public static class Mp4SampleExtractor
{
    /// <summary>Extracts the first audio track's samples, returning <c>null</c> when it is not ISO-BMFF.</summary>
    public static Mp4ExtractionResult? Extract(Stream stream, Stream destination)
    {
        if (!TryFindAudioStbl(stream, out long stblStart, out long stblEnd, out string fourCc))
        {
            return null;
        }

        uint[] sizes = ReadSampleSizes(stream, stblStart, stblEnd);
        if (sizes.Length == 0)
        {
            return null;
        }

        List<(uint SamplesPerChunk, uint FirstChunk)> stsc = ReadSampleToChunk(stream, stblStart, stblEnd);
        long[] chunkOffsets = ReadChunkOffsets(stream, stblStart, stblEnd);
        if (stsc.Count == 0 || chunkOffsets.Length == 0)
        {
            return null;
        }

        var buffer = new byte[1 << 20];
        long total = 0;
        int sampleIndex = 0;
        int tableIndex = 0;

        for (var chunkIndex = 0; chunkIndex < chunkOffsets.Length && sampleIndex < sizes.Length; chunkIndex++)
        {
            uint chunkNumber = (uint)chunkIndex + 1;

            // Advance to the run of chunks that contains this chunk number.
            while (tableIndex + 1 < stsc.Count && chunkNumber >= stsc[tableIndex + 1].FirstChunk)
            {
                tableIndex++;
            }

            uint samplesPerChunk = stsc[tableIndex].SamplesPerChunk;
            long offset = chunkOffsets[chunkIndex];

            for (uint i = 0; i < samplesPerChunk && sampleIndex < sizes.Length; i++)
            {
                int size = (int)sizes[sampleIndex];
                if (size <= 0)
                {
                    sampleIndex++;
                    continue;
                }

                if (size > buffer.Length)
                {
                    buffer = new byte[size];
                }

                int read = ReadAt(stream, offset, buffer.AsSpan(0, size));
                if (read != size)
                {
                    // A truncated table means the rest cannot be trusted.
                    return new Mp4ExtractionResult(fourCc, sampleIndex, total);
                }

                destination.Write(buffer, 0, size);
                total += size;
                offset += size;
                sampleIndex++;
            }
        }

        return new Mp4ExtractionResult(fourCc, sampleIndex, total);
    }

    private static bool TryFindAudioStbl(Stream stream, out long stblStart, out long stblEnd, out string fourCc)
    {
        stblStart = 0;
        stblEnd = 0;
        fourCc = string.Empty;

        Mp4AudioTrack? track = Mp4Probe.Probe(stream);
        if (track is null)
        {
            return false;
        }

        fourCc = track.CodecFourCc;

        if (!TryFindBox(stream, 0, stream.Length, "moov", out long moovStart, out long moovEnd))
        {
            return false;
        }

        long cursor = moovStart;
        Span<byte> handler = stackalloc byte[4];

        while (cursor < moovEnd)
        {
            if (!TryReadBox(stream, cursor, moovEnd, out string type, out long payloadStart, out long payloadEnd, out long next))
            {
                break;
            }

            if (type == "trak"
                && TryFindBox(stream, payloadStart, payloadEnd, "mdia", out long mdiaStart, out long mdiaEnd)
                && TryFindBox(stream, mdiaStart, mdiaEnd, "hdlr", out long hdlrStart, out _))
            {
                handler.Clear();
                ReadAt(stream, hdlrStart + 8, handler);
                if (handler.SequenceEqual("soun"u8)
                    && TryFindBox(stream, mdiaStart, mdiaEnd, "minf", out long minfStart, out long minfEnd)
                    && TryFindBox(stream, minfStart, minfEnd, "stbl", out stblStart, out stblEnd))
                {
                    return true;
                }
            }

            cursor = next;
        }

        return false;
    }

    private static uint[] ReadSampleSizes(Stream stream, long stblStart, long stblEnd)
    {
        if (!TryFindBox(stream, stblStart, stblEnd, "stsz", out long start, out long end))
        {
            return [];
        }

        Span<byte> header = stackalloc byte[12];
        if (ReadAt(stream, start + 4, header) != 12)
        {
            return [];
        }

        uint uniformSize = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
        int count = BinaryPrimitives.ReadInt32BigEndian(header[4..8]);
        if (count < 0 || count > 1 << 26)
        {
            return [];
        }

        if (uniformSize != 0)
        {
            var uniform = new uint[count];
            Array.Fill(uniform, uniformSize);
            return uniform;
        }

        // stsz payload: version/flags(4), uniform sample size(4), sample count(4), then the table.
        long tableStart = start + 12;
        if (tableStart + (long)count * 4 > end)
        {
            return [];
        }

        var sizes = new uint[count];
        var chunk = new byte[Math.Min(count, 4096) * 4];
        int index = 0;
        long position = tableStart;

        while (index < count)
        {
            int batch = Math.Min((count - index), chunk.Length / 4);
            int bytes = batch * 4;
            if (ReadAt(stream, position, chunk.AsSpan(0, bytes)) != bytes)
            {
                break;
            }

            for (var i = 0; i < batch; i++)
            {
                sizes[index + i] = BinaryPrimitives.ReadUInt32BigEndian(chunk.AsSpan(i * 4, 4));
            }

            index += batch;
            position += bytes;
        }

        return sizes;
    }

    private static List<(uint SamplesPerChunk, uint FirstChunk)> ReadSampleToChunk(Stream stream, long stblStart, long stblEnd)
    {
        var entries = new List<(uint, uint)>();
        if (!TryFindBox(stream, stblStart, stblEnd, "stsc", out long start, out _))
        {
            return entries;
        }

        Span<byte> countBytes = stackalloc byte[4];
        if (ReadAt(stream, start + 4, countBytes) != 4)
        {
            return entries;
        }

        int count = BinaryPrimitives.ReadInt32BigEndian(countBytes);
        if (count <= 0 || count > 1 << 20)
        {
            return entries;
        }

        var entry = new byte[12];
        for (var i = 0; i < count; i++)
        {
            if (ReadAt(stream, start + 8 + (long)i * 12, entry) != 12)
            {
                break;
            }

            uint firstChunk = BinaryPrimitives.ReadUInt32BigEndian(entry.AsSpan(0, 4));
            uint samplesPerChunk = BinaryPrimitives.ReadUInt32BigEndian(entry.AsSpan(4, 4));
            entries.Add((samplesPerChunk, firstChunk));
        }

        return entries;
    }

    private static long[] ReadChunkOffsets(Stream stream, long stblStart, long stblEnd)
    {
        // co64 holds 64-bit offsets; stco holds 32-bit. Large immersive files can use either.
        if (TryFindBox(stream, stblStart, stblEnd, "co64", out long start64, out _))
        {
            return ReadOffsets(stream, start64, 8);
        }

        if (TryFindBox(stream, stblStart, stblEnd, "stco", out long start32, out _))
        {
            return ReadOffsets(stream, start32, 4);
        }

        return [];
    }

    private static long[] ReadOffsets(Stream stream, long start, int width)
    {
        Span<byte> countBytes = stackalloc byte[4];
        if (ReadAt(stream, start + 4, countBytes) != 4)
        {
            return [];
        }

        int count = BinaryPrimitives.ReadInt32BigEndian(countBytes);
        if (count <= 0 || count > 1 << 26)
        {
            return [];
        }

        var offsets = new long[count];
        var entry = new byte[width];
        for (var i = 0; i < count; i++)
        {
            if (ReadAt(stream, start + 8 + (long)i * width, entry) != width)
            {
                break;
            }

            offsets[i] = width == 8
                ? (long)BinaryPrimitives.ReadUInt64BigEndian(entry)
                : BinaryPrimitives.ReadUInt32BigEndian(entry);
        }

        return offsets;
    }

    private static bool TryFindBox(Stream stream, long start, long end, string type, out long payloadStart, out long payloadEnd)
    {
        long cursor = start;
        var guard = 0;

        while (cursor < end && guard++ < 4096)
        {
            if (!TryReadBox(stream, cursor, end, out string current, out payloadStart, out payloadEnd, out long next))
            {
                break;
            }

            if (current == type)
            {
                return true;
            }

            if (current is "moov" or "trak" or "mdia" or "minf" or "stbl")
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

    private static bool TryReadBox(Stream stream, long position, long end, out string type,
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

        if (size < headerSize || position + size > end)
        {
            return false;
        }

        payloadStart = position + headerSize;
        payloadEnd = position + size;
        next = position + size;
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
