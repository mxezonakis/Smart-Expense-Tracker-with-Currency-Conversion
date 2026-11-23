using System.Collections.Frozen;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartExpenseTracker.Persistence.ExchangeRates;

namespace SmartExpenseTracker.Persistence
{
    internal sealed class RateSnapshotStore : IRateSnapshotStore
    {
        private static readonly JsonSerializerOptions _jsonOptions = new() { AllowDuplicateProperties = false };
        private readonly TimeProvider _clock;
        private readonly SmartExpenseTrackerDbContext _context;

        public RateSnapshotStore(SmartExpenseTrackerDbContext context, TimeProvider clock)
        {
            _context = context;
            _clock = clock;
        }

        public async Task<RateSnapshot?> ReadAsync(string provider, string baseCurrency, CancellationToken cancellationToken)
        {
            var row = await _context.Set<RateSnapshotRow>().AsNoTracking()
                .SingleOrDefaultAsync(r => r.Provider == provider && r.BaseCurrency == baseCurrency, cancellationToken);
            return Decode(row, provider, baseCurrency);
        }

        private RateSnapshot? Decode(RateSnapshotRow? row, string provider, string baseCurrency)
        {
            if (row == null || row.Provider != provider || row.BaseCurrency != baseCurrency)
            {
                return null;
            }

            try
            {
                using var json = JsonDocument.Parse(row.Rates, new JsonDocumentOptions { AllowDuplicateProperties = false });
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                // Legacy rows contain only the currency-to-rate dictionary.
                var fallbacks = FrozenDictionary<string, StoredCurrencyRate>.Empty;
                IReadOnlyDictionary<string, decimal>? rates;
                if (root.TryGetProperty("Rates", out _))
                {
                    var payload = root.Deserialize<SnapshotPayload>(_jsonOptions);
                    if (payload?.Fallbacks == null || payload.Rates == null)
                    {
                        return null;
                    }

                    fallbacks = payload.Fallbacks.ToFrozenDictionary(StringComparer.Ordinal);
                    rates = payload.Rates;
                }
                else
                {
                    rates = root.Deserialize<Dictionary<string, decimal>>(_jsonOptions);
                }

                if (rates == null)
                {
                    return null;
                }

                var table = new StoredExchangeRateTable(row.Provider, row.BaseCurrency, row.PublishedAt,
                    rates.ToFrozenDictionary(StringComparer.Ordinal), row.NextUpdateAt);
                var snapshot = new RateSnapshot(table, row.RetrievedAt) { FallbackRates = fallbacks };
                return snapshot.IsValidAt(_clock.GetUtcNow()) ? snapshot : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public async Task<RateSnapshot> SaveLatestAsync(RateSnapshot snapshot, CancellationToken cancellationToken)
        {
            if (!snapshot.IsValidAt(_clock.GetUtcNow()))
            {
                throw new InvalidDataException("Invalid rate snapshot.");
            }

            var table = snapshot.Table;
            await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            // Hold the row (or missing-key range) through validation and replacement.
            var row = await _context.Set<RateSnapshotRow>().FromSqlInterpolated(
                $"SELECT * FROM [RateSnapshots] WITH (UPDLOCK, HOLDLOCK) WHERE Provider = {table.Provider} AND BaseCurrency = {table.BaseCurrency}")
                .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
            var existing = Decode(row, table.Provider, table.BaseCurrency);
            snapshot = existing?.MergeWith(snapshot) ?? snapshot;
            if (ReferenceEquals(snapshot, existing))
            {
                await transaction.CommitAsync(cancellationToken);
                return snapshot;
            }

            table = snapshot.Table;
            var rates = snapshot.FallbackRates.Count == 0
                ? JsonSerializer.Serialize(table.BaseUnitsPerCurrencyUnit)
                : JsonSerializer.Serialize(new SnapshotPayload(table.BaseUnitsPerCurrencyUnit, snapshot.FallbackRates));
            if (row == null)
            {
                await _context.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO [RateSnapshots] (Provider, BaseCurrency, PublishedAt, RetrievedAt, NextUpdateAt, Rates)
                    VALUES ({table.Provider}, {table.BaseCurrency}, {table.PublishedAt}, {snapshot.RetrievedAt}, {table.NextUpdateAt}, {rates})
                    """, cancellationToken);
            }
            else
            {
                await _context.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE [RateSnapshots] SET Provider = {table.Provider}, BaseCurrency = {table.BaseCurrency},
                        PublishedAt = {table.PublishedAt}, RetrievedAt = {snapshot.RetrievedAt}, NextUpdateAt = {table.NextUpdateAt}, Rates = {rates}
                    WHERE Provider = {table.Provider} AND BaseCurrency = {table.BaseCurrency}
                    """, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return snapshot;
        }

        private sealed record SnapshotPayload(IReadOnlyDictionary<string, decimal> Rates, IReadOnlyDictionary<string, StoredCurrencyRate> Fallbacks);
    }
}
