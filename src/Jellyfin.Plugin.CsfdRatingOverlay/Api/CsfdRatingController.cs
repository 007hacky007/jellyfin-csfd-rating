using System.IO;
using System.Reflection;
using Jellyfin.Plugin.CsfdRatingOverlay.Cache;
using Jellyfin.Plugin.CsfdRatingOverlay.Models;
using Jellyfin.Plugin.CsfdRatingOverlay.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.CsfdRatingOverlay.Api;

[ApiController]
[Authorize]
[Route("Plugins/CsfdRatingOverlay")]
public class CsfdRatingController : ControllerBase
{
    private readonly CsfdRatingService _ratingService;
    private readonly ICsfdCacheStore _cacheStore;

    public CsfdRatingController(CsfdRatingService ratingService, ICsfdCacheStore cacheStore)
    {
        _ratingService = ratingService;
        _cacheStore = cacheStore;
    }

    [HttpGet("csfd/items/{itemId}")]
    public async Task<IActionResult> GetItem(string itemId, CancellationToken cancellationToken)
    {
        var result = await _ratingService.GetAsync(itemId, enqueueIfMissing: true, cancellationToken);
        if (result.Status == CsfdCacheEntryStatus.Unknown)
        {
            return Accepted(result);
        }

        return Ok(result);
    }

    [HttpPost("csfd/items/batch")]
    public async Task<IActionResult> GetBatch([FromBody] BatchRequest request, CancellationToken cancellationToken)
    {
        if (request?.ItemIds == null || request.ItemIds.Count == 0)
        {
            return BadRequest("itemIds required");
        }

        var result = await _ratingService.GetBatchAsync(request.ItemIds, enqueueIfMissing: true, cancellationToken);
        return Ok(result);
    }

    [HttpGet("web/overlay.js")]
    [AllowAnonymous]
    public IActionResult GetOverlayScript()
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg != null && !cfg.OverlayInjectionEnabled)
        {
            return StatusCode(StatusCodes.Status403Forbidden, "Overlay injection disabled");
        }

        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = "Jellyfin.Plugin.CsfdRatingOverlay.Web.overlay.js";
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            return NotFound();
        }

        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();
        return Content(content, "application/javascript");
    }

    [HttpPost("csfd/actions/backfill")]
    [Authorize(Roles = "Administrator")]
    public async Task<IActionResult> Backfill(CancellationToken cancellationToken)
    {
        var count = await _ratingService.BackfillLibraryAsync(cancellationToken);
        return Ok(new { enqueued = count });
    }

    [HttpPost("csfd/actions/retry-notfound")]
    [Authorize(Roles = "Administrator")]
    public async Task<IActionResult> RetryNotFound(CancellationToken cancellationToken)
    {
        var count = await _ratingService.RetryNotFoundAsync(cancellationToken);
        return Ok(new { enqueued = count });
    }

    [HttpPost("csfd/actions/reset-cache")]
    [Authorize(Roles = "Administrator")]
    public async Task<IActionResult> ResetCache(CancellationToken cancellationToken)
    {
        await _cacheStore.ClearAllAsync(cancellationToken);
        return Ok(new { status = "cleared" });
    }
}

public class BatchRequest
{
    public List<string> ItemIds { get; set; } = new();
}
