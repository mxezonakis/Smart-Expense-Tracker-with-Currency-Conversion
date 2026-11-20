using System.Collections.Frozen;

namespace SmartExpenseTracker.Application.ExchangeRates
{
    public sealed record ExchangeRateTable(
        string Provider,
        string BaseCurrency,
        DateTimeOffset PublishedAt,
        FrozenDictionary<string, decimal> BaseUnitsPerCurrencyUnit);
}
