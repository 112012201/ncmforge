namespace NcmForge.Core.Tools;

/// <summary>Creates the shared HTTP client used for cover-art lookups.</summary>
public static class NcmForgeHttp
{
    /// <summary>
    /// Builds a client with a descriptive user agent and a browsing timeout.
    /// </summary>
    /// <remarks>
    /// The user agent is not cosmetic: MusicBrainz rejects requests without one, and identifying
    /// the tool keeps it a good citizen of the public APIs it depends on.
    /// </remarks>
    public static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("NcmForge/1.0 (+https://github.com/ncmforge)");
        client.DefaultRequestHeaders.Referrer = new Uri("https://music.163.com/");
        return client;
    }
}
