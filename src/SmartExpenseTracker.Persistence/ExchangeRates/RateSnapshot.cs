using System.Collections.Frozen;
using SmartExpenseTracker.Persistence.Expenses;

namespace SmartExpenseTracker.Persistence.ExchangeRates
{
    public sealed record RateSnapshot(StoredExchangeRateTable Table, DateTimeOffset RetrievedAt)
    {
        private static readonly TimeSpan _allowedClockSkew = TimeSpan.FromMinutes(2);

        public FrozenDictionary<string, StoredCurrencyRate> FallbackRates { get; init; } = FrozenDictionary<string, StoredCurrencyRate>.Empty;

        public bool IsValidAt(DateTimeOffset now)
        {
            if (Table == null || string.IsNullOrWhiteSpace(Table.Provider) || Table.Provider.Length > 64)
            {
                return false;
            }

            return ValidTimestamps(Table.PublishedAt, RetrievedAt, now)
                && HasValidPublishedRates()
                && HasValidFallbackRates(now);
        }

        private static bool ValidTimestamps(DateTimeOffset publishedAt, DateTimeOffset retrievedAt, DateTimeOffset now)
        {
            if (publishedAt <= DateTimeOffset.UnixEpoch || retrievedAt <= DateTimeOffset.UnixEpoch)
            {
                return false;
            }

            return publishedAt - now <= _allowedClockSkew
                && retrievedAt - now <= _allowedClockSkew
                && publishedAt - retrievedAt <= _allowedClockSkew;
        }

        private bool HasValidPublishedRates()
        {
            var rates = Table.BaseUnitsPerCurrencyUnit;
            if (rates == null || rates.Count < 2 || !IsCurrencyCode(Table.BaseCurrency))
            {
                return false;
            }

            if (!rates.TryGetValue(Table.BaseCurrency, out var baseRate) || baseRate != 1m)
            {
                return false;
            }

            return rates.All(rate => IsCurrencyCode(rate.Key) && rate.Value > 0m);
        }

        private bool HasValidFallbackRates(DateTimeOffset now)
        {
            foreach (var (currency, quote) in FallbackRates)
            {
                if (!Table.BaseUnitsPerCurrencyUnit.TryGetValue(currency, out var current) ||
                    ConversionSnapshot.TryGetAppliedRate(current, out _))
                {
                    return false;
                }

                if (quote == null || !ConversionSnapshot.TryGetAppliedRate(quote.Rate, out _))
                {
                    return false;
                }

                if (quote.PublishedAt > Table.PublishedAt || !ValidTimestamps(quote.PublishedAt, quote.RetrievedAt, now))
                {
                    return false;
                }
            }

            return true;
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
