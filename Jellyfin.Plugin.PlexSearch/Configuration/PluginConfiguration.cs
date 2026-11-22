using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.PlexSearch.Configuration;

/// <summary>
/// Stored plugin settings. Tokens never leave the server: the API strips them before returning anything to clients.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the stable client identifier this plugin uses when talking to plex.tv.</summary>
    public string ClientIdentifier { get; set; } = string.Empty;

    /// <summary>Gets or sets the plex.tv account token obtained through the PIN sign-in.</summary>
    public string PlexAccountToken { get; set; } = string.Empty;

    /// <summary>Gets or sets the Plex username, for display only.</summary>
    public string PlexUsername { get; set; } = string.Empty;

    /// <summary>Gets or sets the selected server's machine identifier.</summary>
    public string ServerMachineId { get; set; } = string.Empty;

    /// <summary>Gets or sets the selected server's display name.</summary>
    public string ServerName { get; set; } = string.Empty;

    /// <summary>Gets or sets the working connection URI for the selected server.</summary>
    public string ServerUri { get; set; } = string.Empty;

    /// <summary>Gets or sets the access token for the selected server (differs from the account token on shared servers).</summary>
    public string ServerAccessToken { get; set; } = string.Empty;

    /// <summary>Gets or sets the Plex library section IDs to include. Empty means all movie and show libraries.</summary>
    public string[] LibrarySectionIds { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the maximum number of results shown.</summary>
    public int MaxResults { get; set; } = 12;

    /// <summary>Gets or sets the timeout for Plex requests, in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 4;
}
