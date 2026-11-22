# Plex Search for Jellyfin

Adds an **On Plex** section to Jellyfin's search page, showing matching movies and shows from a Plex server — including servers that are only *shared* with you. Tap a result to open it in Plex.

Targets **Jellyfin 10.11+**.

## How it works

- **Backend (C# plugin):** signs in to Plex with the PIN flow, finds your servers via plex.tv, and proxies search and poster requests. Plex tokens stay on the Jellyfin server and are never sent to browsers.
- **Frontend (`plexsearch.js`):** watches the Jellyfin search page and adds the Plex results under everything else (works alongside Jellyfin Enhanced's Seerr results). The plugin adds it to the Jellyfin web app automatically (no JavaScript Injector needed).

## Install

1. In Jellyfin, go to **Dashboard → Plugins → Repositories** (the **Manage Repositories** button in 10.11) and add:
   - Name: `Plex Search`
   - URL: `https://raw.githubusercontent.com/wesknapp/jellyfin-plugin-plexsearch/main/manifest.json`
2. Open the **Catalog**, find **Plex Search** and install it.
3. Restart Jellyfin.

New versions then show up as plugin updates.

**Manual install:** download `plexsearch_x.y.z.z.zip` from the latest [release](https://github.com/wesknapp/jellyfin-plugin-plexsearch/releases), unzip it into your Jellyfin config folder as `plugins/PlexSearch/`, then restart Jellyfin.

## Releasing a new version

Bump `AssemblyVersion` and `FileVersion` in the `.csproj`, commit, then push a matching tag, for example `v0.1.1.0`. The **Build plugin** action builds the zip, publishes a GitHub release and adds the version to `manifest.json`.

## Set up

1. **Dashboard → Plugins → Plex Search**.
2. **Sign in with Plex** and approve it in the tab that opens.
3. Pick the server, then **Use this server**.
4. Optionally tick libraries, then **Save**.
5. Refresh the Jellyfin web page (Ctrl+F5). If the On Plex section never appears (for example a reverse proxy caches the page), use the JavaScript Injector snippet shown on the settings page instead.

## API (for testing)

All under `/PlexSearch`, using your Jellyfin login:

| Endpoint | Who | Purpose |
|---|---|---|
| `GET Search?query=dune` | any user | Search results as JSON |
| `GET Image?path=/library/metadata/...` | any user | Resized poster |
| `GET Status`, `Servers`, `Libraries` | admin | Settings page data |
| `GET ClientScript` | anyone | The search-page script |

## Roadmap

- [x] MVP: sign-in, server/library picker, search section with posters, open in Plex
- [ ] "Your Plex" badge on Jellyfin detail pages
- [x] Load the search script automatically (no JavaScript Injector needed)
- [x] Plugin repository manifest
