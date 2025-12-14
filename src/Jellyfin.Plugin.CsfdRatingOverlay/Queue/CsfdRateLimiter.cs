using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CsfdRatingOverlay.Queue;

public sealed class CsfdRateLimiter
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<CsfdRateLimiter> _logger;
    private readonly TimeSpan _minInterval;
    private readonly TimeSpan _minCooldown;

    private DateTimeOffset _nextEarliest = DateTimeOffset.UtcNow;
    private DateTimeOffset _cooldownUntil = DateTimeOffset.MinValue;
    private TimeSpan _currentBackoff = TimeSpan.Zero;

    public CsfdRateLimiter(TimeSpan minInterval, TimeSpan minCooldown, ILogger<CsfdRateLimiter> logger)
    {
        _minInterval = minInterval;
        _minCooldown = minCooldown;
        _logger = logger;
    }

    public async Task<IAsyncDisposable> WaitAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var delay = CalculateDelay(now);
            if (delay > TimeSpan.Zero)
            {
                _logger.LogDebug("CSFD rate limiter sleeping for {Delay} before next request", delay);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            return new Scope(this);
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    public void RegisterThrottleSignal(TimeSpan? retryAfter = null)
    {
        var baseCooldown = retryAfter.HasValue && retryAfter.Value > TimeSpan.Zero ? retryAfter.Value : _minCooldown;
        _currentBackoff = _currentBackoff == TimeSpan.Zero ? baseCooldown : TimeSpan.FromTicks(Math.Min(TimeSpan.FromMinutes(60).Ticks, _currentBackoff.Ticks * 2));
        _cooldownUntil = DateTimeOffset.UtcNow + _currentBackoff;
        _logger.LogWarning("CSFD throttle detected, backing off for {Cooldown}", _currentBackoff);
    }

    private TimeSpan CalculateDelay(DateTimeOffset now)
    {
        var delayUntil = _nextEarliest > _cooldownUntil ? _nextEarliest : _cooldownUntil;
        if (delayUntil <= now)
        {
            return TimeSpan.Zero;
        }

        return delayUntil - now;
    }

    private void Release()
    {
        _nextEarliest = DateTimeOffset.UtcNow + _minInterval;
        _gate.Release();
    }

    private sealed class Scope : IAsyncDisposable
    {
        private readonly CsfdRateLimiter _owner;
        private bool _disposed;

        public Scope(CsfdRateLimiter owner)
        {
            _owner = owner;
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            _owner.Release();
            return ValueTask.CompletedTask;
        }
    }
}
