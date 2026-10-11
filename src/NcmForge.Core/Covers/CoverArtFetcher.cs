using System.Text.Json;
using NcmForge.Core.Audio;
using NcmForge.Core.Ncm;
using NcmForge.Core.Tagging;

namespace NcmForge.Core.Covers;

/// <summary>What is known about a track when cover art is being looked up.</summary>
public sealed class CoverQuery
{
    public string? Title { get; init; }

    public string? Artist { get; init; }

    public string? Album { get; init; }

    /// <summary>NetEase song id, when the container carried one.</summary>
    public long? MusicId { get; init; }

    /// <summary>Cover URL declared by the container's own metadata.</summary>
    public string? NetEaseCoverUrl { get; init; }

    /// <summary>Cover art already present in the container, used before any network call.</summary>
    public CoverArt? Embedded { get; init; }

    /// <summary>Longest useful search string for databases that take free text.</summary>
    public string SearchText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Title))
            {
                parts.Add(Title!);
            }

            if (!string.IsNullOrWhiteSpace(Artist))
            {
                parts.Add(Artist!);
            }

            return string.Join(' ', parts);
        }
    }

    public static CoverQuery FromMetadata(NcmMetadata metadata, CoverArt? embedded = null)
    {
        return new CoverQuery
        {
            Title = metadata.MusicName,
            Artist = metadata.ArtistDisplay,
            Album = metadata.Album,
            MusicId = metadata.MusicId,
            NetEaseCoverUrl = metadata.AlbumPicUrl,
            Embedded = embedded,
        };
    }
}

/// <summary>A place cover art can be found.</summary>
public interface ICoverSource
{
    /// <summary>Short identifier used in reports, e.g. "netease".</summary>
    string Name { get; }

    Task<CoverArt?> FindAsync(HttpClient http, CoverQuery query, CancellationToken cancellationToken);
}

/// <summary>
/// Resolves cover art by trying a prioritised chain of sources.
/// </summary>
/// <remarks>
/// The order matters: art already inside the container is free and authoritative, NetEase's own
/// record is next because it matches the release the user actually bought, and general databases
/// follow as a fallback for tracks whose identifiers are missing.
/// </remarks>
public sealed class CoverArtFetcher
{
    private readonly IReadOnlyList<ICoverSource> _sources;
    private readonly HttpClient _http;

    public CoverArtFetcher(HttpClient http, IReadOnlyList<ICoverSource>? sources = null)
    {
        _http = http;
        _sources = sources ?? DefaultSources();
    }

    public static IReadOnlyList<ICoverSource> DefaultSources() =>
    [
        new NetEaseCoverSource(),
        new ITunesCoverSource(),
        new DeezerCoverSource(),
        new CoverArtArchiveSource(),
    ];

    /// <summary>
    /// Finds the best available cover. Returns <c>null</c> only when every source is exhausted.
    /// </summary>
    public async Task<CoverArt?> FetchAsync(CoverQuery query, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        // Container-embedded art needs no network at all.
        if (query.Embedded is { } embedded)
        {
            log?.Invoke($"cover: using embedded image ({embedded.Length:N0} bytes, {embedded.Format})");
            return embedded.WithSource("embedded");
        }

        foreach (ICoverSource source in _sources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                CoverArt? cover = await source.FindAsync(_http, query, cancellationToken).ConfigureAwait(false);
                if (cover is not null)
                {
                    log?.Invoke($"cover: {source.Name} supplied {cover.Length:N0} bytes ({cover.Format})");
                    return cover;
                }

                log?.Invoke($"cover: {source.Name} found nothing");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A failing source must never fail the conversion; the next one still gets a chance.
                log?.Invoke($"cover: {source.Name} failed ({ex.GetType().Name}: {ex.Message})");
            }
        }

        log?.Invoke("cover: no artwork found in any source");
        return null;
    }

    /// <summary>Downloads an image and validates that it really is one.</summary>
    internal static async Task<CoverArt?> DownloadAsync(HttpClient http, string? url, string sourceName,
        Action<string>? log, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        using HttpResponseMessage response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            log?.Invoke($"cover[{sourceName}]: HTTP {(int)response.StatusCode} for {url}");
            return null;
        }

        byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        // Guard against error pages and truncated responses being embedded as "artwork".
        if (bytes.Length < 512)
        {
            log?.Invoke($"cover[{sourceName}]: response too small ({bytes.Length} bytes)");
            return null;
        }

        ImageFormat format = ImageSignature.Detect(bytes);
        if (format == ImageFormat.Unknown)
        {
            log?.Invoke($"cover[{sourceName}]: response is not a recognised image");
            return null;
        }

        return new CoverArt(bytes, format, sourceName);
    }

    internal static async Task<JsonDocument?> GetJsonAsync(HttpClient http, string url, Action<string>? log,
        string sourceName, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                log?.Invoke($"cover[{sourceName}]: HTTP {(int)response.StatusCode}");
                return null;
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            log?.Invoke($"cover[{sourceName}]: malformed JSON ({ex.Message})");
            return null;
        }
    }
}

internal static class CoverArtExtensions
{
    /// <summary>Returns a copy of <paramref name="cover"/> attributed to a different source.</summary>
    internal static CoverArt WithSource(this CoverArt cover, string source)
        => new(cover.Data, cover.Format, source, cover.Description);
}
