using System.Globalization;
using Jellyfin.Plugin.CsfdRatingOverlay.Cache;
using Jellyfin.Plugin.CsfdRatingOverlay.Client;
using Jellyfin.Plugin.CsfdRatingOverlay.Matching;
using Jellyfin.Plugin.CsfdRatingOverlay.Models;
using Jellyfin.Plugin.CsfdRatingOverlay.Queue;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CsfdRatingOverlay.Services;

public class CsfdFetchProcessor : ICsfdFetchProcessor
{
    private readonly ILibraryManager _libraryManager;
    private readonly ICsfdCacheStore _cacheStore;
    private readonly CsfdClient _csfdClient;
    private readonly CsfdRateLimiter _rateLimiter;
    private readonly ILogger<CsfdFetchProcessor> _logger;

    public CsfdFetchProcessor(
        ILibraryManager libraryManager,
        ICsfdCacheStore cacheStore,
        CsfdClient csfdClient,
        CsfdRateLimiter rateLimiter,
        ILogger<CsfdFetchProcessor> logger)
    {
        _libraryManager = libraryManager;
        _cacheStore = cacheStore;
        _csfdClient = csfdClient;
        _rateLimiter = rateLimiter;
        _logger = logger;
    }

    public async Task<FetchWorkResult> ProcessAsync(CsfdFetchRequest request, CancellationToken cancellationToken)
    {
        var config = CsfdRatingOverlayPlugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
        if (!config.Enabled)
        {
            return FetchWorkResult.Success;
        }

        var item = GetItem(request.ItemId);
        if (item == null)
        {
            _logger.LogWarning("Item {ItemId} not found in library; dropping request", request.ItemId);
            return FetchWorkResult.Success;
        }

        var fingerprint = MetadataFingerprint.Compute(item);
        var cacheEntry = await _cacheStore.GetAsync(request.ItemId, cancellationToken).ConfigureAwait(false);
        if (!ShouldAttempt(cacheEntry, fingerprint, request.Force))
        {
            return FetchWorkResult.Success;
        }

        var updatedEntry = cacheEntry ?? new CsfdCacheEntry
        {
            ItemId = request.ItemId,
            CreatedUtc = DateTimeOffset.UtcNow,
            Fingerprint = fingerprint
        };

        updatedEntry.AttemptCount = (cacheEntry?.AttemptCount ?? 0) + 1;
        updatedEntry.AttemptedUtc = DateTimeOffset.UtcNow;
        updatedEntry.Fingerprint = fingerprint;

        var queryTitle = !string.IsNullOrWhiteSpace(item.OriginalTitle) ? item.OriginalTitle! : item.Name ?? string.Empty;
        CsfdClientResult<IReadOnlyList<CsfdCandidate>> searchResult;
        await using (await _rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false))
        {
            searchResult = await _csfdClient.SearchAsync(queryTitle, cancellationToken).ConfigureAwait(false);
        }
        if (searchResult.Throttled)
        {
            _logger.LogWarning("CSFD search throttled for {Item}", queryTitle);
            _rateLimiter.RegisterThrottleSignal(searchResult.RetryAfter);
            return FetchWorkResult.Throttled(searchResult.RetryAfter, searchResult.Error);
        }

        if (!searchResult.Success || searchResult.Payload == null)
        {
            await MarkTransientAsync(updatedEntry, config, searchResult.Error ?? "Search failed", cancellationToken).ConfigureAwait(false);
            return FetchWorkResult.Transient(searchResult.Error);
        }

        var isSeries = item is Series;
        var candidate = CandidateSelector.PickBest(item.Name ?? queryTitle, item.OriginalTitle, item.ProductionYear, isSeries, searchResult.Payload);
        if (candidate == null)
        {
            await MarkNotFoundAsync(updatedEntry, queryTitle, cancellationToken).ConfigureAwait(false);
            return FetchWorkResult.Success;
        }

        CsfdClientResult<int> ratingResult;
        await using (await _rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false))
        {
            ratingResult = await _csfdClient.GetRatingPercentAsync(candidate.CsfdId, cancellationToken).ConfigureAwait(false);
        }
        if (ratingResult.Throttled)
        {
            _logger.LogWarning("CSFD details throttled for {Candidate}", candidate.CsfdId);
            _rateLimiter.RegisterThrottleSignal(ratingResult.RetryAfter);
            return FetchWorkResult.Throttled(ratingResult.RetryAfter, ratingResult.Error);
        }

        if (!ratingResult.Success)
        {
            await MarkTransientAsync(updatedEntry, config, ratingResult.Error ?? "Rating fetch failed", cancellationToken).ConfigureAwait(false);
            return FetchWorkResult.Transient(ratingResult.Error);
        }

        var percent = ratingResult.Payload;
        var stars = percent / 10.0;
        var display = stars.ToString("0.0", CultureInfo.InvariantCulture) + " ⭐️";

        updatedEntry.Status = CsfdCacheEntryStatus.Resolved;
        updatedEntry.CsfdId = candidate.CsfdId;
        updatedEntry.Percent = percent;
        updatedEntry.Stars = stars;
        updatedEntry.DisplayText = display;
        updatedEntry.MatchedTitle = candidate.Title;
        updatedEntry.MatchedYear = candidate.Year;
        updatedEntry.RatingCount = null;
        updatedEntry.LastError = null;
        updatedEntry.RetryAfterUtc = null;

        await _cacheStore.UpsertAsync(updatedEntry, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Cached CSFD rating for {ItemId} as {Display}", request.ItemId, display);
        return FetchWorkResult.Success;
    }

    private BaseItem? GetItem(string itemId)
    {
        return Guid.TryParse(itemId, out var guid) ? _libraryManager.GetItemById(guid) : null;
    }

    private bool ShouldAttempt(CsfdCacheEntry? entry, string fingerprint, bool force)
    {
        if (force)
        {
            return true;
        }

        if (entry == null)
        {
            return true;
        }

        return entry.Status switch
        {
            CsfdCacheEntryStatus.Resolved => false,
            CsfdCacheEntryStatus.NotFound => !string.Equals(entry.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase),
            CsfdCacheEntryStatus.ErrorPermanent => false,
            CsfdCacheEntryStatus.ErrorTransient => !entry.RetryAfterUtc.HasValue || entry.RetryAfterUtc.Value <= DateTimeOffset.UtcNow,
            _ => true
        };
    }

    private async Task MarkNotFoundAsync(CsfdCacheEntry entry, string query, CancellationToken cancellationToken)
    {
        entry.Status = CsfdCacheEntryStatus.NotFound;
        entry.QueryUsed = query;
        entry.LastError = "Not found";
        entry.RetryAfterUtc = null;
        await _cacheStore.UpsertAsync(entry, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("CSFD not found for {ItemId}; cached negative result", entry.ItemId);
    }

    private async Task MarkTransientAsync(CsfdCacheEntry entry, Configuration.PluginConfiguration config, string error, CancellationToken cancellationToken)
    {
        var backoffMinutes = Math.Min(120, config.CooldownMinMinutes * Math.Pow(2, entry.AttemptCount - 1));
        var retryAfter = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(backoffMinutes);

        entry.Status = entry.AttemptCount >= config.MaxRetries ? CsfdCacheEntryStatus.ErrorPermanent : CsfdCacheEntryStatus.ErrorTransient;
        entry.RetryAfterUtc = entry.Status == CsfdCacheEntryStatus.ErrorPermanent ? null : retryAfter;
        entry.LastError = error;

        await _cacheStore.UpsertAsync(entry, cancellationToken).ConfigureAwait(false);
        _logger.LogWarning("CSFD fetch transient error for {ItemId}: {Message}", entry.ItemId, error);
    }
}
