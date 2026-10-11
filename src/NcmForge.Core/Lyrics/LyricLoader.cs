using System.Text;

namespace NcmForge.Core.Lyrics;

/// <summary>
/// Finds and decodes the <c>.lrc</c> sidecar that accompanies a download.
/// </summary>
/// <remarks>
/// Encoding is the awkward part in practice: Chinese lyric files in the wild are a mix of UTF-8,
/// UTF-16 and GBK, frequently without a BOM. Decoding is therefore attempted in order of
/// certainty — BOM, then strict UTF-8, then GBK — rather than assumed.
/// </remarks>
public static class LyricLoader
{
    private static bool _codePagesRegistered;

    /// <summary>
    /// Locates lyrics for a container: an exact sibling <c>.lrc</c> first, then a file named after
    /// the track title.
    /// </summary>
    public static LyricDocument? FindAndLoad(string ncmPath, string? title, Action<string>? log = null)
    {
        string? path = FindSidecar(ncmPath, title);
        if (path is null)
        {
            log?.Invoke("lyrics    : no .lrc sidecar found");
            return null;
        }

        LyricDocument? document = Load(path, log);
        if (document is null || document.IsEmpty)
        {
            log?.Invoke($"lyrics    : {Path.GetFileName(path)} contained no lyric lines");
            return null;
        }

        log?.Invoke($"lyrics    : {Path.GetFileName(path)} ({document.Lines.Count} lines)");
        return document;
    }

    /// <summary>Returns the path of the lyrics file that would be used, without reading it.</summary>
    public static string? FindSidecar(string ncmPath, string? title)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(ncmPath)) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(ncmPath);

        string exact = Path.Combine(directory, stem + ".lrc");
        if (File.Exists(exact))
        {
            return exact;
        }

        if (string.IsNullOrWhiteSpace(title) || !Directory.Exists(directory))
        {
            return null;
        }

        string wanted = Normalize(title!);
        string[] candidates;
        try
        {
            candidates = Directory.GetFiles(directory, "*.lrc");
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        foreach (string candidate in candidates)
        {
            if (Normalize(Path.GetFileNameWithoutExtension(candidate)) == wanted)
            {
                return candidate;
            }
        }

        // Last resort: a title embedded in a longer file name, as when a download keeps the
        // "Artist - Title" form while the container stores only the title.
        foreach (string candidate in candidates)
        {
            string name = Normalize(Path.GetFileNameWithoutExtension(candidate));
            if (name.Length > 0 && (name.Contains(wanted, StringComparison.Ordinal) || wanted.Contains(name, StringComparison.Ordinal)))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Reads and parses a lyrics file.</summary>
    public static LyricDocument? Load(string lrcPath, Action<string>? log = null)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(lrcPath);
            return LyricDocument.Parse(Decode(bytes));
        }
        catch (IOException ex)
        {
            log?.Invoke($"lyrics    : could not read {Path.GetFileName(lrcPath)} ({ex.Message})");
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            log?.Invoke($"lyrics    : could not read {Path.GetFileName(lrcPath)} ({ex.Message})");
            return null;
        }
    }

    /// <summary>Decodes lyric bytes, detecting the encoding rather than assuming one.</summary>
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes[3..]);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes[2..]);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        }

        // Strict UTF-8 rejects invalid sequences, which is what distinguishes it from GBK bytes.
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Not UTF-8: fall through to the Chinese legacy code page.
        }

        try
        {
            EnsureCodePages();
            return Encoding.GetEncoding("GB18030").GetString(bytes);
        }
        catch (ArgumentException)
        {
            // The code page provider is unavailable; Latin-1 at least preserves the bytes.
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private static void EnsureCodePages()
    {
        if (_codePagesRegistered)
        {
            return;
        }

        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _codePagesRegistered = true;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException)
        {
            _codePagesRegistered = true;
        }
    }

    /// <summary>Lower-cases and strips separators so file names can be compared loosely.</summary>
    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }
}
