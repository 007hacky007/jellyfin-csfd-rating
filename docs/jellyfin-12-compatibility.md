# Jellyfin 12.0 compatibility audit

## Implementation follow-up: `feat/jellyfin-12-compatibility`

The authentication and build/release changes identified below are implemented on this branch:

- Both overlay fetch paths use `Authorization: MediaBrowser Token="…"`. The scheme is accepted by [Jellyfin 10.11](https://github.com/jellyfin/jellyfin/blob/v10.11.4/Jellyfin.Server.Implementations/Security/AuthorizationContext.cs) and 12.0 with legacy authentication disabled.
- A `net10.0` build using Jellyfin `12.0.0` packages is published alongside the existing `net9.0`/10.11 and `net8.0`/10.10 builds. The newest ABI appears first in the release manifest so equal plugin versions select the appropriate artifact.
- CI installs all three SDKs, tests every backend target, and runs both JavaScript suites. Manual releases tag the workflow's checked-out commit.

Validation on 2026-09-08:

- Backend: **8 tests passed per target (24 total)**, running on matching .NET and ASP.NET runtimes (8.0.30, 9.0.19, 10.0.11).
- JavaScript: **77 assertions passed** (36 detail-toggle, 41 real-overlay), including authenticated uncached poster/detail fetches, both API client token access methods, a server base URL, and anonymous configuration. The old BoxSet fixture was corrected to include Jellyfin's actual playstate button.
- All three Release builds published. The workflow's ZIP, checksum, and manifest scripts were executed locally; archive contents, framework identities, checksums, and server-version artifact selection passed verification.
- `actionlint` and `git diff --check` passed.

Automatic injection still requires a File Transformation build compatible with the server, or manual injection. No live Jellyfin installation was upgraded, and no release was published during this work.

The remaining sections preserve the original audit findings before these changes.

## Original audit baseline

Verified on 2026-09-08 against repository commit `06690246f5d11f5dfde54a434fe5461cb0485c79` (plugin 0.0.14), Jellyfin server/web tag `v12.0`, and Jellyfin NuGet packages `12.0.0`.

**The audited release was not ready for Jellyfin 12.0 with default settings.** The overlay's authentication needed changing, a .NET 10 release target needed adding, and automatic injection depended on an appropriate File Transformation release. No plugin implementation or release configuration was changed during the initial audit.

## Required changes

### 1. Replace the legacy authentication header

`Web/overlay.js:103` and `:562` send `X-Emby-Token`. Jellyfin 12 reads that header only when `EnableLegacyAuthorization` is enabled. Its upgrade migration explicitly disables that setting, and new installations also default to false.

Consequently, the authenticated `POST /Plugins/CsfdRatingOverlay/csfd/items/batch` endpoint returns 401 under default settings. Uncached poster/detail ratings stay at their placeholder; existing browser cache can temporarily hide the failure. The client configuration endpoint is anonymous, so settings can still load.

Use `Authorization: MediaBrowser Token="<token>"` or Jellyfin Web's authenticated API client. Apply the change to both fetch paths. Do not substitute `X-MediaBrowser-Token` or `X-Emby-Authorization`, which are also legacy mechanisms.

Evidence: [12.0 authorization parser](https://github.com/jellyfin/jellyfin/blob/v12.0/Jellyfin.Server.Implementations/Security/AuthorizationContext.cs), [upgrade migration](https://github.com/jellyfin/jellyfin/blob/v12.0/Jellyfin.Server/Migrations/Routines/20260531160000_DisableLegacyAuthorization.cs).

### 2. Add a .NET 10 build and release artifact

The plugin project currently targets `net8.0;net9.0`, using Jellyfin `10.10.0` and `10.11.4`. The release workflow publishes only those two targets, with manifest ABIs `10.10.0` and `10.11.0`.

Add a `net10.0` target with Jellyfin Common/Controller/Model `12.0.0`, suitable .NET 10 dependency versions, and a corresponding ZIP and manifest entry targeting ABI `12.0.0.0`. Add .NET 10 to CI and exercise the tests against that target. Retain the older targets if continued 10.10/10.11 support is desired.

An isolated copy compiled successfully after changing only project framework/package references. All eight backend tests passed against Jellyfin 12.0. No C# implementation changes were necessary for compilation. This covers the APIs our code uses, including metadata providers, external IDs/URLs, library queries, metadata persistence, service registration, controllers, and scheduled tasks; it does not establish runtime behavior for every path.

Jellyfin explicitly requires plugins to be retargeted and rebuilt for .NET 10 in its [12.0 release notes](https://github.com/jellyfin/jellyfin/releases/tag/v12.0). An old ABI entry being visible in the catalogue is not proof of binary compatibility.

### 3. Verify the automatic injection dependency

The latest published [File Transformation release](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation/releases/tag/2.5.11.0) observed was `2.5.11.0`, with assets for Jellyfin 10.11.7–10.11.11 only. Its README describes releases as specific to individual Jellyfin versions. The documented catalogue returned an empty array during this audit, so it did not establish availability of a 12.0 package.

Its `v12` branch at commit `2dd4279bc7aedd082de0639278f9142ead229a52` targets Jellyfin 12.0/.NET 10. The [registration interface](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation/blob/2dd4279bc7aedd082de0639278f9142ead229a52/src/Jellyfin.Plugin.FileTransformation/PluginInterface.cs) still accepts `RegisterTransformation(JObject)`, and the reflection callback still converts its payload to our parameter type and accepts a string result. No registration-contract change was identified on our side.

A compatible published dependency and a startup/injection smoke test remain necessary. The README's manual script injection is an alternative once our authentication and build changes are made.

## Web layout findings

Jellyfin 12 makes Modern the default. Source inspection found the hooks used by the overlay still present:

- React and legacy cards retain `.card`, `.cardScalable`, `.cardImageContainer`, `data-id`, and `data-type`.
- Modern still routes item details to the legacy detail controller, retaining `.itemMiscInfo.itemMiscInfo-primary` and the playstate button's `data-type`.
- `window.ApiClient` remains assigned by `ServerConnections`.

Sources: [React card wrapper](https://github.com/jellyfin/jellyfin-web/blob/v12.0/src/components/cardbuilder/Card/CardWrapper.tsx), [card attributes](https://github.com/jellyfin/jellyfin-web/blob/v12.0/src/utils/items.ts), [Modern detail route](https://github.com/jellyfin/jellyfin-web/blob/v12.0/src/apps/modern/routes/legacyRoutes/user.ts), [detail markup](https://github.com/jellyfin/jellyfin-web/blob/v12.0/src/apps/legacy/controllers/itemDetails/index.html), [API client assignment](https://github.com/jellyfin/jellyfin-web/blob/v12.0/src/lib/jellyfin-apiclient/ServerConnections.js).

No desktop/mobile selector rewrite was identified. TV cards use button wrappers, which our existing `prepareCard` deliberately skips; TV poster coverage is an existing limitation rather than a verified new 12.0 regression.

## Validation and limits

- Isolated .NET 10/Jellyfin 12.0 build and backend tests: **8 passed**.
- Existing detail-toggle suite: **36 assertions passed**.
- Existing real-overlay suite: **22 assertions passed, 1 failed**. The failing BoxSet fixture puts `data-type` on the page instead of providing the playstate button our current code inspects. This reproduces on the unchanged checkout and is not evidence of a new Jellyfin 12 regression.
- An isolated jsdom check using the current overlay and 12.0-compatible card/detail markup reproduced placeholders when its mock endpoint enforced the 12.0 auth rule. Substituting the modern header in memory produced both ratings successfully. This was a simulated endpoint, not a live server test.

No live Jellyfin 12 installation was started or upgraded. Plugin loading, actual File Transformation injection, native metadata writes, and visual behavior still require an end-to-end smoke test before claiming full support.
