namespace NcmForge.Core.Audio.Flac;

/// <summary>Decoded STREAMINFO fields relevant to conversion decisions.</summary>
/// <param name="SampleRate">Sample rate in Hz.</param>
/// <param name="Channels">Channel count, 1-8 by specification.</param>
/// <param name="BitsPerSample">Bits per sample.</param>
/// <param name="TotalSamples">Total inter-channel samples, or 0 when unknown.</param>
public sealed record FlacStreamInfo(int SampleRate, int Channels, int BitsPerSample, long TotalSamples)
{
    public TimeSpan? Duration => SampleRate > 0 && TotalSamples > 0
        ? TimeSpan.FromSeconds((double)TotalSamples / SampleRate)
        : null;
}

/// <summary>Reads the STREAMINFO block of a FLAC stream.</summary>
public static class FlacProbe
{
    /// <summary>Returns STREAMINFO, or <c>null</c> when the stream is not FLAC.</summary>
    public static FlacStreamInfo? Read(Stream stream)
    {
        if (!stream.CanSeek || stream.Length < 42)
        {
            return null;
        }

        long original = stream.Position;
        try
        {
            stream.Position = 0;

            Span<byte> header = stackalloc byte[8];
            if (ReadFully(stream, header) != 8 || !header[..4].SequenceEqual("fLaC"u8))
            {
                return null;
            }

            // STREAMINFO must be the first block: 1 byte header + 3 bytes length, then 34 bytes.
            byte blockType = (byte)(header[4] & 0x7F);
            int length = (header[5] << 16) | (header[6] << 8) | header[7];
            if (blockType != 0 || length < 34)
            {
                return null;
            }

            Span<byte> info = stackalloc byte[34];
            if (ReadFully(stream, info) != 34)
            {
                return null;
            }

            // Bytes 10..17 pack sample rate (20), channels-1 (3), bits-1 (5) and total samples (36).
            int sampleRate = (info[10] << 12) | (info[11] << 4) | (info[12] >> 4);
            int channels = ((info[12] >> 1) & 0x07) + 1;
            int bitsPerSample = (((info[12] & 0x01) << 4) | (info[13] >> 4)) + 1;

            long totalSamples = ((long)(info[13] & 0x0F) << 32)
                | ((long)info[14] << 24)
                | ((long)info[15] << 16)
                | ((long)info[16] << 8)
                | info[17];

            return new FlacStreamInfo(sampleRate, channels, bitsPerSample, totalSamples);
        }
        finally
        {
            stream.Position = original;
        }
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
