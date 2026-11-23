using SmartExpenseTracker.Persistence.ExchangeRates;

namespace SmartExpenseTracker.Caching
{
    public sealed record ResolvedRates(RateSnapshot Snapshot, bool IsFallback);

    public interface IRateResolver
    {
        Task<ResolvedRates> ResolveAsync(CancellationToken cancellationToken);
    }

    public sealed class RateUnavailableException : Exception
    {
        public RateUnavailableException(Exception? cause = null)
            : base("No eligible exchange-rate table is available.", cause)
        {
        }
    }
}
