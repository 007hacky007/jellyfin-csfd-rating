# Jellyfin ČSFD Rating Overlay plugin

Adds ČSFD ratings to Jellyfin movies and series, caches results, throttles outbound calls, and exposes a lightweight web overlay bundle to render `8.3 ⭐️` badges on poster cards.

## Features
- Persistent cache with Resolved/NotFound/ErrorTransient/ErrorPermanent statuses and metadata fingerprinting.
- Global throttle: single-flight queue + per-request delay with backoff on 429/403/503.
- NotFound policy: only re-attempt when metadata fingerprint changes; admin override to retry unmatched or reset cache.
- API endpoints for single and batch lookup.
- Optional web overlay bundle with viewport-aware fetching, sessionStorage caching, and batch calls.

## Build
1. Install .NET SDK 8.0 (`dotnet` is currently missing on this machine; install via https://dotnet.microsoft.com/download). 
2. From the repo root: `dotnet restore` then `dotnet build -c Release`.
3. The plugin DLL/zip can be taken from `src/Jellyfin.Plugin.CsfdRatingOverlay/bin/Release/net8.0/`.

## Configuration
- Dashboard → Plugins → ČSFD Rating Overlay.
- Keys: Enabled, OverlayInjectionEnabled, RequestDelayMs (default 2000), MaxRetries (default 5), CooldownMinMinutes (default 10).
- Buttons: Backfill library, Retry unmatched items, Reset cache.
- Config changes apply on next plugin restart for rate limit settings.

## API
- `GET /Plugins/CsfdRatingOverlay/csfd/items/{itemId}` → returns cached status or `202 Accepted` + enqueues fetch when missing.
- `POST /Plugins/CsfdRatingOverlay/csfd/items/batch` with `{ "itemIds": ["..."] }` → map of itemId → status/data; missing entries are enqueued.
- Admin actions (POST): `/csfd/actions/backfill`, `/csfd/actions/retry-notfound`, `/csfd/actions/reset-cache`.
- Overlay bundle: `GET /Plugins/CsfdRatingOverlay/web/overlay.js` (honors `OverlayInjectionEnabled`).

## Overlay injection
- The plugin now auto-injects the overlay script by patching Jellyfin `index.html` on startup when `OverlayInjectionEnabled` is true (default). A backup `index.html.bak-csfd` is written on first patch.
- If you prefer manual control, disable `OverlayInjectionEnabled` and inject manually:
  ```html
  <script src="/Plugins/CsfdRatingOverlay/web/overlay.js"></script>
  ```
- The bundle watches poster cards (`data-id`/`data-itemid`), uses IntersectionObserver + MutationObserver, batch fetches ratings, caches per session, and injects a bottom-center badge.

## Test plan (minimal)
- Unit: MetadataFingerprint.Compute stable output for same data; CandidateSelector picks best year match; percent→stars formatting produces one decimal + star.
- Integration (manual):
  - Start plugin, trigger Backfill library; verify throttle respects 1 request/2s (logs).
  - Confirm NotFound cached and not retried until title/year change; change metadata and rerun backfill to see one retry.
  - Simulate transient errors (network) and verify retryAfter/backoff then ErrorPermanent after MaxRetries.
  - Load overlay.js via injection; scroll library and verify badges display `8.3 ⭐️` on resolved items and nothing on NotFound/error.

## Notes
- External ČSFD HTML layout may change; CsfdClient uses regex-based parsing and may need adjustments if parsing fails.
- Backoff on throttling doubles up to 60 minutes; cooldown minimum is configurable.
