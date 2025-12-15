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

    [HttpGet("csfd/client-config")]
    public IActionResult GetClientConfig()
    {
        var config = Plugin.Instance.Configuration;
        return Ok(new { clientCacheVersion = config.ClientCacheVersion });
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

    [HttpGet("csfd/status")]
    [Authorize(Roles = "Administrator")]
    public async Task<IActionResult> GetStatus(CancellationToken cancellationToken)
    {
        var status = await _ratingService.GetStatusAsync(cancellationToken);
        return Ok(status);
    }

    [HttpGet("csfd/unmatched")]
    [Authorize(Roles = "Administrator")]
    public async Task<IActionResult> GetUnmatched(CancellationToken cancellationToken)
    {
        var items = await _ratingService.GetUnmatchedItemsAsync(cancellationToken);
        return Ok(items);
    }

    [HttpPost("csfd/search")]
    [Authorize(Roles = "Administrator")]
    public async Task<IActionResult> Search([FromBody] SearchRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request?.Query))
        {
            return BadRequest("Query required");
        }

        var result = await _ratingService.SearchCsfdAsync(request.Query, cancellationToken);
        if (!result.Success)
        {
            return StatusCode(500, result.Error);
        }

        return Ok(result.Payload);
    }

    [HttpPost("csfd/match")]
    [Authorize(Roles = "Administrator")]
    public async Task<IActionResult> Match([FromBody] MatchRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request?.ItemId) || string.IsNullOrWhiteSpace(request?.CsfdId))
        {
            return BadRequest("ItemId and CsfdId required");
        }

        try
        {
            await _ratingService.ManualMatchAsync(request.ItemId, request.CsfdId, cancellationToken);
            return Ok();
        }
        catch (Exception ex)
        {
            return StatusCode(500, ex.Message);
        }
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

    [HttpPost("csfd/actions/pause")]
    [Authorize(Roles = "Administrator")]
    public IActionResult Pause()
    {
        _ratingService.SetPaused(true);
        return Ok(new { status = "paused" });
    }

    [HttpPost("csfd/actions/resume")]
    [Authorize(Roles = "Administrator")]
    public IActionResult Resume()
    {
        _ratingService.SetPaused(false);
        return Ok(new { status = "resumed" });
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

    [HttpPost("csfd/actions/retry-errors")]
    [Authorize(Roles = "Administrator")]
    public async Task<IActionResult> RetryErrors(CancellationToken cancellationToken)
    {
        var count = await _ratingService.RetryErrorsAsync(cancellationToken);
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
