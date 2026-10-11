using System.Buffers.Binary;
using System.Text.Json;
using NcmForge.Core.Util;

namespace NcmForge.Core.Ncm;

/// <summary>Values written into a container built by <see cref="NcmWriter"/>.</summary>
public sealed class NcmWriteOptions
{
    public string? Title { get; set; }

    public string? Artist { get; set; }

    public string? Album { get; set; }

    public long? MusicId { get; set; }

    public string Format { get; set; } = "mp3";

    public long? DurationMilliseconds { get; set; }

    public string? CoverUrl { get; set; }

    /// <summary>
    /// When set, the container is built in the immersive layout: a <c>uint32</c> length and the
    /// image in the clear, ahead of the encrypted audio.
    /// </summary>
    public byte[]? EmbeddedCover { get; set; }
}

/// <summary>
/// Builds .ncm containers.
/// </summary>
/// <remarks>
/// The format has no public specification, so the writer exists to pin the reverse-engineered
/// layout down in executable form: a round-trip test through this writer and <see cref="NcmFile"/>
/// is what keeps the reader honest. It also lets test suites build fixtures from free audio instead
/// of shipping copyrighted ones.
/// </remarks>
public static class NcmWriter
{
    /// <summary>Container magic.</summary>
    private static readonly byte[] Magic = "CTENFDAM"u8.ToArray();

    /// <summary>Bytes between the metadata block and the audio, or the cover block.</summary>
    private const int TrailerLength = 9;

    /// <summary>Writes a container to <paramref name="destination"/>.</summary>
    /// <param name="destination">Where the container is written.</param>
    /// <param name="audio">Plain (unencrypted) audio payload.</param>
    /// <param name="options">Metadata and layout.</param>
    /// <param name="audioKey">Per-file key; a random one is generated when omitted.</param>
    public static void Write(Stream destination, Stream audio, NcmWriteOptions options, byte[]? audioKey = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(options);

        audioKey ??= CreateKey();
        if (audioKey.Length == 0)
        {
            throw new ArgumentException("The audio key must not be empty.", nameof(audioKey));
        }

        byte[] audioBytes = ReadAll(audio);

        destination.Write(Magic);
        destination.Write([0x01, 0x70]);

        byte[] keyBlock = NcmCrypto.EncodeKeyBlock(audioKey);
        WriteInt32LittleEndian(destination, keyBlock.Length);
        destination.Write(keyBlock);

        byte[] metadataBlock = NcmCrypto.EncodeMetadataBlock(BuildMetadataJson(options));
        WriteInt32LittleEndian(destination, metadataBlock.Length);
        destination.Write(metadataBlock);

        // The audio is masked with the same periodic keystream the reader removes.
        var keystream = new NcmKeystream(audioKey);
        keystream.Apply(audioBytes, 0);

        // Trailer: a CRC32 over the masked payload, then five reserved bytes.
        Span<byte> trailer = stackalloc byte[TrailerLength];
        BinaryPrimitives.WriteUInt32LittleEndian(trailer, Crc32.Compute(audioBytes));
        destination.Write(trailer);

        if (options.EmbeddedCover is { Length: > 0 } cover)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)cover.Length);
            destination.Write(length);
            destination.Write(cover);
        }

        destination.Write(audioBytes);
    }

    /// <summary>Convenience overload writing straight to a file.</summary>
    public static void Write(string path, Stream audio, NcmWriteOptions options, byte[]? audioKey = null)
    {
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        Write(file, audio, options, audioKey);
    }

    /// <summary>Builds the metadata JSON in the shape NetEase itself uses.</summary>
    public static string BuildMetadataJson(NcmWriteOptions options)
    {
        var artist = new List<object[]>();
        if (!string.IsNullOrWhiteSpace(options.Artist))
        {
            artist.Add([options.Artist!, "1"]);
        }

        var payload = new Dictionary<string, object?>
        {
            ["musicId"] = (options.MusicId ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["musicName"] = options.Title ?? "Unknown",
            ["artist"] = artist,
            ["albumId"] = "1",
            ["album"] = options.Album ?? options.Title ?? "Unknown",
            ["albumPic"] = options.CoverUrl ?? string.Empty,
            ["bitrate"] = 320000,
            ["duration"] = options.DurationMilliseconds ?? 0,
            ["format"] = options.Format,
        };

        return JsonSerializer.Serialize(payload);
    }

    /// <summary>Generates a random per-file key of the length real containers use.</summary>
    public static byte[] CreateKey()
    {
        // Real keys are ~98 bytes of printable ASCII, which is a NetEase quirk rather than a
        // requirement; any non-empty key works with the keystream.
        var key = new byte[98];
        const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        var random = new Random();

        for (var i = 0; i < key.Length; i++)
        {
            key[i] = (byte)Alphabet[random.Next(Alphabet.Length)];
        }

        return key;
    }

    private static void WriteInt32LittleEndian(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static byte[] ReadAll(Stream stream)
    {
        if (stream is MemoryStream memory && memory.Position == 0)
        {
            return memory.ToArray();
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
