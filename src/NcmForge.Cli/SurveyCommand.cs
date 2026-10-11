using System.Text;
using NcmForge.Core.Audio;
using NcmForge.Core.Audio.Flac;
using NcmForge.Core.Ncm;
using NcmForge.Core.Pipeline;

namespace NcmForge.Cli;

/// <summary>
/// Summarises many containers at once, one row per file.
/// </summary>
/// <remarks>
/// Written for batch reconnaissance: given a folder of downloads from different store product
/// lines, it answers "which codecs and layouts actually occur here" without opening files by hand.
/// </remarks>
internal static class SurveyCommand
{
    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: ncmforge survey <file.ncm | directory> [--recursive] [--json]");
            return 2;
        }

        bool recursive = args.Contains("--recursive");
        bool json = args.Contains("--json");
        var inputs = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();

        List<string> files = [];
        foreach (string input in inputs)
        {
            if (File.Exists(input))
            {
                files.Add(Path.GetFullPath(input));
            }
            else if (Directory.Exists(input))
            {
                files.AddRange(Directory.EnumerateFiles(
                    input, "*.ncm", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly));
            }
        }

        files = files.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0)
        {
            Console.Error.WriteLine("no .ncm files found");
            return 1;
        }

        var rows = new List<SurveyRow>();
        foreach (string file in files)
        {
            rows.Add(Inspect(file));
        }

        if (json)
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(rows, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        var text = new StringBuilder();
        text.AppendLine($"{"file",-52} {"layout",-20} {"container",-9} {"codec",-34} {"fourcc",-7} {"ch",-3} {"rate",-6} {"dur",-9} {"meta",-6}");

        foreach (SurveyRow row in rows)
        {
            text.AppendLine(
                $"{Truncate(row.File, 52),-52} {row.Layout,-20} {row.Container,-9} {Truncate(row.Codec, 34),-34} " +
                $"{row.FourCc,-7} {row.Channels,-3} {row.SampleRate,-6} {row.Duration,-9} {row.MetadataFormat,-6}");
        }

        text.AppendLine();
        text.AppendLine("=== codec histogram ===");
        foreach (IGrouping<string, SurveyRow> group in rows.GroupBy(r => $"{r.Container}/{r.FourCc}").OrderByDescending(g => g.Count()))
        {
            text.AppendLine($"  {group.Count(),3} x {group.Key}");
        }

        text.AppendLine();
        text.AppendLine("=== layout histogram ===");
        foreach (IGrouping<string, SurveyRow> group in rows.GroupBy(r => r.Layout).OrderByDescending(g => g.Count()))
        {
            text.AppendLine($"  {group.Count(),3} x {group.Key}");
        }

        text.AppendLine();
        text.AppendLine("=== errors ===");
        var errors = rows.Where(r => r.Error is not null).ToList();
        if (errors.Count == 0)
        {
            text.AppendLine("  (none)");
        }
        else
        {
            foreach (SurveyRow row in errors)
            {
                text.AppendLine($"  {row.File}: {row.Error}");
            }
        }

        Console.WriteLine(text.ToString());
        return errors.Count == 0 ? 0 : 1;
    }

    private static SurveyRow Inspect(string path)
    {
        try
        {
            using NcmFile ncm = NcmFile.Open(path);

            int? channels = ConversionPipeline.ResolveChannelCount(ncm);
            int? rate = ConversionPipeline.ResolveSampleRate(ncm) ?? ncm.Mp4Track?.SampleRate;
            TimeSpan? duration = ncm.Mp4Track?.Duration;

            if (duration is null)
            {
                using Stream payload = ncm.OpenDecryptedAudioStream();
                FlacStreamInfo? flac = FlacProbe.Read(payload);
                if (flac is not null)
                {
                    rate = flac.SampleRate;
                    duration = flac.Duration;
                }
            }

            return new SurveyRow(
                Path.GetFileName(path),
                ncm.Layout.ToString(),
                ncm.Format.ToString(),
                ncm.CodecDisplayName,
                ncm.Mp4Track?.CodecFourCc ?? "-",
                channels?.ToString() ?? "-",
                rate?.ToString() ?? "-",
                duration is null ? "-" : $"{duration.Value:m\\:ss\\.ff}",
                ncm.Metadata.Format ?? "-",
                Math.Round((double)ncm.ContainerLength / (1 << 20), 1),
                null);
        }
        catch (Exception ex)
        {
            return new SurveyRow(Path.GetFileName(path), "-", "-", "-", "-", "-", "-", "-", "-", 0, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string Truncate(string value, int length)
        => value.Length <= length ? value : value[..(length - 1)] + "…";

    private sealed record SurveyRow(
        string File,
        string Layout,
        string Container,
        string Codec,
        string FourCc,
        string Channels,
        string SampleRate,
        string Duration,
        string MetadataFormat,
        double SizeMb,
        string? Error);
}
