namespace SmartExpenseTracker.Application.ExchangeRates
{
    public interface IExchangeRateProvider
    {
        Task<ExchangeRateTable> GetLatestAsync(string baseCurrency, CancellationToken cancellationToken);
    }
}
