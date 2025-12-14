namespace Jellyfin.Plugin.CsfdRatingOverlay.Cache;

public interface ICsfdCacheStore
{
    Task<CsfdCacheEntry?> GetAsync(string itemId, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, CsfdCacheEntry>> GetManyAsync(IEnumerable<string> itemIds, CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<CsfdCacheEntry>> GetAllAsync(CancellationToken cancellationToken = default);

    Task UpsertAsync(CsfdCacheEntry entry, CancellationToken cancellationToken = default);

    Task DeleteAsync(string itemId, CancellationToken cancellationToken = default);

    Task ClearAllAsync(CancellationToken cancellationToken = default);
}
