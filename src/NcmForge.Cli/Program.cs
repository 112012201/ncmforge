using System.Text;
using NcmForge.Core.Audio;
using NcmForge.Core.Audio.Mp4;
using NcmForge.Core.Ncm;

namespace NcmForge.Cli;

/// <summary>Command dispatch for the diagnostic front end.</summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: ncmforge <doctor|survey|inspect|crack|extract-es|convert|pack> [options]");
            return 2;
        }

        string verb = args[0];

        // Verbs that take no container path handle their own argument set.
        if (verb == "doctor")
        {
            return DoctorCommand.Run(args[1..]);
        }

        if (verb is "pack" or "survey")
        {
            return verb == "pack" ? PackCommand.Run(args[1..]) : SurveyCommand.Run(args[1..]);
        }

        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: ncmforge <doctor|survey|inspect|crack|extract-es|convert|pack> [options]");
            return 2;
        }

        string path = args[1];

        if (verb == "extract-es")
        {
            return ExtractElementaryStream(args[1], args.Length > 2 ? args[2] : null);
        }

        if (verb == "convert")
        {
            try
            {
                return ConvertCommand.RunAsync(args[1..]).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error: {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
        }

        string? dumpDir = null;
        for (var i = 2; i < args.Length - 1; i++)
        {
            if (args[i] == "--dump")
            {
                dumpDir = args[i + 1];
            }
        }

        try
        {
            return verb switch
            {
                "inspect" => Inspect(path, dumpDir),
                "crack" => CrackCommand.Run(path, args[2..]),
                _ => Fail($"unknown verb '{verb}'"),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    /// <summary>
    /// Pulls the coded samples out of an ISO base media file into a raw elementary stream.
    /// Useful both for feeding an external decoder and for proving a tag rewrite left the media
    /// data addressable.
    /// </summary>
    private static int ExtractElementaryStream(string input, string? output)
    {
        using var source = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read);
        Mp4AudioTrack? track = Mp4Probe.Probe(source);
        if (track is null)
        {
            Console.Error.WriteLine($"error: {input} is not an ISO base media file");
            return 1;
        }

        output ??= Path.ChangeExtension(input, track.CodecFourCc == "av3a" ? ".av3a" : ".es");

        source.Position = 0;
        using (var destination = File.Create(output))
        {
            Mp4ExtractionResult? result = Mp4SampleExtractor.Extract(source, destination);
            if (result is null)
            {
                Console.Error.WriteLine("error: no readable audio sample table");
                return 1;
            }

            Console.WriteLine($"codec={result.CodecFourCc} samples={result.SampleCount} bytes={result.ByteCount}");
        }

        Console.WriteLine($"-> {output}");
        return 0;
    }

    private static int Inspect(string path, string? dumpDir)
    {
        var report = new StringBuilder();
        void Line(string text = "") => report.AppendLine(text);

        long fileSize = new FileInfo(path).Length;

        report.AppendLine($"container      : {path}");
        report.AppendLine($"size           : {fileSize:N0} bytes");

        using var ncm = NcmFile.Open(path);

        Line();
        Line("=== layout ===");
        Line($"key length     : {ncm.AudioKey.Length} bytes");
        Line($"metadata end   : 0x{ncm.MetadataEndOffset:X} ({ncm.MetadataEndOffset:N0})");
        Line($"audio offset   : 0x{ncm.AudioOffset:X} ({ncm.AudioOffset:N0})");
        Line($"layout         : {ncm.Layout}");
        Line($"audio length   : {ncm.AudioLength:N0} bytes");
        Line($"embedded image : {(ncm.EmbeddedImage is { } img ? $"{img.Format}, {img.ImageLength:N0} bytes at 0x{img.Offset:X} (declared {img.DeclaredLength:N0})" : "(none)")}");

        Line();
        Line("=== detected payload ===");
        Line($"format         : {ncm.Format}");
        Line($"detail         : {ncm.FormatDetail ?? "(none)"}");
        Line($"codec          : {ncm.CodecDisplayName}");
        if (ncm.Mp4Track is { } track)
        {
            Line($"mp4 fourcc     : {track.CodecFourCc}");
            Line($"mp4 channels   : {track.Channels}");
            Line($"mp4 sample rate: {track.SampleRate} Hz / {track.SampleSize} bit");
            Line($"mp4 duration   : {track.Duration}");
        }

        byte[] head = ReadDecryptedHead(ncm, AudioFormatDetector.SniffLength * 3);
        Line($"first {head.Length} decrypted bytes:");
        Line(HexDump(head));

        Line();
        Line("=== metadata (selector fields) ===");
        Line($"MusicId        : {ncm.Metadata.MusicId}");
        Line($"MusicName      : {ncm.Metadata.MusicName}");
        Line($"Artists        : {ncm.Metadata.ArtistDisplay}");
        Line($"Album          : {ncm.Metadata.Album}");
        Line($"AlbumId        : {ncm.Metadata.AlbumId}");
        Line($"AlbumPic       : {ncm.Metadata.AlbumPicUrl}");
        Line($"Format         : {ncm.Metadata.Format}");
        Line($"Bitrate        : {ncm.Metadata.Bitrate}");
        Line($"SampleRate     : {ncm.Metadata.SampleRate}");
        Line($"Channels       : {ncm.Metadata.Channels}");
        Line($"BitsPerSample  : {ncm.Metadata.BitsPerSample}");
        Line($"Duration       : {ncm.Metadata.Duration}");

        Line();
        Line("=== metadata (raw JSON) ===");
        Line(ncm.Metadata.RawJson);

        Console.WriteLine(report.ToString());

        if (dumpDir is not null)
        {
            Directory.CreateDirectory(dumpDir);
            string stem = Path.GetFileNameWithoutExtension(path);
            string outPath = Path.Combine(dumpDir, stem + ".decrypted.bin");
            ncm.ExtractAudioTo(outPath);
            Console.WriteLine($"dumped decrypted payload -> {outPath} ({new FileInfo(outPath).Length:N0} bytes)");

            if (ncm.ReadEmbeddedImage() is { } cover && ncm.EmbeddedImage is { } meta)
            {
                string coverPath = Path.Combine(dumpDir, stem + ".embedded" + ImageSignature.Extension(meta.Format));
                File.WriteAllBytes(coverPath, cover);
                Console.WriteLine($"dumped embedded cover    -> {coverPath} ({cover.Length:N0} bytes)");
            }
        }

        return 0;
    }

    private static byte[] ReadDecryptedHead(NcmFile ncm, int count)
    {
        using Stream audio = ncm.OpenDecryptedAudioStream();
        var buffer = new byte[count];
        int read = ReadFully(audio, buffer);
        Array.Resize(ref buffer, read);
        return buffer;
    }

    private static string HexDump(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < data.Length; i += 16)
        {
            int count = Math.Min(16, data.Length - i);
            sb.Append($"  {i:X4}  ");

            for (var j = 0; j < 16; j++)
            {
                sb.Append(j < count ? $"{data[i + j]:X2} " : "   ");
                if (j == 7)
                {
                    sb.Append(' ');
                }
            }

            sb.Append(' ');
            for (var j = 0; j < count; j++)
            {
                byte b = data[i + j];
                sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            }

            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
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
