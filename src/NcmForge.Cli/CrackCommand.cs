using System.Text;
using NcmForge.Core.Audio;
using NcmForge.Core.Ncm;
using NcmForge.Core.Util;

namespace NcmForge.Cli;

/// <summary>
/// Locates the true audio payload inside a container by brute force.
/// </summary>
/// <remarks>
/// The keystream is a 256-byte repeating pattern, so decrypting at offset X with phase 0 only
/// produces coherent plaintext when X is congruent to the real payload start. Scanning every
/// offset therefore finds the payload start without relying on any assumption about the layout,
/// which is what makes it useful for containers whose trailer differs from the documented one.
/// </remarks>
internal static class CrackCommand
{
    private const int Sniff = AudioFormatDetector.SniffLength;

    public static int Run(string path, string[] extraArgs)
    {
        long? extractFrom = null;
        string? extractOut = null;
        string extractKey = "padded";

        for (var i = 0; i < extraArgs.Length; i++)
        {
            switch (extraArgs[i])
            {
                case "--extract-from" when i + 1 < extraArgs.Length:
                    extractFrom = ParseOffset(extraArgs[++i]);
                    break;
                case "--extract-out" when i + 1 < extraArgs.Length:
                    extractOut = extraArgs[++i];
                    break;
                case "--extract-key" when i + 1 < extraArgs.Length:
                    extractKey = extraArgs[++i];
                    break;
            }
        }

        byte[] file = File.ReadAllBytes(path);
        var report = new StringBuilder();
        void Line(string text = "") => report.AppendLine(text);

        Line($"container : {path}");
        Line($"size      : {file.Length:N0} bytes");

        if (file.Length < 32 || !file.AsSpan(0, 8).SequenceEqual("CTENFDAM"u8))
        {
            Console.Error.WriteLine("error: not an .ncm container");
            return 1;
        }

        int keyLength = BitConverter.ToInt32(file, 10);
        if (keyLength <= 0 || 14 + keyLength + 4 > file.Length)
        {
            Console.Error.WriteLine($"error: implausible key length {keyLength}");
            return 1;
        }

        ReadOnlySpan<byte> rawKey = file.AsSpan(14, keyLength);
        (byte[] padded, byte[] verbatim, bool prefixValid, int unpaddedLength) = NcmCrypto.DecodeKeyCandidates(rawKey);

        int metadataLength = BitConverter.ToInt32(file, 14 + keyLength);
        long metadataEnd = 14L + keyLength + 4 + metadataLength;

        Line();
        Line("=== key layer ===");
        Line($"raw key block     : {keyLength} bytes");
        Line($"aes plaintext     : {padded.Length + 17} bytes after padding strip (raw block decrypts to {keyLength})");
        Line($"unpadded length   : {unpaddedLength}");
        Line($"'neteasecloudmusic' prefix present : {prefixValid}");
        Line($"padded key length : {padded.Length}");
        Line($"verbatim key len  : {verbatim.Length}");
        Line($"padded key hex    : {Convert.ToHexString(padded.AsSpan(0, Math.Min(24, padded.Length)))}...");
        Line();
        Line("=== metadata layer ===");
        Line($"metadata length   : {metadataLength}");
        Line($"metadata end      : 0x{metadataEnd:X}");

        Line();
        Line("=== trailer bytes at metadata end (16) ===");
        Line($"  {Convert.ToHexString(file.AsSpan((int)metadataEnd, 16))}");
        uint storedCrc = BitConverter.ToUInt32(file, (int)metadataEnd);
        Line($"  first 4 as LE u32 : 0x{storedCrc:X8}");

        // ---------- phase-0 signature scan ----------
        Line();
        Line("=== phase-0 signature scan ===");

        var results = new List<(string Key, long Offset, AudioFormat Format, string? Detail)>();

        foreach ((string name, byte[] key) in new[] { ("padded", padded), ("verbatim", verbatim) })
        {
            var keystream = new NcmKeystream(key);
            var probe = new byte[16];
            var buffer = new byte[Sniff];
            long limit = file.Length - Sniff;

            for (long x = metadataEnd; x <= limit; x++)
            {
                // The pre-filter must run on *decrypted* bytes: the mask is a 256-byte pattern
                // whose phase at X is 1 when X is assumed to be the payload start, so a complete
                // 16-byte decrypt is the cheapest correct filter.
                Buffer.BlockCopy(file, (int)x, probe, 0, probe.Length);
                keystream.Apply(probe, 0);

                byte b0 = probe[0];
                bool interesting = b0 is 0x66 or 0x4F or 0x52 or 0x0B or 0x49
                    || (probe[4] == 0x66 && probe[5] == 0x74 && probe[6] == 0x79 && probe[7] == 0x70);

                if (!interesting)
                {
                    continue;
                }

                // Only strong multi-byte signatures are reported: an MPEG frame sync alone occurs
                // by chance thousands of times across 14 MB of noise.
                Buffer.BlockCopy(file, (int)x, buffer, 0, Sniff);
                keystream.Apply(buffer, 0);

                AudioFormat format = AudioFormatDetector.Detect(buffer, out string? detail);
                if (format is AudioFormat.Flac or AudioFormat.Mp4 or AudioFormat.Ogg or AudioFormat.Wav
                    or AudioFormat.Eac3 or AudioFormat.Ac3)
                {
                    results.Add((name, x, format, detail));
                }
            }
        }

        if (results.Count == 0)
        {
            Line("no coherent payload start found for either key reading.");
        }
        else
        {
            Line($"found {results.Count} candidate(s):");
            foreach ((string key, long offset, AudioFormat format, string? detail) in
                     results.OrderBy(r => r.Offset).ThenBy(r => r.Key).Take(60))
            {
                Line($"  key={key,-8} offset=0x{offset:X8} ({offset,12:N0})  {format}{(detail is null ? string.Empty : $" [{detail}]")}");
            }
        }

        // ---------- CRC hypothesis ----------
        Line();
        Line("=== trailer CRC32 hypothesis ===");
        Line($"stored u32 = 0x{storedCrc:X8}");
        foreach ((string name, byte[] key) in new[] { ("padded", padded), ("verbatim", verbatim) })
        {
            foreach (long start in CandidateStarts(file, metadataEnd, results))
            {
                if (start < 0 || start >= file.Length)
                {
                    continue;
                }

                var keystream = new NcmKeystream(key);
                var chunk = new byte[1 << 20];
                uint crc = 0;
                long position = start;
                long remaining = file.Length - start;
                bool first = true;

                while (remaining > 0)
                {
                    int want = (int)Math.Min(chunk.Length, remaining);
                    Buffer.BlockCopy(file, (int)position, chunk, 0, want);
                    keystream.Apply(chunk.AsSpan(0, want), position - start);

                    crc = first ? Crc32.Compute(chunk.AsSpan(0, want)) : Crc32.ComputeIncremental(crc, chunk.AsSpan(0, want));
                    first = false;

                    position += want;
                    remaining -= want;
                }

                string verdict = crc == storedCrc ? "  *** MATCH ***" : string.Empty;
                Line($"  key={name,-8} start=0x{start:X8}  crc32=0x{crc:X8}{verdict}");
            }
        }

        Console.WriteLine(report.ToString());

        // Optional: dump a confirmed payload so an external decoder can validate the keystream.
        if (extractFrom is { } from && extractOut is not null)
        {
            byte[] key = extractKey == "verbatim" ? verbatim : padded;
            var keystream = new NcmKeystream(key);
            long length = file.Length - from;
            var buffer = new byte[length];
            Buffer.BlockCopy(file, (int)from, buffer, 0, (int)length);
            keystream.Apply(buffer, 0);
            File.WriteAllBytes(extractOut, buffer);
            Console.WriteLine($"wrote {length:N0} bytes from 0x{from:X} (key={extractKey}) -> {extractOut}");
        }

        return 0;
    }

    private static long ParseOffset(string text)
        => text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.Parse(text[2..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture)
            : long.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

    private static IEnumerable<long> CandidateStarts(byte[] file, long metadataEnd, List<(string Key, long Offset, AudioFormat Format, string? Detail)> hits)
    {
        var seen = new HashSet<long>();
        var starts = new List<long>();

        void Add(long value)
        {
            if (value >= 0 && value < file.Length && seen.Add(value))
            {
                starts.Add(value);
            }
        }

        Add(metadataEnd);
        Add(metadataEnd + 9);

        // The plaintext JPEG observed in immersive containers sits between the trailer and the audio.
        for (long i = metadataEnd; i < file.Length - 2; i++)
        {
            if (file[i] == 0xFF && file[i + 1] == 0xD9)
            {
                for (long k = 0; k <= 8; k++)
                {
                    Add(i + 1 + k);
                }

                break;
            }
        }

        foreach ((string _, long offset, AudioFormat _, string? _) in hits)
        {
            Add(offset);
        }

        return starts;
    }
}
