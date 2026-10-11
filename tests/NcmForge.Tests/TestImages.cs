using System.Buffers.Binary;
using System.Text;

namespace NcmForge.Tests;

/// <summary>Builds the smallest valid image headers the probes must understand.</summary>
internal static class TestImages
{
    /// <summary>A structurally valid baseline JPEG header carrying no scan data.</summary>
    public static byte[] Jpeg(int width, int height, int components = 3, int precision = 8)
    {
        using var stream = new MemoryStream();

        stream.Write([0xFF, 0xD8]); // SOI

        // APP0 / JFIF, segment length 16.
        stream.Write([0xFF, 0xE0, 0x00, 0x10]);
        stream.Write("JFIF\0"u8);
        stream.Write([0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00]);

        // SOF0 with the frame dimensions the probe reads.
        Span<byte> sof = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(sof, (ushort)(8 + 3 * components));
        stream.Write([0xFF, 0xC0]);
        stream.Write(sof);
        stream.WriteByte((byte)precision);
        BinaryPrimitives.WriteUInt16BigEndian(sof, (ushort)height);
        stream.Write(sof);
        BinaryPrimitives.WriteUInt16BigEndian(sof, (ushort)width);
        stream.Write(sof);
        stream.WriteByte((byte)components);

        for (var i = 0; i < components; i++)
        {
            stream.WriteByte((byte)(i + 1));
            stream.WriteByte(0x11);
            stream.WriteByte(0);
        }

        stream.Write([0xFF, 0xD9]); // EOI
        return stream.ToArray();
    }

    /// <summary>A PNG signature plus IHDR chunk, which is all the probe needs.</summary>
    public static byte[] Png(int width, int height, int bitDepth = 8, byte colorType = 2)
    {
        using var stream = new MemoryStream();

        stream.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        stream.Write([0x00, 0x00, 0x00, 0x0D]); // IHDR length
        stream.Write("IHDR"u8);

        Span<byte> value = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(value, width);
        stream.Write(value);
        BinaryPrimitives.WriteInt32BigEndian(value, height);
        stream.Write(value);
        stream.WriteByte((byte)bitDepth);
        stream.WriteByte(colorType);
        stream.Write([0x00, 0x00, 0x00]); // compression, filter, interlace
        stream.Write([0x00, 0x00, 0x00, 0x00]); // CRC placeholder

        return stream.ToArray();
    }

    /// <summary>A FLAC stream: signature, STREAMINFO, then opaque "frame" bytes.</summary>
    public static byte[] Flac(int sampleRate, int channels, int bitsPerSample, long totalSamples, byte[] frames)
    {
        using var stream = new MemoryStream();
        stream.Write("fLaC"u8);

        var info = new byte[34];
        BinaryPrimitives.WriteUInt16BigEndian(info.AsSpan(0, 2), 4096);
        BinaryPrimitives.WriteUInt16BigEndian(info.AsSpan(2, 2), 4096);

        // Packed 64-bit tail: sample rate (20), channels-1 (3), bits-1 (5), total samples (36).
        info[10] = (byte)(sampleRate >> 12);
        info[11] = (byte)(sampleRate >> 4);
        info[12] = (byte)(((sampleRate & 0x0F) << 4) | (((channels - 1) & 0x07) << 1) | (((bitsPerSample - 1) >> 4) & 0x01));
        info[13] = (byte)((((bitsPerSample - 1) & 0x0F) << 4) | (int)((totalSamples >> 32) & 0x0F));
        info[14] = (byte)(totalSamples >> 24);
        info[15] = (byte)(totalSamples >> 16);
        info[16] = (byte)(totalSamples >> 8);
        info[17] = (byte)totalSamples;

        // Metadata block header: last-block flag set, type 0 (STREAMINFO), length 34.
        stream.Write([0x80, 0x00, 0x00, 0x22]);
        stream.Write(info);
        stream.Write(frames);

        return stream.ToArray();
    }

    /// <summary>
    /// A minimal but structurally valid ISO base media file: <c>ftyp</c>, <c>moov</c> with a single
    /// audio track, and the samples in one <c>mdat</c> chunk.
    /// </summary>
    public static byte[] Mp4(string fourCc, int channels, int sampleRate, int sampleSize, byte[][] samples)
    {
        byte[] ftyp = Box("ftyp", Concat(
            Encoding.ASCII.GetBytes("isom"),
            [0x00, 0x00, 0x02, 0x00],
            Encoding.ASCII.GetBytes("isom"),
            Encoding.ASCII.GetBytes("iso2"),
            Encoding.ASCII.GetBytes("mp41")));

        int totalSamples = samples.Sum(s => s.Length);

        // The chunk offset depends on moov's size, and moov's size does not depend on the offset
        // value, so one placeholder pass is enough to resolve it.
        byte[] moov = BuildMoov(fourCc, channels, sampleRate, sampleSize, samples, 0);
        int dataOffset = ftyp.Length + moov.Length + 8;
        moov = BuildMoov(fourCc, channels, sampleRate, sampleSize, samples, dataOffset);

        byte[] mdatPayload = new byte[totalSamples];
        var cursor = 0;
        foreach (byte[] sample in samples)
        {
            sample.CopyTo(mdatPayload, cursor);
            cursor += sample.Length;
        }

        return [.. ftyp, .. moov, .. Box("mdat", mdatPayload)];
    }

    private static byte[] BuildMoov(string fourCc, int channels, int sampleRate, int sampleSize, byte[][] samples, long dataOffset)
    {
        var mvhdBody = new byte[96];
        BinaryPrimitives.WriteInt32BigEndian(mvhdBody.AsSpan(8, 4), 1000); // timescale
        BinaryPrimitives.WriteInt32BigEndian(mvhdBody.AsSpan(12, 4), 2000); // duration
        byte[] mvhd = FullBox("mvhd", mvhdBody);

        var mdhdBody = new byte[20];
        BinaryPrimitives.WriteInt32BigEndian(mdhdBody.AsSpan(8, 4), sampleRate);
        BinaryPrimitives.WriteInt32BigEndian(mdhdBody.AsSpan(12, 4), samples.Sum(s => s.Length));
        byte[] mdhd = FullBox("mdhd", mdhdBody);

        byte[] hdlr = FullBox("hdlr", Concat(
            new byte[4], // pre_defined
            Encoding.ASCII.GetBytes("soun"),
            new byte[12], // reserved
            "SoundHandler\0"u8.ToArray()));

        var entryBody = new byte[28];
        BinaryPrimitives.WriteUInt16BigEndian(entryBody.AsSpan(6, 2), 1); // data_reference_index
        BinaryPrimitives.WriteUInt16BigEndian(entryBody.AsSpan(16, 2), (ushort)channels);
        BinaryPrimitives.WriteUInt16BigEndian(entryBody.AsSpan(18, 2), (ushort)sampleSize);
        BinaryPrimitives.WriteUInt32BigEndian(entryBody.AsSpan(24, 4), (uint)sampleRate << 16); // 16.16 fixed

        byte[] stsd = FullBox("stsd", Concat(
            [0x00, 0x00, 0x00, 0x01], // entry_count
            Box(fourCc, entryBody)));

        byte[] stsz = FullBox("stsz", Concat(
            [0x00, 0x00, 0x00, 0x00], // uniform sample size: 0 means the table is explicit
            Int32(samples.Length),
            Concat(samples.Select(s => Int32(s.Length)).ToArray())));

        byte[] stsc = FullBox("stsc", Concat(
            [0x00, 0x00, 0x00, 0x01], // entry_count
            Int32(1),                 // first_chunk
            Int32(samples.Length),    // samples_per_chunk
            Int32(1)));               // sample_description_index

        byte[] stco = FullBox("stco", Concat(
            [0x00, 0x00, 0x00, 0x01], // entry_count
            Int32((int)dataOffset)));

        byte[] stbl = Box("stbl", Concat(stsd, stsz, stsc, stco));
        byte[] minf = Box("minf", stbl);
        byte[] mdia = Box("mdia", Concat(mdhd, hdlr, minf));
        byte[] trak = Box("trak", mdia);

        return Box("moov", Concat(mvhd, trak));
    }

    private static byte[] Int32(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] Box(string type, byte[] payload)
    {
        var result = new byte[8 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(0, 4), result.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(result, 4);
        payload.CopyTo(result, 8);
        return result;
    }

    private static byte[] FullBox(string type, byte[] payload)
        => Box(type, Concat(new byte[4], payload)); // version + flags

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var cursor = 0;
        foreach (byte[] part in parts)
        {
            part.CopyTo(result, cursor);
            cursor += part.Length;
        }

        return result;
    }
}
