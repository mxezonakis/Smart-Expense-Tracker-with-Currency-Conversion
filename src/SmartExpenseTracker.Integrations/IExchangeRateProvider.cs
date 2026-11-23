namespace SmartExpenseTracker.Integrations
{
    public interface IExchangeRateProvider
    {
        Task<ExchangeRateTable> GetLatestAsync(string baseCurrency, CancellationToken cancellationToken);
    }
}
