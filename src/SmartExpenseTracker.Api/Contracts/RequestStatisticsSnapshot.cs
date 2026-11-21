namespace SmartExpenseTracker.Api.Contracts
{
    /// <summary>An atomic snapshot of request statistics for one running application instance.</summary>
    /// <remarks>All values are zero initially and reset on restart. Escaping downstream exceptions are not recorded.</remarks>
    /// <param name="CompletedRequests">Number of recorded, normally completed requests; statistics reads and aborted requests are excluded.</param>
    /// <param name="FailedRequests">Recorded completed requests whose final HTTP status is 400-599.</param>
    /// <param name="TotalDurationMs">Cumulative elapsed HTTP pipeline duration in milliseconds, measured with a monotonic clock.</param>
    /// <param name="AverageDurationMs">Total duration divided by completed requests, or zero when no requests have been recorded.</param>
    public sealed record RequestStatisticsSnapshot(long CompletedRequests, long FailedRequests, double TotalDurationMs, double AverageDurationMs);
}
