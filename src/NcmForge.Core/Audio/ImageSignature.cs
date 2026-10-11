using NcmForge.Core.Ncm;

namespace NcmForge.Core.Audio;

/// <summary>Identifies embedded cover images from their leading bytes and finds their exact extent.</summary>
public static class ImageSignature
{
    public static ImageFormat Detect(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
        {
            return ImageFormat.Jpeg;
        }

        if (head.Length >= 8 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47
            && head[4] == 0x0D && head[5] == 0x0A && head[6] == 0x1A && head[7] == 0x0A)
        {
            return ImageFormat.Png;
        }

        if (head.Length >= 6 && head[0] == 0x47 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x38)
        {
            return ImageFormat.Gif;
        }

        if (head.Length >= 2 && head[0] == 0x42 && head[1] == 0x4D)
        {
            return ImageFormat.Bmp;
        }

        if (head.Length >= 12 && head[0] == 0x52 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x46
            && head[8] == 0x57 && head[9] == 0x45 && head[10] == 0x42 && head[11] == 0x50)
        {
            return ImageFormat.WebP;
        }

        return ImageFormat.Unknown;
    }

    public static string MimeType(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => "image/jpeg",
        ImageFormat.Png => "image/png",
        ImageFormat.Gif => "image/gif",
        ImageFormat.Bmp => "image/bmp",
        ImageFormat.WebP => "image/webp",
        _ => "application/octet-stream",
    };

    public static string Extension(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => ".jpg",
        ImageFormat.Png => ".png",
        ImageFormat.Gif => ".gif",
        ImageFormat.Bmp => ".bmp",
        ImageFormat.WebP => ".webp",
        _ => ".bin",
    };

    /// <summary>
    /// Finds where an image actually ends, so trailing container padding is never tagged onto it.
    /// Falls back to the whole buffer when no end marker is found.
    /// </summary>
    public static int FindImageLength(ReadOnlySpan<byte> data, ImageFormat format)
    {
        switch (format)
        {
            case ImageFormat.Jpeg:
            {
                // JPEG ends with EOI (FF D9). The last marker in the block wins, since entropy-coded
                // data cannot contain FF D9 without stuffing.
                for (var i = data.Length - 2; i >= 2; i--)
                {
                    if (data[i] == 0xFF && data[i + 1] == 0xD9)
                    {
                        return i + 2;
                    }
                }

                break;
            }

            case ImageFormat.Png:
            {
                // PNG ends with the IEND chunk: length(0) + "IEND" + CRC.
                ReadOnlySpan<byte> iend = [0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82];
                for (var i = data.Length - iend.Length; i >= 0; i--)
                {
                    if (data.Slice(i, iend.Length).SequenceEqual(iend))
                    {
                        return i + iend.Length;
                    }
                }

                break;
            }
        }

        return data.Length;
    }
}
