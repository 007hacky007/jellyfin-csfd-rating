namespace Jellyfin.Plugin.CsfdRatingOverlay.Queue;

public interface ICsfdFetchProcessor
{
    Task<FetchWorkResult> ProcessAsync(CsfdFetchRequest request, CancellationToken cancellationToken);
}
