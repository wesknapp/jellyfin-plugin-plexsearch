# Plex Search for Jellyfin

Adds an **On Plex** section to Jellyfin's search page, showing matching movies and shows from a Plex server — including servers that are only *shared* with you. Tap a result to open it in Plex.

Targets **Jellyfin 10.11+**.

## How it works

- **Backend (C# plugin):** signs in to Plex with the PIN flow, finds your servers via plex.tv, and proxies search and poster requests. Plex tokens stay on the Jellyfin server and are never sent to browsers.
- **Frontend (`plexsearch.js`):** watches the Jellyfin search page and adds the Plex results under everything else (works alongside Jellyfin Enhanced's Seerr results). Loaded with the JavaScript Injector plugin.

## Install (manual, private repo)

1. Open the latest successful run under **Actions → Build plugin** and download the `plexsearch_x.y.z.z` artifact.
2. Unzip it into your Jellyfin config folder as `plugins/PlexSearch/` (so you have `plugins/PlexSearch/Jellyfin.Plugin.PlexSearch.dll` and `meta.json`).
3. Restart Jellyfin.

## Set up

1. **Dashboard → Plugins → Plex Search**.
2. **Sign in with Plex** and approve it in the tab that opens.
3. Pick the server, then **Use this server**.
4. Optionally tick libraries, then **Save**.
5. Install the **JavaScript Injector** plugin, add a script, and paste the snippet shown on the settings page. Refresh the web UI.

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
- [ ] Load script via File Transformation (no JavaScript Injector needed)
- [ ] Plugin repository manifest
