# Jellyfin ČSFD Rating Overlay plugin

Adds ČSFD ratings to Jellyfin movies and series. It fetches ratings from ČSFD, caches them locally, and displays them as overlays on your media cards and detail pages.

![Screenshot](https://raw.githubusercontent.com/007hacky007/jellyfin-csfd-rating/master/docs/Screenshot%202025-12-14%20at%2022.19.56.png)

## Installation

1. Open your Jellyfin Dashboard.
2. Navigate to **Plugins** -> **Repositories**.
3. Add a new repository:
   - **Name:** ČSFD Rating Overlay
   - **Repository URL:** `https://github.com/007hacky007/jellyfin-csfd-rating/releases/latest/download/manifest.json`
4. Install **ČSFD Rating Overlay** plugin.
5. Install **File Transformation** plugin.
   - This plugin is required to automatically inject the overlay script.
   - See [jellyfin-plugin-file-transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation) for installation details.
6. Restart Jellyfin.
7. Profit!

## Manual Overlay Injection

If you prefer not to use the **File Transformation** plugin, you can manually inject the overlay script into your Jellyfin web interface.

1. Locate your Jellyfin web `index.html` file (e.g., `/usr/share/jellyfin/web/index.html` on Linux).
2. Add the following line before the closing `</head>` tag:
   ```html
   <script src="/Plugins/CsfdRatingOverlay/web/overlay.js"></script>
   ```
3. Refresh your browser cache.
