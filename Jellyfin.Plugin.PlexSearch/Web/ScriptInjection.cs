using System.Text;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.PlexSearch.Web;

/// <summary>
/// Registers <see cref="ScriptInjectionMiddleware"/> at the front of Jellyfin's HTTP pipeline,
/// so the search-page script loads without the JavaScript Injector plugin.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddTransient<IStartupFilter, ScriptInjectionStartupFilter>();
    }
}

/// <summary>Adds the middleware before everything else Jellyfin registers.</summary>
public class ScriptInjectionStartupFilter : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.UseMiddleware<ScriptInjectionMiddleware>();
            next(app);
        };
    }
}

/// <summary>
/// Adds a script tag for <c>PlexSearch/ClientScript</c> to the Jellyfin web app's index.html as it is served.
/// Nothing on disk is changed. The script guards against loading twice, so an old JavaScript Injector snippet is harmless.
/// </summary>
public class ScriptInjectionMiddleware
{
    private const string Marker = "data-plexsearch";

    // index.html lives at {baseUrl}/web/, so "../PlexSearch/..." resolves to {baseUrl}/PlexSearch/...
    private static readonly string Tag =
        $"<script {Marker} defer src=\"../PlexSearch/ClientScript?v={typeof(ScriptInjectionMiddleware).Assembly.GetName().Version}\"></script>";

    private readonly RequestDelegate _next;
    private readonly ILogger<ScriptInjectionMiddleware> _logger;
    private bool _loggedOnce;

    /// <summary>Initializes a new instance of the <see cref="ScriptInjectionMiddleware"/> class.</summary>
    public ScriptInjectionMiddleware(RequestDelegate next, ILogger<ScriptInjectionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <summary>Handles one request.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsWebIndexRequest(context.Request))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Get the page uncompressed and in full (not a 304), so it can be edited.
        context.Request.Headers.Remove(HeaderNames.AcceptEncoding);
        context.Request.Headers.Remove(HeaderNames.IfNoneMatch);
        context.Request.Headers.Remove(HeaderNames.IfModifiedSince);

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        buffer.Position = 0;
        var response = context.Response;
        var isPlainHtml = response.StatusCode == StatusCodes.Status200OK
            && !response.HasStarted
            && (response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) ?? false)
            && !response.Headers.ContainsKey(HeaderNames.ContentEncoding);

        if (!isPlainHtml)
        {
            await buffer.CopyToAsync(originalBody).ConfigureAwait(false);
            return;
        }

        var html = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        var bodyEnd = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (bodyEnd >= 0 && !html.Contains(Marker, StringComparison.Ordinal))
        {
            html = html.Insert(bodyEnd, Tag);
            if (!_loggedOnce)
            {
                _loggedOnce = true;
                _logger.LogInformation("Plex Search: added search script to the web app ({Path})", context.Request.Path);
            }
        }

        var bytes = Encoding.UTF8.GetBytes(html);
        response.Headers.Remove(HeaderNames.ETag);
        response.Headers.Remove(HeaderNames.LastModified);
        response.Headers.CacheControl = "no-cache";
        response.ContentLength = bytes.Length;
        await originalBody.WriteAsync(bytes).ConfigureAwait(false);
    }

    private static bool IsWebIndexRequest(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method))
        {
            return false;
        }

        var path = (request.PathBase + request.Path).Value ?? string.Empty;
        return path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase);
    }
}
