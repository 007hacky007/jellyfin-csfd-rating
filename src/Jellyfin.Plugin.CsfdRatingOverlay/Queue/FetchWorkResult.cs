namespace Jellyfin.Plugin.CsfdRatingOverlay.Queue;

public enum FetchWorkResultKind
{
    Success,
    Throttled,
    TransientError,
    PermanentError
}

public class FetchWorkResult
{
    public static readonly FetchWorkResult Success = new(FetchWorkResultKind.Success, null, null);

    public FetchWorkResult(FetchWorkResultKind kind, TimeSpan? retryAfter, string? message)
    {
        Kind = kind;
        RetryAfter = retryAfter;
        Message = message;
    }

    public FetchWorkResultKind Kind { get; }

    public TimeSpan? RetryAfter { get; }

    public string? Message { get; }

    public static FetchWorkResult Throttled(TimeSpan? retryAfter = null, string? message = null)
        => new(FetchWorkResultKind.Throttled, retryAfter, message);

    public static FetchWorkResult Transient(string? message = null)
        => new(FetchWorkResultKind.TransientError, null, message);

    public static FetchWorkResult Permanent(string? message = null)
        => new(FetchWorkResultKind.PermanentError, null, message);
}
