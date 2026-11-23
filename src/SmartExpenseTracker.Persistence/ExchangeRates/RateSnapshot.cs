namespace SmartExpenseTracker.Persistence.ExchangeRates
{
    public sealed record RateSnapshot(StoredExchangeRateTable Table, DateTimeOffset RetrievedAt)
    {
        public bool IsValidAt(DateTimeOffset now)
        {
            return Table != null && !string.IsNullOrWhiteSpace(Table.Provider) && Table.Provider.Length <= 64 &&
                IsCurrencyCode(Table.BaseCurrency) && Table.PublishedAt > DateTimeOffset.UnixEpoch &&
                RetrievedAt - now <= TimeSpan.FromMinutes(2) && Table.PublishedAt - now <= TimeSpan.FromMinutes(2) &&
                RetrievedAt > DateTimeOffset.UnixEpoch && Table.PublishedAt - RetrievedAt <= TimeSpan.FromMinutes(2) &&
                Table.BaseUnitsPerCurrencyUnit is { Count: > 1 } rates &&
                rates.TryGetValue(Table.BaseCurrency, out var identity) && identity == 1m &&
                rates.All(rate => IsCurrencyCode(rate.Key) && rate.Value > 0m);
        }

        private static bool IsCurrencyCode(string? code)
        {
            return code is { Length: 3 } && code.All(char.IsAsciiLetterUpper);
        }
    }
}
