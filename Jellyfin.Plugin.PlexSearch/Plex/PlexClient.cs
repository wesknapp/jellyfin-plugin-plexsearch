using System.Net.Http.Headers;
using System.Text.Json;
using Jellyfin.Plugin.PlexSearch.Configuration;

namespace Jellyfin.Plugin.PlexSearch.Plex;

/// <summary>Plex server info returned to the settings page (no tokens).</summary>
public record PlexServerInfo(string MachineId, string Name, bool Owned, string SourceTitle);

/// <summary>Plex library info returned to the settings page.</summary>
public record PlexLibraryInfo(string Id, string Title, string Type);

/// <summary>One search result returned to the browser.</summary>
public record PlexSearchResult(
    string RatingKey,
    string Title,
    int? Year,
    string Type,
    string LibraryTitle,
    string? ImagePath,
    string WebUrl,
    string AndroidAppUrl,
    string? DiscoverUrl);

/// <summary>Result of a PIN sign-in request.</summary>
public record PlexPin(long Id, string Code, string AuthUrl);

/// <summary>
/// Thin wrapper around the plex.tv and Plex Media Server HTTP APIs.
/// </summary>
public class PlexClient
{
    private const string Product = "Jellyfin Plex Search";
    private const string PlexTv = "https://plex.tv";
    private const string PlexClients = "https://clients.plex.tv";

    private readonly IHttpClientFactory _httpClientFactory;

    public PlexClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    /// <summary>Returns the plugin's plex.tv client identifier, creating and saving one on first use.</summary>
    public static string EnsureClientIdentifier()
    {
        var config = Config;
        if (string.IsNullOrWhiteSpace(config.ClientIdentifier))
        {
            config.ClientIdentifier = "jellyfin-plexsearch-" + Guid.NewGuid().ToString("N");
            Plugin.Instance!.SaveConfiguration();
        }

        return config.ClientIdentifier;
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string url, string? token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("X-Plex-Product", Product);
        request.Headers.Add("X-Plex-Client-Identifier", EnsureClientIdentifier());
        request.Headers.Add("X-Plex-Version", "0.1.0");
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Add("X-Plex-Token", token);
        }

        return request;
    }

    private HttpClient NewClient(int? timeoutSeconds = null)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(timeoutSeconds ?? Math.Max(1, Config.TimeoutSeconds));
        return client;
    }

    private async Task<JsonDocument> SendJsonAsync(HttpRequestMessage request, CancellationToken ct, int? timeoutSeconds = null)
    {
        using var client = NewClient(timeoutSeconds);
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    // ---------- Sign-in (PIN flow) ----------

    public async Task<PlexPin> CreatePinAsync(CancellationToken ct)
    {
        using var request = NewRequest(HttpMethod.Post, PlexTv + "/api/v2/pins?strong=true", null);
        using var doc = await SendJsonAsync(request, ct, 15).ConfigureAwait(false);
        var root = doc.RootElement;
        var id = root.GetProperty("id").GetInt64();
        var code = root.GetProperty("code").GetString() ?? string.Empty;
        var authUrl = "https://app.plex.tv/auth#?clientID=" + Uri.EscapeDataString(EnsureClientIdentifier())
            + "&code=" + Uri.EscapeDataString(code)
            + "&context%5Bdevice%5D%5Bproduct%5D=" + Uri.EscapeDataString(Product);
        return new PlexPin(id, code, authUrl);
    }

    /// <summary>Checks a PIN; when approved, stores the account token. Returns true once signed in.</summary>
    public async Task<bool> CheckPinAsync(long pinId, CancellationToken ct)
    {
        using var request = NewRequest(HttpMethod.Get, PlexTv + "/api/v2/pins/" + pinId, null);
        using var doc = await SendJsonAsync(request, ct, 15).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("authToken", out var tokenEl)
            || tokenEl.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(tokenEl.GetString()))
        {
            return false;
        }

        var config = Config;
        config.PlexAccountToken = tokenEl.GetString()!;
        config.PlexUsername = await GetUsernameAsync(config.PlexAccountToken, ct).ConfigureAwait(false);
        Plugin.Instance!.SaveConfiguration();
        return true;
    }

    private async Task<string> GetUsernameAsync(string token, CancellationToken ct)
    {
        try
        {
            using var request = NewRequest(HttpMethod.Get, PlexTv + "/api/v2/user", token);
            using var doc = await SendJsonAsync(request, ct, 15).ConfigureAwait(false);
            return doc.RootElement.TryGetProperty("username", out var u) ? u.GetString() ?? string.Empty : string.Empty;
        }
        catch (HttpRequestException)
        {
            return string.Empty;
        }
    }

    public static void SignOut()
    {
        var config = Config;
        config.PlexAccountToken = string.Empty;
        config.PlexUsername = string.Empty;
        config.ServerMachineId = string.Empty;
        config.ServerName = string.Empty;
        config.ServerUri = string.Empty;
        config.ServerAccessToken = string.Empty;
        config.LibrarySectionIds = Array.Empty<string>();
        Plugin.Instance!.SaveConfiguration();
    }

    // ---------- Servers ----------

    private async Task<List<JsonElement>> GetServerResourcesAsync(CancellationToken ct)
    {
        using var request = NewRequest(
            HttpMethod.Get,
            PlexClients + "/api/v2/resources?includeHttps=1&includeRelay=1",
            Config.PlexAccountToken);
        using var doc = await SendJsonAsync(request, ct, 15).ConfigureAwait(false);
        var list = new List<JsonElement>();
        foreach (var res in doc.RootElement.EnumerateArray())
        {
            var provides = res.TryGetProperty("provides", out var p) ? p.GetString() ?? string.Empty : string.Empty;
            if (provides.Contains("server", StringComparison.OrdinalIgnoreCase))
            {
                list.Add(res.Clone());
            }
        }

        return list;
    }

    public async Task<List<PlexServerInfo>> ListServersAsync(CancellationToken ct)
    {
        var servers = await GetServerResourcesAsync(ct).ConfigureAwait(false);
        return servers.Select(s => new PlexServerInfo(
            Str(s, "clientIdentifier"),
            Str(s, "name"),
            s.TryGetProperty("owned", out var o) && o.ValueKind == JsonValueKind.True,
            Str(s, "sourceTitle"))).ToList();
    }

    /// <summary>Selects a server, finds a working connection to it, and saves it. Returns an error message or null.</summary>
    public async Task<string?> SelectServerAsync(string machineId, CancellationToken ct)
    {
        var servers = await GetServerResourcesAsync(ct).ConfigureAwait(false);
        var server = servers.FirstOrDefault(s => Str(s, "clientIdentifier") == machineId);
        if (server.ValueKind == JsonValueKind.Undefined)
        {
            return "Server not found on your Plex account.";
        }

        var accessToken = Str(server, "accessToken");
        if (string.IsNullOrEmpty(accessToken))
        {
            accessToken = Config.PlexAccountToken;
        }

        // Try direct remote connections first, then local, then Plex relay (slow, bandwidth-capped).
        var connections = new List<(string Uri, int Rank)>();
        if (server.TryGetProperty("connections", out var conns) && conns.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in conns.EnumerateArray())
            {
                var uri = Str(c, "uri");
                if (string.IsNullOrEmpty(uri))
                {
                    continue;
                }

                var relay = c.TryGetProperty("relay", out var r) && r.ValueKind == JsonValueKind.True;
                var local = c.TryGetProperty("local", out var l) && l.ValueKind == JsonValueKind.True;
                connections.Add((uri.TrimEnd('/'), relay ? 2 : local ? 1 : 0));
            }
        }

        foreach (var (uri, _) in connections.OrderBy(c => c.Rank))
        {
            if (await TestConnectionAsync(uri, accessToken, ct).ConfigureAwait(false))
            {
                var config = Config;
                config.ServerMachineId = machineId;
                config.ServerName = Str(server, "name");
                config.ServerUri = uri;
                config.ServerAccessToken = accessToken;
                config.LibrarySectionIds = Array.Empty<string>();
                Plugin.Instance!.SaveConfiguration();
                return null;
            }
        }

        return "Couldn't reach that Plex server on any of its addresses.";
    }

    private async Task<bool> TestConnectionAsync(string uri, string token, CancellationToken ct)
    {
        try
        {
            using var request = NewRequest(HttpMethod.Get, uri + "/identity", token);
            using var doc = await SendJsonAsync(request, ct, 5).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return false;
        }
    }

    public async Task<List<PlexLibraryInfo>> ListLibrariesAsync(CancellationToken ct)
    {
        var config = Config;
        using var request = NewRequest(HttpMethod.Get, config.ServerUri + "/library/sections", config.ServerAccessToken);
        using var doc = await SendJsonAsync(request, ct).ConfigureAwait(false);
        var list = new List<PlexLibraryInfo>();
        if (doc.RootElement.TryGetProperty("MediaContainer", out var mc)
            && mc.TryGetProperty("Directory", out var dirs)
            && dirs.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in dirs.EnumerateArray())
            {
                var type = Str(d, "type");
                if (type is "movie" or "show")
                {
                    list.Add(new PlexLibraryInfo(Str(d, "key"), Str(d, "title"), type));
                }
            }
        }

        return list;
    }

    // ---------- Search & images ----------

    public async Task<List<PlexSearchResult>> SearchAsync(string query, CancellationToken ct)
    {
        var config = Config;
        var max = Math.Clamp(config.MaxResults, 1, 50);
        var url = config.ServerUri + "/hubs/search?query=" + Uri.EscapeDataString(query) + "&limit=" + (max * 2);
        using var request = NewRequest(HttpMethod.Get, url, config.ServerAccessToken);
        using var doc = await SendJsonAsync(request, ct).ConfigureAwait(false);

        var allowed = new HashSet<string>(config.LibrarySectionIds ?? Array.Empty<string>());
        var results = new List<PlexSearchResult>();
        var seen = new HashSet<string>();

        if (!doc.RootElement.TryGetProperty("MediaContainer", out var mc)
            || !mc.TryGetProperty("Hub", out var hubs)
            || hubs.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var hub in hubs.EnumerateArray())
        {
            if (!hub.TryGetProperty("Metadata", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var m in items.EnumerateArray())
            {
                var type = Str(m, "type");
                if (type is not ("movie" or "show"))
                {
                    continue;
                }

                var sectionId = m.TryGetProperty("librarySectionID", out var sid)
                    ? (sid.ValueKind == JsonValueKind.Number ? sid.GetRawText() : sid.GetString() ?? string.Empty)
                    : string.Empty;
                if (allowed.Count > 0 && !allowed.Contains(sectionId))
                {
                    continue;
                }

                var ratingKey = Str(m, "ratingKey");
                if (string.IsNullOrEmpty(ratingKey) || !seen.Add(ratingKey))
                {
                    continue;
                }

                int? year = m.TryGetProperty("year", out var y) && y.ValueKind == JsonValueKind.Number ? y.GetInt32() : null;
                var thumb = Str(m, "thumb");
                var metadataKey = Uri.EscapeDataString("/library/metadata/" + ratingKey);
                var webUrl = "https://app.plex.tv/desktop/#!/server/" + config.ServerMachineId + "/details?key=" + metadataKey;

                // The Plex Android app opens this straight to the title on this server.
                var androidAppUrl = "plex://libraries/" + config.ServerMachineId + "/unmatched/"
                    + (type == "show" ? "tv/" : string.Empty) + metadataKey;

                // The Plex iOS app ignores plex:// links to a title, but opens watch.plex.tv pages for it.
                var slug = Str(m, "slug");
                var discoverUrl = string.IsNullOrEmpty(slug) ? null : "https://watch.plex.tv/" + type + "/" + Uri.EscapeDataString(slug);

                results.Add(new PlexSearchResult(
                    ratingKey,
                    DisplayTitle(Str(m, "title"), Str(m, "originalTitle")),
                    year,
                    type,
                    Str(m, "librarySectionTitle"),
                    string.IsNullOrEmpty(thumb) ? null : thumb,
                    webUrl,
                    androidAppUrl,
                    discoverUrl));

                if (results.Count >= max)
                {
                    return results;
                }
            }
        }

        return results;
    }

    /// <summary>Fetches a resized poster from Plex. Caller must dispose the response.</summary>
    public async Task<HttpResponseMessage> GetImageAsync(string path, int width, int height, CancellationToken ct)
    {
        var config = Config;
        var url = config.ServerUri + "/photo/:/transcode?width=" + width + "&height=" + height
            + "&minSize=1&upscale=1&url=" + Uri.EscapeDataString(path);
        var request = NewRequest(HttpMethod.Get, url, config.ServerAccessToken);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
        var client = NewClient(10);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Plex keeps a short title and an original title, e.g. "Dune" and "Dune: Part One".
    /// Use the original title when it is the fuller form of the same name; a foreign-language
    /// original title (e.g. "Le Fabuleux Destin d'Amélie Poulain") is left out.
    /// </summary>
    private static string DisplayTitle(string title, string originalTitle)
    {
        return originalTitle.Length > title.Length && originalTitle.StartsWith(title, StringComparison.OrdinalIgnoreCase)
            ? originalTitle
            : title;
    }

    private static string Str(JsonElement el, string name)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v))
        {
            return v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() ?? string.Empty,
                JsonValueKind.Number => v.GetRawText(),
                _ => string.Empty
            };
        }

        return string.Empty;
    }
}
