using System.Collections.Frozen;

namespace SmartExpenseTracker.Persistence.ExchangeRates
{
    public sealed record StoredExchangeRateTable(
        string Provider,
        string BaseCurrency,
        DateTimeOffset PublishedAt,
        FrozenDictionary<string, decimal> BaseUnitsPerCurrencyUnit,
        DateTimeOffset? NextUpdateAt = null);
}
