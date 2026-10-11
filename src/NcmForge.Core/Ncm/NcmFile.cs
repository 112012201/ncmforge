using NcmForge.Core.Audio;
using NcmForge.Core.Audio.Mp4;

namespace NcmForge.Core.Ncm;

/// <summary>
/// Reader for the NetEase Cloud Music <c>.ncm</c> container: parses the header, unwraps the
/// per-file key and JSON metadata, locates the audio payload, and removes the keystream.
/// </summary>
public sealed class NcmFile : IDisposable
{
    /// <summary>Eight-byte container magic ("CTENFDAM").</summary>
    private static readonly byte[] Magic = "CTENFDAM"u8.ToArray();

    /// <summary>
    /// Bytes between the metadata block and the payload in both known layouts: a CRC32 followed by
    /// five reserved bytes.
    /// </summary>
    private const int StandardTrailerLength = 9;

    /// <summary>How far a fallback signature scan will look for a payload start.</summary>
    private const int MaxScanDistance = 64 << 20;

    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly NcmKeystream _keystream;

    private AudioFormat? _cachedFormat;
    private string? _cachedFormatDetail;
    private Mp4AudioTrack? _cachedMp4Track;
    private bool _mp4Probed;

    private NcmFile(Stream stream, bool ownsStream, NcmMetadata metadata, NcmKeystream keystream,
        byte[] audioKey, long metadataEndOffset, long audioOffset, NcmPayloadLayout layout,
        NcmEmbeddedImage? embeddedImage)
    {
        _stream = stream;
        _ownsStream = ownsStream;
        _keystream = keystream;
        Metadata = metadata;
        AudioKey = audioKey;
        MetadataEndOffset = metadataEndOffset;
        AudioOffset = audioOffset;
        Layout = layout;
        EmbeddedImage = embeddedImage;
    }

    /// <summary>JSON metadata embedded in the container.</summary>
    public NcmMetadata Metadata { get; }

    /// <summary>The recovered per-file RC4 key. Exposed for diagnostics and regression tests.</summary>
    public byte[] AudioKey { get; }

    /// <summary>Offset of the first byte after the metadata block, in the container.</summary>
    public long MetadataEndOffset { get; }

    /// <summary>Offset of the first encrypted audio byte, in the container.</summary>
    public long AudioOffset { get; }

    /// <summary>Which layout was recognised.</summary>
    public NcmPayloadLayout Layout { get; }

    /// <summary>Cover art embedded in the container, when the layout carries one.</summary>
    public NcmEmbeddedImage? EmbeddedImage { get; }

    /// <summary>Length of the encrypted audio payload.</summary>
    public long AudioLength => _stream.Length - AudioOffset;

    /// <summary>Total container size in bytes.</summary>
    public long ContainerLength => _stream.Length;

    /// <summary>Codec detected from the decrypted payload's leading bytes.</summary>
    public AudioFormat Format
    {
        get
        {
            EnsureFormat();
            return _cachedFormat!.Value;
        }
    }

    /// <summary>Extra detail from detection (for example the Dolby <c>bsid</c>).</summary>
    public string? FormatDetail
    {
        get
        {
            EnsureFormat();
            return _cachedFormatDetail;
        }
    }

    /// <summary>
    /// Audio track description when the payload is an ISO base media file; otherwise <c>null</c>.
    /// </summary>
    public Mp4AudioTrack? Mp4Track
    {
        get
        {
            if (!_mp4Probed)
            {
                _mp4Probed = true;
                if (Format == AudioFormat.Mp4)
                {
                    using Stream audio = OpenDecryptedAudioStream();
                    _cachedMp4Track = Mp4Probe.Probe(audio);
                }
            }

            return _cachedMp4Track;
        }
    }

    /// <summary>
    /// The codec actually carried, preferring the MP4 sample entry over the sniffed magic because
    /// the sample entry names the codec while the magic only says "ISO base media".
    /// </summary>
    public string CodecDisplayName
    {
        get
        {
            if (Mp4Track is { } track)
            {
                return track.CodecName;
            }

            return Format switch
            {
                AudioFormat.Mp3 => "MPEG-1/2 Audio Layer III (MP3)",
                AudioFormat.Flac => "FLAC",
                AudioFormat.Eac3 => FormatDetail ?? "Dolby Digital Plus (E-AC-3)",
                AudioFormat.Ac3 => FormatDetail ?? "Dolby Digital (AC-3)",
                AudioFormat.Aac => "AAC (ADTS)",
                AudioFormat.Ogg => "Ogg",
                AudioFormat.Wav => "RIFF/WAVE",
                _ => "unknown",
            };
        }
    }

    public static NcmFile Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
        try
        {
            return Open(stream, leaveOpen: false);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens an .ncm container over <paramref name="stream"/>. The stream must be seekable; a
    /// non-seekable source is buffered to memory first.
    /// </summary>
    public static NcmFile Open(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);

        Stream source = stream;
        bool ownsSource = !leaveOpen;

        if (!stream.CanSeek)
        {
            var buffered = new MemoryStream();
            stream.CopyTo(buffered);
            buffered.Position = 0;
            source = buffered;
            ownsSource = true;
        }

        try
        {
            return Parse(source, ownsSource);
        }
        catch
        {
            if (ownsSource)
            {
                source.Dispose();
            }

            throw;
        }
    }

    /// <summary>Quick check that a file looks like an .ncm container, without decrypting anything.</summary>
    public static bool IsNcmFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> magic = stackalloc byte[8];
            return stream.Read(magic) == 8 && magic.SequenceEqual(Magic);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static NcmFile Parse(Stream stream, bool ownsStream)
    {
        long containerLength = stream.Length;
        if (containerLength < 16)
        {
            throw new NcmFormatException("File is too small to be an .ncm container.");
        }

        Span<byte> magic = stackalloc byte[8];
        stream.ReadExactly(magic);
        if (!magic.SequenceEqual(Magic))
        {
            throw new NcmFormatException(
                "Not an .ncm container: the file does not begin with the CTENFDAM magic. " +
                "Unencrypted MP3/FLAC files need no conversion.");
        }

        stream.Seek(2, SeekOrigin.Current);
        int keyLength = ReadInt32LittleEndian(stream);
        if (keyLength <= 0 || keyLength > 1 << 20 || stream.Position + keyLength > containerLength)
        {
            throw new NcmFormatException($"Declared key block length {keyLength} is out of range.");
        }

        byte[] rawKeyBlock = new byte[keyLength];
        stream.ReadExactly(rawKeyBlock);
        byte[] audioKey = NcmCrypto.DecodeKeyBlock(rawKeyBlock);

        int metadataLength = ReadInt32LittleEndian(stream);
        if (metadataLength < 0 || metadataLength > 1 << 24 || stream.Position + metadataLength > containerLength)
        {
            throw new NcmFormatException($"Declared metadata block length {metadataLength} is out of range.");
        }

        byte[] rawMetadataBlock = new byte[metadataLength];
        stream.ReadExactly(rawMetadataBlock);
        NcmMetadata metadata = NcmMetadata.Parse(NcmCrypto.DecodeMetadataBlock(rawMetadataBlock));

        long metadataEnd = stream.Position;
        var keystream = new NcmKeystream(audioKey);

        (long audioOffset, NcmPayloadLayout layout, NcmEmbeddedImage? image) =
            ResolvePayload(stream, keystream, metadataEnd, containerLength);

        return new NcmFile(stream, ownsStream, metadata, keystream, audioKey, metadataEnd, audioOffset, layout, image);
    }

    /// <summary>
    /// Locates the first audio byte and any embedded cover art.
    /// </summary>
    /// <remarks>
    /// Two layouts are known and both are confirmed against the payload's own signature rather
    /// than trusted: the documented <c>[metadata][CRC32+5][audio]</c>, and an immersive variant
    /// <c>[metadata][CRC32+5][uint32 cover length][cover][audio]</c> whose cover is stored in the
    /// clear. A bounded signature scan covers anything else.
    /// </remarks>
    private static (long Offset, NcmPayloadLayout Layout, NcmEmbeddedImage? Image) ResolvePayload(
        Stream stream, NcmKeystream keystream, long metadataEnd, long containerLength)
    {
        long trailerEnd = metadataEnd + StandardTrailerLength;

        // Immersive layout: a uint32 block length followed by a clear-text image.
        NcmEmbeddedImage? image = TryReadEmbeddedImage(stream, trailerEnd, containerLength);
        if (image is not null)
        {
            long afterImage = image.Offset + image.DeclaredLength;
            if (afterImage < containerLength && ProbeFormat(stream, keystream, afterImage) != AudioFormat.Unknown)
            {
                return (afterImage, NcmPayloadLayout.AfterEmbeddedImage, image);
            }

            // The image is real but the payload did not follow it; fall through to other strategies
            // while still reporting the cover.
        }

        if (trailerEnd < containerLength && ProbeFormat(stream, keystream, trailerEnd) != AudioFormat.Unknown)
        {
            return (trailerEnd, NcmPayloadLayout.Standard, image);
        }

        long limit = Math.Min(containerLength - 16, trailerEnd + MaxScanDistance);
        for (long candidate = metadataEnd; candidate <= limit; candidate++)
        {
            if (candidate == trailerEnd)
            {
                continue;
            }

            // The scan accepts only unambiguous signatures. Dolby's two-byte sync word occurs by
            // chance roughly once every 350 KB, so admitting it here would let a large payload
            // produce dozens of false starts ahead of the real one.
            if (IsUnambiguousSignature(ProbeFormat(stream, keystream, candidate)))
            {
                return (candidate, NcmPayloadLayout.Scanned, image);
            }
        }

        if (trailerEnd >= containerLength)
        {
            throw new NcmFormatException("The container holds no audio payload after its metadata block.");
        }

        return (trailerEnd, NcmPayloadLayout.Standard, image);
    }

    /// <summary>
    /// Reads the optional <c>[uint32 length][image]</c> block that immersive containers place
    /// between the trailer and the audio.
    /// </summary>
    private static NcmEmbeddedImage? TryReadEmbeddedImage(Stream stream, long lengthFieldOffset, long containerLength)
    {
        if (lengthFieldOffset + 4 + 8 > containerLength)
        {
            return null;
        }

        Span<byte> lengthBytes = stackalloc byte[4];
        if (ReadAt(stream, lengthFieldOffset, lengthBytes) != 4)
        {
            return null;
        }

        int declared = lengthBytes[0] | (lengthBytes[1] << 8) | (lengthBytes[2] << 16) | (lengthBytes[3] << 24);
        long imageOffset = lengthFieldOffset + 4;

        if (declared <= 0 || imageOffset + declared > containerLength)
        {
            return null;
        }

        Span<byte> head = stackalloc byte[16];
        int headRead = ReadAt(stream, imageOffset, head[..Math.Min(16, declared)]);
        ImageFormat format = ImageSignature.Detect(head[..headRead]);
        if (format == ImageFormat.Unknown)
        {
            return null;
        }

        // Trim to the image's own end marker so trailing container padding is never tagged on.
        int probeLength = Math.Min(declared, 4 << 20);
        byte[] block = new byte[probeLength];
        int read = ReadAt(stream, imageOffset, block);
        int imageLength = ImageSignature.FindImageLength(block.AsSpan(0, read), format);

        return new NcmEmbeddedImage(imageOffset, declared, imageLength, format);
    }

    /// <summary>Reads the embedded cover art bytes, trimmed to the image itself.</summary>
    public byte[]? ReadEmbeddedImage()
    {
        if (EmbeddedImage is not { } image)
        {
            return null;
        }

        var buffer = new byte[image.ImageLength];
        int read = ReadAt(_stream, image.Offset, buffer);
        return read == buffer.Length ? buffer : buffer[..read];
    }

    private void EnsureFormat()
    {
        if (_cachedFormat is not null)
        {
            return;
        }

        Span<byte> head = stackalloc byte[AudioFormatDetector.SniffLength];
        int read = ReadDecryptedAt(AudioOffset, head);
        _cachedFormat = AudioFormatDetector.Detect(head[..read], out _cachedFormatDetail);
    }

    /// <summary>
    /// True for signatures long enough that a chance match inside bulk data is implausible.
    /// </summary>
    private static bool IsUnambiguousSignature(AudioFormat format)
        => format is AudioFormat.Flac or AudioFormat.Mp4 or AudioFormat.Ogg or AudioFormat.Wav
            or AudioFormat.Mp3 or AudioFormat.Aac;

    private static AudioFormat ProbeFormat(Stream stream, NcmKeystream keystream, long offset)
    {
        Span<byte> head = stackalloc byte[AudioFormatDetector.SniffLength];
        int read = ReadAt(stream, offset, head);
        if (read < 4)
        {
            return AudioFormat.Unknown;
        }

        // Phase 0: the candidate is being tested as the payload start.
        keystream.Apply(head[..read], 0);
        return AudioFormatDetector.Detect(head[..read], out _);
    }

    private int ReadDecryptedAt(long audioOffset, Span<byte> destination)
    {
        long phase = audioOffset - AudioOffset;
        if (phase < 0)
        {
            return 0;
        }

        int read = ReadAt(_stream, audioOffset, destination);
        if (read > 0)
        {
            _keystream.Apply(destination[..read], phase);
        }

        return read;
    }

    /// <summary>
    /// Opens the decrypted payload as a seekable read-only stream. The returned stream shares the
    /// container's file handle, so this instance must stay alive while it is in use.
    /// </summary>
    public Stream OpenDecryptedAudioStream()
        => new NcmDecryptedStream(_stream, _keystream, AudioOffset, AudioLength, ownsBase: false);

    /// <summary>Streams the decrypted payload to <paramref name="destination"/> with optional progress.</summary>
    public void CopyAudioTo(Stream destination, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        const int BufferSize = 1 << 20;

        using Stream source = OpenDecryptedAudioStream();
        byte[] buffer = new byte[BufferSize];
        long total = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = source.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            destination.Write(buffer, 0, read);
            total += read;
            progress?.Report(total);
        }
    }

    /// <summary>Writes the decrypted payload to <paramref name="path"/>.</summary>
    public void ExtractAudioTo(string path, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        using var destination = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.SequentialScan);
        CopyAudioTo(destination, progress, cancellationToken);
    }

    private static int ReadInt32LittleEndian(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[4];
        stream.ReadExactly(buffer);
        return buffer[0] | (buffer[1] << 8) | (buffer[2] << 16) | (buffer[3] << 24);
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

    public void Dispose()
    {
        if (_ownsStream)
        {
            _stream.Dispose();
        }
    }
}
