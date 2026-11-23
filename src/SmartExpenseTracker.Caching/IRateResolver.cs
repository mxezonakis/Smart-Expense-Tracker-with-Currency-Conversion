using SmartExpenseTracker.Persistence.ExchangeRates;
using SmartExpenseTracker.Persistence.Expenses;

namespace SmartExpenseTracker.Caching
{
    public sealed record ResolvedRate(decimal AppliedRate, DateTimeOffset PublishedAt, bool IsFallback);

    public sealed record ResolvedRates(RateSnapshot Snapshot, bool IsFallback)
    {
        internal TimeProvider Clock { get; init; } = TimeProvider.System;
        internal TimeSpan MaximumAge { get; init; } = RateCacheOptions.MaximumAllowedAge;

        public ResolvedRate? ForCurrency(string currency)
        {
            if (!Snapshot.Table.BaseUnitsPerCurrencyUnit.TryGetValue(currency, out var rate))
            {
                return null;
            }

            if (ConversionSnapshot.TryGetAppliedRate(rate, out var applied))
            {
                return new ResolvedRate(applied, Snapshot.Table.PublishedAt, IsFallback);
            }

            if (Snapshot.FallbackRates.TryGetValue(currency, out var prior) && Clock.GetUtcNow() - prior.PublishedAt < MaximumAge &&
                ConversionSnapshot.TryGetAppliedRate(prior.Rate, out applied))
            {
                return new ResolvedRate(applied, prior.PublishedAt, true);
            }

            throw new RateUnavailableException();
        }
    }

    public interface IRateResolver
    {
        Task<ResolvedRates> ResolveAsync(CancellationToken cancellationToken);
    }

    public sealed class RateUnavailableException : Exception
    {
        public RateUnavailableException(Exception? cause = null)
            : base("No eligible exchange rate is available.", cause)
        {
        }
    }
}
