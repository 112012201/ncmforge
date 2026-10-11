using System.Diagnostics;

namespace NcmForge.Core.Pipeline;

/// <summary>
/// Runs a user-supplied decoder for codecs nothing local implements.
/// </summary>
/// <remarks>
/// AVS3-P3 (Audio Vivid) is the motivating case: NetEase's immersive downloads carry it, no ffmpeg
/// build decodes it, and the known reference decoders are standalone programs. Rather than embed a
/// decoder that may be slow, unfinished, or legally encumbered, the pipeline shells out to one the
/// user supplies, so the tool stays shippable and the choice stays theirs.
/// </remarks>
public sealed class ExternalDecoder
{
    private readonly string _commandTemplate;

    public ExternalDecoder(string commandTemplate)
    {
        if (string.IsNullOrWhiteSpace(commandTemplate))
        {
            throw new ArgumentException("The decoder command template is empty.", nameof(commandTemplate));
        }

        _commandTemplate = commandTemplate;
    }

    /// <summary>Expands the template for one job, producing the command line to execute.</summary>
    public string BuildCommandLine(string inputPath, string outputPath, int channels, int sampleRate)
    {
        return _commandTemplate
            .Replace("{input}", inputPath, StringComparison.Ordinal)
            .Replace("{output}", outputPath, StringComparison.Ordinal)
            .Replace("{channels}", channels.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{sampleRate}", sampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    /// <summary>True when the template references both required placeholders.</summary>
    public bool IsUsable(out string? problem)
    {
        if (!_commandTemplate.Contains("{input}", StringComparison.Ordinal))
        {
            problem = "the decoder command must contain {input}";
            return false;
        }

        if (!_commandTemplate.Contains("{output}", StringComparison.Ordinal))
        {
            problem = "the decoder command must contain {output}";
            return false;
        }

        problem = null;
        return true;
    }

    /// <summary>
    /// Runs the decoder and verifies it produced a usable WAV file.
    /// </summary>
    public async Task DecodeAsync(string inputPath, string outputPath, int channels, int sampleRate,
        Action<string>? log, CancellationToken cancellationToken)
    {
        string commandLine = BuildCommandLine(inputPath, outputPath, channels, sampleRate);
        log?.Invoke($"decoder: {commandLine}");

        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(commandLine);
        }
        else
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(commandLine);
        }

        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new ConversionException($"Could not start the external decoder: {commandLine}");
        }

        Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw;
        }

        string error = await errorTask.ConfigureAwait(false);
        string output = await outputTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            string detail = string.IsNullOrWhiteSpace(error) ? output : error;
            throw new ConversionException(
                $"The external decoder exited with code {process.ExitCode}.{Environment.NewLine}{Truncate(detail)}");
        }

        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length < 44)
        {
            throw new ConversionException(
                "The external decoder reported success but produced no usable WAV file. " +
                $"The command must write its PCM output to the path given by {{output}} ({outputPath}).");
        }

        log?.Invoke($"decoder: produced {new FileInfo(outputPath).Length:N0} bytes of PCM");
    }

    private static string Truncate(string text, int limit = 2000)
        => text.Length <= limit ? text : text[..limit] + "...";
}
