using NcmForge.Core.Ncm;

namespace NcmForge.Cli;

/// <summary>
/// Builds .ncm containers from ordinary audio files, using the shared <see cref="NcmWriter"/>.
/// </summary>
/// <remarks>
/// Useful for exercising the conversion paths in the field without shipping copyrighted fixtures,
/// and for reproducing unusual layouts — including the immersive embedded-cover variant — on demand.
/// </remarks>
internal static class PackCommand
{
    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Usage();
            return 2;
        }

        string input = args[0];
        string output = args[1];

        var options = new NcmWriteOptions
        {
            Title = Path.GetFileNameWithoutExtension(input),
            Artist = "NcmForge Test",
        };
        options.Album = options.Title;
        options.Format = Path.GetExtension(input).TrimStart('.').ToLowerInvariant();

        for (var i = 2; i < args.Length - 1; i++)
        {
            string value = args[i + 1];
            switch (args[i])
            {
                case "--title": options.Title = value; break;
                case "--artist": options.Artist = value; break;
                case "--album": options.Album = value; break;
                case "--cover-url": options.CoverUrl = value; break;
                case "--music-id": options.MusicId = long.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                case "--cover": options.EmbeddedCover = File.ReadAllBytes(value); break;
            }
        }

        using var audio = File.OpenRead(input);
        NcmWriter.Write(output, audio, options);

        long size = new FileInfo(output).Length;
        Console.WriteLine(
            $"packed {new FileInfo(input).Length:N0} bytes of {options.Format} audio" +
            (options.EmbeddedCover is null ? string.Empty : $" + {options.EmbeddedCover.Length:N0} bytes of cover") +
            $" -> {output} ({size:N0} bytes)");

        return 0;
    }

    private static void Usage()
    {
        Console.Error.WriteLine(
            "usage: ncmforge pack <input-audio> <output.ncm> " +
            "[--title T] [--artist A] [--album B] [--cover img] [--cover-url URL] [--music-id N]");
    }
}
