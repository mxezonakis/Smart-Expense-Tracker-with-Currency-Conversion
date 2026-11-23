using System.Collections.Frozen;
using SmartExpenseTracker.Persistence.Expenses;

namespace SmartExpenseTracker.Persistence.ExchangeRates
{
    public sealed record RateSnapshot(StoredExchangeRateTable Table, DateTimeOffset RetrievedAt)
    {
        public FrozenDictionary<string, StoredCurrencyRate> FallbackRates { get; init; } = FrozenDictionary<string, StoredCurrencyRate>.Empty;

        public bool IsValidAt(DateTimeOffset now)
        {
            return Table != null && !string.IsNullOrWhiteSpace(Table.Provider) && Table.Provider.Length <= 64 &&
                IsCurrencyCode(Table.BaseCurrency) && Table.PublishedAt > DateTimeOffset.UnixEpoch &&
                RetrievedAt - now <= TimeSpan.FromMinutes(2) && Table.PublishedAt - now <= TimeSpan.FromMinutes(2) &&
                RetrievedAt > DateTimeOffset.UnixEpoch && Table.PublishedAt - RetrievedAt <= TimeSpan.FromMinutes(2) &&
                Table.BaseUnitsPerCurrencyUnit is { Count: > 1 } rates &&
                rates.TryGetValue(Table.BaseCurrency, out var identity) && identity == 1m &&
                rates.All(rate => IsCurrencyCode(rate.Key) && rate.Value > 0m) &&
                FallbackRates.All(pair => rates.TryGetValue(pair.Key, out var current) &&
                    !ConversionSnapshot.TryGetAppliedRate(current, out _) && pair.Value is { } quote &&
                    ConversionSnapshot.TryGetAppliedRate(quote.Rate, out _) &&
                    quote.PublishedAt > DateTimeOffset.UnixEpoch && quote.PublishedAt <= Table.PublishedAt &&
                    quote.RetrievedAt > DateTimeOffset.UnixEpoch && quote.RetrievedAt - now <= TimeSpan.FromMinutes(2) &&
                    quote.PublishedAt - quote.RetrievedAt <= TimeSpan.FromMinutes(2));
        }

        // Keep the newest publication, then the newest retrieval. This instance wins an exact tie.
        public RateSnapshot MergeWith(RateSnapshot? other)
        {
            if (other == null || Table.Provider != other.Table.Provider || Table.BaseCurrency != other.Table.BaseCurrency)
            {
                return this;
            }

            return (Table.PublishedAt, RetrievedAt).CompareTo((other.Table.PublishedAt, other.RetrievedAt)) >= 0
                ? RetainUsableRatesFrom(other)
                : other.RetainUsableRatesFrom(this);
        }

        private RateSnapshot RetainUsableRatesFrom(RateSnapshot previous)
        {
            Dictionary<string, StoredCurrencyRate>? merged = null;
            foreach (var (currency, current) in Table.BaseUnitsPerCurrencyUnit)
            {
                if (ConversionSnapshot.TryGetAppliedRate(current, out _) ||
                    !previous.Table.BaseUnitsPerCurrencyUnit.TryGetValue(currency, out var prior))
                {
                    continue;
                }

                var quote = ConversionSnapshot.TryGetAppliedRate(prior, out _)
                    ? new StoredCurrencyRate(prior, previous.Table.PublishedAt, previous.RetrievedAt)
                    : previous.FallbackRates.GetValueOrDefault(currency);
                if (quote == null || quote.PublishedAt > Table.PublishedAt ||
                    (FallbackRates.TryGetValue(currency, out var retained) &&
                    (retained.PublishedAt, retained.RetrievedAt).CompareTo((quote.PublishedAt, quote.RetrievedAt)) >= 0))
                {
                    continue;
                }

                merged ??= new Dictionary<string, StoredCurrencyRate>(FallbackRates, StringComparer.Ordinal);
                merged[currency] = quote;
            }

            return merged == null ? this : this with { FallbackRates = merged.ToFrozenDictionary(StringComparer.Ordinal) };
        }

        private static bool IsCurrencyCode(string? code)
        {
            return code is { Length: 3 } && code.All(char.IsAsciiLetterUpper);
        }
    }

    public sealed record StoredCurrencyRate(decimal Rate, DateTimeOffset PublishedAt, DateTimeOffset RetrievedAt);
}
