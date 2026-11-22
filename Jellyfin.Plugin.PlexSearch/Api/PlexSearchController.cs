using System.Reflection;
using Jellyfin.Plugin.PlexSearch.Plex;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PlexSearch.Api;

/// <summary>
/// HTTP API used by the settings page and the search-page script.
/// Admin-only endpoints manage sign-in and server choice; Search/Image are for any signed-in Jellyfin user.
/// </summary>
[ApiController]
[Route("PlexSearch")]
public class PlexSearchController : ControllerBase
{
    private const string AdminPolicy = "RequiresElevation";

    private readonly PlexClient _plex;
    private readonly ILogger<PlexSearchController> _logger;

    public PlexSearchController(IHttpClientFactory httpClientFactory, ILogger<PlexSearchController> logger)
    {
        _plex = new PlexClient(httpClientFactory);
        _logger = logger;
    }

    private static Configuration.PluginConfiguration Config => Plugin.Instance!.Configuration;

    private static bool ServerConfigured =>
        !string.IsNullOrEmpty(Config.ServerUri) && !string.IsNullOrEmpty(Config.ServerAccessToken);

    // ---------- Status / settings (admin) ----------

    [HttpGet("Status")]
    [Authorize(Policy = AdminPolicy)]
    public ActionResult<object> GetStatus()
    {
        var c = Config;
        return new
        {
            signedIn = !string.IsNullOrEmpty(c.PlexAccountToken),
            username = c.PlexUsername,
            serverMachineId = c.ServerMachineId,
            serverName = c.ServerName,
            serverConnected = ServerConfigured,
            librarySectionIds = c.LibrarySectionIds,
            maxResults = c.MaxResults,
            timeoutSeconds = c.TimeoutSeconds
        };
    }

    public class SettingsBody
    {
        public string[]? LibrarySectionIds { get; set; }

        public int? MaxResults { get; set; }

        public int? TimeoutSeconds { get; set; }
    }

    [HttpPost("Settings")]
    [Authorize(Policy = AdminPolicy)]
    public ActionResult SaveSettings([FromBody] SettingsBody body)
    {
        var c = Config;
        if (body.LibrarySectionIds != null)
        {
            c.LibrarySectionIds = body.LibrarySectionIds;
        }

        if (body.MaxResults.HasValue)
        {
            c.MaxResults = Math.Clamp(body.MaxResults.Value, 1, 50);
        }

        if (body.TimeoutSeconds.HasValue)
        {
            c.TimeoutSeconds = Math.Clamp(body.TimeoutSeconds.Value, 1, 30);
        }

        Plugin.Instance!.SaveConfiguration();
        return NoContent();
    }

    // ---------- Sign-in (admin) ----------

    [HttpPost("Auth/Pin")]
    [Authorize(Policy = AdminPolicy)]
    public async Task<ActionResult<PlexPin>> CreatePin(CancellationToken ct)
    {
        try
        {
            return await _plex.CreatePinAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Plex PIN request failed");
            return StatusCode(StatusCodes.Status502BadGateway, "Couldn't reach plex.tv.");
        }
    }

    [HttpGet("Auth/Pin/{id}")]
    [Authorize(Policy = AdminPolicy)]
    public async Task<ActionResult<object>> CheckPin([FromRoute] long id, CancellationToken ct)
    {
        try
        {
            var done = await _plex.CheckPinAsync(id, ct).ConfigureAwait(false);
            return new { signedIn = done, username = Config.PlexUsername };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Plex PIN check failed");
            return StatusCode(StatusCodes.Status502BadGateway, "Couldn't reach plex.tv.");
        }
    }

    [HttpPost("Auth/SignOut")]
    [Authorize(Policy = AdminPolicy)]
    public ActionResult SignOutOfPlex()
    {
        PlexClient.SignOut();
        return NoContent();
    }

    // ---------- Servers & libraries (admin) ----------

    [HttpGet("Servers")]
    [Authorize(Policy = AdminPolicy)]
    public async Task<ActionResult<List<PlexServerInfo>>> GetServers(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(Config.PlexAccountToken))
        {
            return BadRequest("Sign in to Plex first.");
        }

        try
        {
            return await _plex.ListServersAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Listing Plex servers failed");
            return StatusCode(StatusCodes.Status502BadGateway, "Couldn't list servers from plex.tv.");
        }
    }

    [HttpPost("Servers/{machineId}/Select")]
    [Authorize(Policy = AdminPolicy)]
    public async Task<ActionResult> SelectServer([FromRoute] string machineId, CancellationToken ct)
    {
        try
        {
            var error = await _plex.SelectServerAsync(machineId, ct).ConfigureAwait(false);
            return error == null ? NoContent() : StatusCode(StatusCodes.Status502BadGateway, error);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Selecting Plex server failed");
            return StatusCode(StatusCodes.Status502BadGateway, "Couldn't reach plex.tv.");
        }
    }

    [HttpGet("Libraries")]
    [Authorize(Policy = AdminPolicy)]
    public async Task<ActionResult<List<PlexLibraryInfo>>> GetLibraries(CancellationToken ct)
    {
        if (!ServerConfigured)
        {
            return BadRequest("Pick a Plex server first.");
        }

        try
        {
            return await _plex.ListLibrariesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Listing Plex libraries failed");
            return StatusCode(StatusCodes.Status502BadGateway, "Plex server didn't respond.");
        }
    }

    // ---------- Search & images (any signed-in user) ----------

    [HttpGet("Search")]
    [Authorize]
    public async Task<ActionResult<object>> Search([FromQuery] string query, CancellationToken ct)
    {
        if (!ServerConfigured)
        {
            return new { configured = false, serverName = string.Empty, results = Array.Empty<PlexSearchResult>() };
        }

        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
        {
            return new { configured = true, serverName = Config.ServerName, results = Array.Empty<PlexSearchResult>() };
        }

        try
        {
            var results = await _plex.SearchAsync(query.Trim(), ct).ConfigureAwait(false);
            return new { configured = true, serverName = Config.ServerName, results };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            _logger.LogInformation("Plex search failed: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status502BadGateway, "Plex unavailable");
        }
    }

    [HttpGet("Image")]
    [Authorize]
    public async Task<ActionResult> Image([FromQuery] string path, [FromQuery] int width = 200, [FromQuery] int height = 300, CancellationToken ct = default)
    {
        // Only allow Plex metadata artwork paths, so this can't be used as a general proxy.
        if (!ServerConfigured || string.IsNullOrEmpty(path) || !path.StartsWith("/library/metadata/", StringComparison.Ordinal))
        {
            return NotFound();
        }

        try
        {
            var response = await _plex.GetImageAsync(path, Math.Clamp(width, 50, 800), Math.Clamp(height, 50, 1200), ct).ConfigureAwait(false);
            HttpContext.Response.RegisterForDispose(response);
            if (!response.IsSuccessStatusCode)
            {
                return NotFound();
            }

            var contentType = response.Content.Headers.ContentType?.ToString() ?? "image/jpeg";
            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            Response.Headers.CacheControl = "private, max-age=86400";
            return File(stream, contentType);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return NotFound();
        }
    }

    // ---------- Client script ----------

    /// <summary>Serves the search-page script. Load it with the JavaScript Injector plugin.</summary>
    [HttpGet("ClientScript")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    public ActionResult ClientScript()
    {
        var asm = Assembly.GetExecutingAssembly();
        var stream = asm.GetManifestResourceStream("Jellyfin.Plugin.PlexSearch.Web.plexsearch.js");
        if (stream == null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "no-cache";
        return File(stream, "application/javascript");
    }
}
