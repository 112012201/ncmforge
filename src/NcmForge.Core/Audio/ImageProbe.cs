using System.Buffers.Binary;
using NcmForge.Core.Ncm;

namespace NcmForge.Core.Audio;

/// <summary>Pixel dimensions and bit depth of a cover image, read from its own header.</summary>
public readonly record struct ImageInfo(int Width, int Height, int BitDepth, int ColorComponents)
{
    /// <summary>Bits per pixel as FLAC's PICTURE block expects it (depth x components).</summary>
    public int DepthBits => BitDepth * Math.Max(1, ColorComponents);
}

/// <summary>
/// Reads image dimensions without a decoding library.
/// </summary>
/// <remarks>
/// FLAC's PICTURE block requires width, height and colour depth. Pulling in an imaging dependency
/// just to read four integers would be wasteful and would not help cross-platform packaging, so the
/// two header formats that matter here are parsed directly.
/// </remarks>
public static class ImageProbe
{
    public static ImageInfo? Read(ReadOnlySpan<byte> data, ImageFormat format)
    {
        return format switch
        {
            ImageFormat.Jpeg => ReadJpeg(data),
            ImageFormat.Png => ReadPng(data),
            _ => null,
        };
    }

    private static ImageInfo? ReadPng(ReadOnlySpan<byte> data)
    {
        // 8-byte signature, then IHDR: length(4) "IHDR" width(4) height(4) depth(1) colorType(1)
        const int IhdrOffset = 8;
        if (data.Length < IhdrOffset + 25)
        {
            return null;
        }

        if (!data.Slice(IhdrOffset + 4, 4).SequenceEqual("IHDR"u8))
        {
            return null;
        }

        int width = BinaryPrimitives.ReadInt32BigEndian(data.Slice(IhdrOffset + 8, 4));
        int height = BinaryPrimitives.ReadInt32BigEndian(data.Slice(IhdrOffset + 12, 4));
        int bitDepth = data[IhdrOffset + 16];
        byte colorType = data[IhdrOffset + 17];

        int components = colorType switch
        {
            0 => 1, // greyscale
            2 => 3, // truecolour
            3 => 1, // palette
            4 => 2, // greyscale + alpha
            6 => 4, // truecolour + alpha
            _ => 3,
        };

        return new ImageInfo(width, height, bitDepth, components);
    }

    private static ImageInfo? ReadJpeg(ReadOnlySpan<byte> data)
    {
        int position = 2; // skip SOI

        while (position + 4 <= data.Length)
        {
            if (data[position] != 0xFF)
            {
                position++;
                continue;
            }

            byte marker = data[position + 1];
            position += 2;

            // Standalone markers carry no length.
            if (marker is 0xD8 or 0xD9 or 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                continue;
            }

            if (position + 2 > data.Length)
            {
                return null;
            }

            int segmentLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(position, 2));
            if (segmentLength < 2 || position + segmentLength > data.Length)
            {
                return null;
            }

            // SOF0..SOF15, excluding the non-frame markers DHT (C4), JPG (C8) and DAC (CC).
            bool isStartOfFrame = marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC;
            if (isStartOfFrame)
            {
                if (segmentLength < 8)
                {
                    return null;
                }

                int precision = data[position + 2];
                int height = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(position + 3, 2));
                int width = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(position + 5, 2));
                int components = data[position + 7];
                return new ImageInfo(width, height, precision, components);
            }

            position += segmentLength;
        }

        return null;
    }
}
