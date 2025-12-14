namespace Jellyfin.Plugin.CsfdRatingOverlay.Client;

public class CsfdClientResult<T>
{
    private CsfdClientResult(bool success, T? payload, bool throttled, TimeSpan? retryAfter, string? error)
    {
        Success = success;
        Payload = payload;
        Throttled = throttled;
        RetryAfter = retryAfter;
        Error = error;
    }

    public bool Success { get; }

    public T? Payload { get; }

    public bool Throttled { get; }

    public TimeSpan? RetryAfter { get; }

    public string? Error { get; }

    public static CsfdClientResult<T> Ok(T payload) => new(true, payload, false, null, null);

    public static CsfdClientResult<T> Throttle(TimeSpan? retryAfter = null, string? error = null)
        => new(false, default, true, retryAfter, error);

    public static CsfdClientResult<T> Fail(string? error = null)
        => new(false, default, false, null, error);
}
