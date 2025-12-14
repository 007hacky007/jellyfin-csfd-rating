# Codex Max Task: Jellyfin “ČSFD Rating Overlay” Plugin (Server + Web UI Overlay)

## Objective

Create a Jellyfin solution that adds **ČSFD rating** to Movies and Series and renders it as a **bottom-center overlay on every poster card** throughout Jellyfin Web.

Key behaviors:

1. Fetch ČSFD rating as **percentage (0–100)** and convert to stars:

   * `stars = percent / 10.0` (e.g., `83% → 8.3`)
   * Display string: **`8.3 ⭐️`** (one decimal, invariant culture)
2. **Throttle** ČSFD requests to avoid bans.
3. **Persist** results so each item is requested **only once** (except explicit retry/reset rules defined below).
4. Overlay appears in **every listing** (home rows, library views, collections, search results, etc.) at **bottom center** of poster.

Important note:

* Jellyfin server plugins are .NET; Web UI modifications are not officially supported. Implement overlay injection as **optional and isolated**, with clear documentation and a config toggle.

---

## Architecture (two layers)

### A) Server-side Jellyfin plugin (supported)

Responsibilities:

* Resolve Jellyfin items (Movie/Series) → ČSFD entry
* Fetch and parse rating (percentage)
* Store result in a persistent cache (positive and negative caching)
* Expose API endpoints for Web UI overlay
* Provide admin settings and actions (backfill, retry, reset)

### B) Jellyfin Web UI overlay bundle (required for the visual overlay)

Responsibilities:

* Inject badge markup into poster cards
* Batch-fetch ratings from local Jellyfin server API
* Cache per session in browser to avoid repeated API calls
* Use observers to keep scrolling performance acceptable

Delivery options:

* Preferred: bundle JS/CSS shipped with this plugin and loaded via an optional injection mechanism.
* Alternate: ship the JS/CSS as “ready-to-paste” into a known JS injection plugin, documented in README.

---

## Functional Requirements

### 1) Rating conversion & formatting

* Input: integer `percent` in `[0..100]`
* Stars: `stars = percent / 10.0`
* Format: exactly **one decimal place**, invariant culture:

  * `starsText = stars.ToString("0.0", CultureInfo.InvariantCulture)`
* Display: `displayText = $"{starsText} ⭐️"`

### 2) Request throttling (anti-ban)

Implement a single global throttled pipeline:

* Defaults:

  * **1 request every 2 seconds**
  * **1 concurrent request**
* Behavior:

  * All CSFD calls go through a single queue and limiter.
  * On `429/403/503` (or equivalent throttling/availability signals):

    * Pause queue with exponential backoff and a cooldown window.
  * Avoid burst retries; do not exceed configured rate.

### 3) Persistent cache (positive + negative caching)

Use a persistent store in plugin data directory (SQLite recommended; JSON acceptable for first iteration but must be safe under concurrency).

**Cache entry must include:**

* `itemId` (Jellyfin internal ID)
* `status` enum:

  * `Resolved` (rating known)
  * `NotFound` (no matching title on CSFD)
  * `ErrorTransient` (temporary failure; retry eligible after retryAfterUtc)
  * `ErrorPermanent` (give up; manual intervention needed)
* `fingerprint` (see “NotFound handling” below)
* Timestamps: `createdUtc`, `updatedUtc`, `attemptedUtc`
* If Resolved:

  * `csfdId`, `percent`, `stars`, `displayText`, optional `ratingCount`
  * `matchedTitle`, `matchedYear`
* If errors:

  * `lastError`, `retryAfterUtc`, `attemptCount`

### 4) Matching Jellyfin items to ČSFD

For each item (Movie/Series):

* Use Jellyfin metadata:

  * Titles (primary/original), production year, item type
  * Optional provider IDs (IMDb/TMDb) if helpful; do not assume availability
* Implement:

  * `SearchCsfdAsync(title, year?, type)` returning candidate list
  * Pick best candidate by:

    * Year match preferred (if both available)
    * Title similarity (normalized)
    * Type alignment (movie vs series)
* Once identified, store `csfdId` and never re-resolve unless retry/reset rules apply.

---

## Not Found Handling (must implement exactly)

### Goal

Avoid repeatedly querying ČSFD for titles that cannot be found, while still allowing controlled re-attempts when metadata changes or an admin explicitly requests it.

### Policy

#### A) Deterministic “NotFound” is terminal for the current metadata state

* If CSFD search yields **no acceptable match**, store a **negative cache entry**:

  * `status = NotFound`
  * `attemptedUtc`, `queryUsed`, and `fingerprint`
* Automatic behavior afterward:

  * Do **not** retry again for that item **as long as fingerprint is unchanged**
  * Overlay shows **nothing** for that item by default

#### B) Automatic re-attempt only when metadata fingerprint changes

Compute `fingerprint` from the exact fields used for matching (at minimum):

* normalized primary title
* normalized original title (if present)
* year (or empty)
* item type (movie/series)
* optionally include stable provider IDs if present

If current fingerprint differs from cached fingerprint and cached status is `NotFound`:

* Allow **one new attempt**, and update cache accordingly.

This preserves “only once” semantics **per metadata state**, and naturally supports user fixing titles/years.

#### C) Manual admin actions override

Provide admin actions:

* **Retry unmatched items**: re-enqueue all items with `status = NotFound` regardless of fingerprint.
* **Reset cache**: per-item and global; deletes entries so items can be fetched again.

#### D) Transient failures are not NotFound

If request fails due to network/timeouts, 5xx, throttling blocks, parsing issues:

* Do NOT mark `NotFound`
* Mark `ErrorTransient` with `retryAfterUtc` and exponential backoff
* Cap retries (e.g., 5 attempts). If exceeded, mark `ErrorPermanent`
* Only admin reset should retry `ErrorPermanent`

---

## Server Plugin API (for Web overlay)

Implement local endpoints (path prefix can be `/Plugins/CsfdRatingOverlay` or similar):

1. `GET /csfd/items/{itemId}`

   * Returns cached data only:

     * `Resolved` → `{ itemId, percent, stars, displayText }`
     * `NotFound` → `{ itemId, status: "NotFound" }`
     * `ErrorTransient` / `ErrorPermanent` similarly
   * Optional: if missing cache entry, return `202 Accepted` and enqueue fetch, or return `404` + enqueue. Choose one and document it.

2. `POST /csfd/items/batch`

   * Request: `{ "itemIds": ["...", "..."] }`
   * Response: map keyed by itemId with status and data
   * Must be efficient; used heavily by UI overlay.

---

## Queue & Background Jobs

### Backfill task

Provide a scheduled task / dashboard action:

* Enumerate all Movies and Series
* For each item:

  * If cache has `Resolved` → skip
  * If `NotFound` with same fingerprint → skip
  * If `NotFound` and fingerprint changed → enqueue
  * If `ErrorTransient` and now >= retryAfterUtc → enqueue
  * If `ErrorPermanent` → skip unless admin reset
  * If no cache entry → enqueue

### Request pipeline

* Use `Channel<T>` or equivalent:

  * Producer: backfill + on-demand requests
  * Consumer: single worker (or strictly controlled concurrency)
* All CSFD requests go through:

  * `SemaphoreSlim(1,1)` (single-flight)
  * delay interval enforcement
  * retry/backoff logic

---

## Web UI Overlay Requirements

### Placement & appearance

* Badge text is `displayText` exactly (e.g., `8.3 ⭐️`)
* Position: **bottom center** of poster card in every listing
* CSS:

  * parent container must be positioned (relative)
  * badge: `position:absolute; bottom:6px; left:50%; transform:translateX(-50%);`
  * include readable background (semi-opaque), padding, rounded corners

### Performance and behavior

* Use `MutationObserver` to detect new poster nodes.
* Use `IntersectionObserver` to fetch data only when posters enter viewport.
* Use **batch endpoint** to reduce server calls.
* Cache in `sessionStorage` keyed by `itemId` for the browser session.

### Failure states

* If status is `NotFound` or errors: show nothing by default (no placeholder).
* Must not spam repeated fetches.

---

## Admin Settings UI (Dashboard)

Configuration keys (suggested):

* `Enabled` (server plugin enabled)
* `OverlayInjectionEnabled` (web injection enabled)
* `RequestDelayMs` (default 2000)
* `MaxRetries` (default 5)
* `CooldownMinMinutes` (default 10; can grow by backoff)
  Buttons:
* `BackfillLibrary`
* `RetryUnmatchedItems`
* `ResetCacheAll`
* Per-item actions optional (nice-to-have)

---

## Implementation Steps (Codex must follow)

### Phase 0: Scaffold

1. Create Jellyfin plugin project (net8.0).
2. Add plugin configuration model and dashboard UI skeleton.
3. Set up logging.

### Phase 1: Cache store

1. Implement `ICsfdCacheStore` with `Get`, `GetMany`, `Upsert`, `Delete`, `ClearAll`.
2. Implement SQLite schema or robust JSON store with file locking.
3. Add `CsfdCacheEntryStatus` enum and migration strategy.

### Phase 2: Throttle + queue + retry policy

1. Implement `CsfdRateLimiter` (single-flight + min interval).
2. Implement `CsfdFetchQueue` background service.
3. Implement backoff and retry rules:

   * `ErrorTransient` with `retryAfterUtc`
   * cap retries → `ErrorPermanent`

### Phase 3: ČSFD client

1. Implement `CsfdClient`:

   * `SearchAsync(query)` returning candidates
   * `GetDetailsAsync(csfdId)` returning percent
2. Parsing must be resilient and unit-tested with fixtures.

### Phase 4: Matching + fingerprinting + NotFound policy

1. Implement `MetadataFingerprint(item)` producing stable hash/string.
2. Implement match selection logic.
3. If no match → store `NotFound` with fingerprint; enforce retry rules precisely.

### Phase 5: API endpoints

1. Implement `GET item` and `POST batch`.
2. Add auth policy: must require authenticated Jellyfin session (same as other plugin endpoints).
3. Ensure responses include `status` always.

### Phase 6: Web overlay bundle

1. Implement injection script and CSS.
2. Implement DOM badge injection for poster cards.
3. Implement batch fetching + session caching + observers.

### Phase 7: Packaging & documentation

1. Build plugin zip artifact.
2. Provide README with:

   * install steps
   * injection option(s)
   * throttle guidance
   * NotFound handling explanation
   * troubleshooting section

---

## Acceptance Criteria (Definition of Done)

1. **Overlay rendering**

   * A Movie/Series with 83% on CSFD shows **`8.3 ⭐️`** at bottom-center on its poster in all major Jellyfin Web listings.

2. **One-time fetch behavior**

   * After first resolution, the plugin does not re-request the rating for the same item unless:

     * item was `NotFound` and fingerprint changed (one re-attempt), or
     * admin triggers retry/reset.

3. **NotFound handling**

   * If title cannot be found:

     * plugin stores `NotFound`
     * does not retry automatically unless fingerprint changes
     * retry button forces re-attempt

4. **Throttling**

   * Under backfill of 1,000 items, outbound CSFD requests remain at or below configured throttle and concurrency = 1.

5. **Stability**

   * On 429/403/5xx/network issues, plugin backs off and does not spam CSFD.
   * Overlay does not degrade scrolling significantly (viewport-based fetching + batching).

---

## Output Expected from Codex

* Full source code for server plugin + web assets
* Build instructions
* Sample configuration
* A minimal test plan (unit tests for parsing and fingerprint logic; basic integration notes for overlay)

Include the NotFound behavior exactly as specified above.
