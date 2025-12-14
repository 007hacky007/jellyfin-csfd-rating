using Jellyfin.Plugin.CsfdRatingOverlay.Services;
using MediaBrowser.Model.Tasks;
using System.Composition;

namespace Jellyfin.Plugin.CsfdRatingOverlay.Tasks;

[Export(typeof(IScheduledTask))]
[ExportMetadata("Category", "Metadata")]
public class CsfdBackfillTask : IScheduledTask
{
    private readonly CsfdRatingService _ratingService;

    public CsfdBackfillTask(CsfdRatingService ratingService)
    {
        _ratingService = ratingService;
    }

    public string Name => "ČSFD rating backfill";

    public string Key => "CsfdRatingBackfill";

    public string Description => "Enqueue CSFD rating fetch for all movies and series.";

    public string Category => "Metadata";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await _ratingService.BackfillLibraryAsync(cancellationToken).ConfigureAwait(false);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return Array.Empty<TaskTriggerInfo>();
    }
}
