using System.Collections.Frozen;

namespace SmartExpenseTracker.Integrations
{
    public sealed record ExchangeRateTable(
        string Provider,
        string BaseCurrency,
        DateTimeOffset PublishedAt,
        FrozenDictionary<string, decimal> BaseUnitsPerCurrencyUnit,
        DateTimeOffset? NextUpdateAt = null);
}
