namespace SmartExpenseTracker.Persistence.ExchangeRates
{
    public interface IRateSnapshotStore
    {
        Task<RateSnapshot?> ReadAsync(string provider, string baseCurrency, CancellationToken cancellationToken);

        Task<RateSnapshot> SaveLatestAsync(RateSnapshot snapshot, CancellationToken cancellationToken);
    }
}
