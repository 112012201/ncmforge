namespace NcmForge.Core.Audio;

/// <summary>Codec/container actually carried inside an .ncm payload.</summary>
public enum AudioFormat
{
    Unknown = 0,
    Mp3,
    Flac,
    /// <summary>Dolby Digital Plus (E-AC-3), the codec used by NetEase immersive/surround tracks.</summary>
    Eac3,
    /// <summary>Legacy Dolby Digital (AC-3).</summary>
    Ac3,
    Aac,
    Ogg,
    Wav,
    Mp4,
}

/// <summary>
/// Identifies an audio stream from its leading bytes.
/// </summary>
/// <remarks>
/// An .ncm container does not record a trustworthy codec identifier: the metadata's
/// <c>format</c> field describes the *store* product (and is frequently "mp3" even for lossless
/// purchases). The decrypted payload itself is the only reliable evidence, so detection is done
/// on the sniffed signature.
/// </remarks>
public static class AudioFormatDetector
{
    /// <summary>Bytes needed to make a confident determination.</summary>
    public const int SniffLength = 64;

    private const ushort Ac3Eac3SyncWord = 0x0B77;

    public static AudioFormat Detect(ReadOnlySpan<byte> head, out string? detail)
    {
        detail = null;

        if (head.Length < 4)
        {
            return AudioFormat.Unknown;
        }

        // FLAC: "fLaC"
        if (head[0] == 0x66 && head[1] == 0x4C && head[2] == 0x61 && head[3] == 0x43)
        {
            return AudioFormat.Flac;
        }

        // Ogg: "OggS"
        if (head[0] == 0x4F && head[1] == 0x67 && head[2] == 0x67 && head[3] == 0x53)
        {
            return AudioFormat.Ogg;
        }

        // RIFF/WAVE
        if (head[0] == 0x52 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x46)
        {
            return AudioFormat.Wav;
        }

        // ISO base media ("....ftyp"): M4A/AAC-in-MP4.
        if (head.Length >= 8 && head[4] == 0x66 && head[5] == 0x74 && head[6] == 0x79 && head[7] == 0x70)
        {
            return AudioFormat.Mp4;
        }

        // ID3v2 tag in front of MPEG audio.
        if (head[0] == 0x49 && head[1] == 0x44 && head[2] == 0x33)
        {
            return AudioFormat.Mp3;
        }

        // Dolby sync word 0x0B77 — shared by AC-3 and E-AC-3.
        if (head[0] == 0x0B && head[1] == 0x77)
        {
            int bsid = ReadBits(head[2..], 24, 5);
            if (bsid is >= 11 and <= 16)
            {
                detail = $"E-AC-3 (bsid={bsid})";
                return AudioFormat.Eac3;
            }

            if (bsid <= 8)
            {
                detail = $"AC-3 (bsid={bsid})";
                return AudioFormat.Ac3;
            }

            detail = $"Dolby, unrecognised bsid={bsid}";
            return AudioFormat.Eac3;
        }

        // MPEG audio frame sync: 11 set bits, not an ADTS header.
        if (head[0] == 0xFF && (head[1] & 0xE0) == 0xE0)
        {
            bool isAdts = (head[1] & 0xF6) == 0xF0;
            if (isAdts)
            {
                return AudioFormat.Aac;
            }

            int versionBits = (head[1] >> 3) & 0x03;
            int layerBits = (head[1] >> 1) & 0x03;
            if (versionBits != 0x01 && layerBits != 0x00)
            {
                return AudioFormat.Mp3;
            }
        }

        return AudioFormat.Unknown;
    }

    /// <summary>Reads <paramref name="count"/> bits starting at <paramref name="bitOffset"/> (MSB-first).</summary>
    private static int ReadBits(ReadOnlySpan<byte> data, int bitOffset, int count)
    {
        int value = 0;
        for (var i = 0; i < count; i++)
        {
            int bitIndex = bitOffset + i;
            int byteIndex = bitIndex >> 3;
            if (byteIndex >= data.Length)
            {
                break;
            }

            int bit = (data[byteIndex] >> (7 - (bitIndex & 7))) & 1;
            value = (value << 1) | bit;
        }

        return value;
    }
}
