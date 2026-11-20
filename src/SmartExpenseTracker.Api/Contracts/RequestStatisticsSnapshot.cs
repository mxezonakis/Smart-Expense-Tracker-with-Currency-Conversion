namespace SmartExpenseTracker.Api.Contracts
{
    public sealed record RequestStatisticsSnapshot(long CompletedRequests, long FailedRequests, double TotalDurationMs, double AverageDurationMs);
}
