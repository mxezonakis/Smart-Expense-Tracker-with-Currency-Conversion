using System.Data.Common;
using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartExpenseTracker.Integrations;
using SmartExpenseTracker.Persistence.ExchangeRates;

namespace SmartExpenseTracker.Caching
{
    internal sealed class RateResolver : IRateResolver
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly IMemoryCache _cache;
        private readonly TimeProvider _clock;
        private readonly RateCacheOptions _options;
        private readonly ILogger<RateResolver> _logger;
        private readonly (string Provider, string Currency) _key;
        private readonly object _gate = new();
        private Lazy<Task<ResolvedRates>>? _flight;
        private DateTimeOffset _retryAt;

        public RateResolver(IServiceScopeFactory scopes, IMemoryCache cache, TimeProvider clock, IOptions<RateCacheOptions> options,
            ILogger<RateResolver> logger, string provider)
        {
            _scopes = scopes;
            _cache = cache;
            _clock = clock;
            _options = options.Value;
            _logger = logger;
            _key = (provider, _options.BaseCurrency);
        }

        public async Task<ResolvedRates> ResolveAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Lazy<Task<ResolvedRates>> flight;
            lock (_gate)
            {
                var now = _clock.GetUtcNow();
                var cached = _cache.Get<RateSnapshot>(_key);
                cancellationToken.ThrowIfCancellationRequested();
                if (cached != null && IsFresh(cached, now))
                {
                    return ForCaller(new ResolvedRates(cached, false));
                }

                if (now < _retryAt)
                {
                    return ForCaller(Fallback(cached));
                }

                flight = _flight ??= new Lazy<Task<ResolvedRates>>(RefreshAsync);
            }

            var resolved = await flight.Value.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsWithinAgeLimit(resolved.Snapshot, _clock.GetUtcNow()))
            {
                throw new RateUnavailableException();
            }

            return ForCaller(resolved);
        }

        private ResolvedRates ForCaller(ResolvedRates rates)
        {
            return rates.Snapshot.FallbackRates.Count == 0 ? rates :
                rates with { Clock = _clock, MaximumAge = _options.MaximumAge };
        }

        private async Task<ResolvedRates> RefreshAsync()
        {
            RateSnapshot? previous = null;
            try
            {
                using var deadline = new CancellationTokenSource(_options.RefreshTimeout, _clock);
                var token = deadline.Token;
                await using var scope = _scopes.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<IRateSnapshotStore>();
                previous = _cache.Get<RateSnapshot>(_key);
                previous = await RecoverSnapshotAsync(store, previous, token);

                var now = _clock.GetUtcNow();
                if (previous != null && IsFresh(previous, now))
                {
                    ClearRetry();
                    return new ResolvedRates(previous, false);
                }

                token.ThrowIfCancellationRequested();

                var provider = scope.ServiceProvider.GetRequiredService<IExchangeRateProvider>();
                var candidate = await FetchSnapshotAsync(provider, previous, token);
                candidate = await SaveSnapshotAsync(store, candidate, token);
                if (!IsUsableSnapshot(candidate, _clock.GetUtcNow()))
                {
                    throw new InvalidDataException("Unusable stored exchange-rate table.");
                }

                ClearRetry();
                Remember(candidate);
                return new ResolvedRates(candidate, false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or OperationCanceledException or InvalidDataException)
            {
                lock (_gate)
                {
                    var interval = ex is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests }
                        ? _options.RateLimitRetryInterval
                        : _options.RetryInterval;
                    _retryAt = _clock.GetUtcNow() + interval;
                }

                return Fallback(previous, ex);
            }
            finally
            {
                lock (_gate)
                {
                    _flight = null;
                }
            }
        }

        private async Task<RateSnapshot?> RecoverSnapshotAsync(IRateSnapshotStore store, RateSnapshot? cached, CancellationToken token)
        {
            bool retryPending;
            lock (_gate)
            {
                retryPending = _retryAt != default;
            }

            if (!retryPending && cached != null && IsWithinAgeLimit(cached, _clock.GetUtcNow()))
            {
                return cached;
            }

            try
            {
                using var readBudget = new CancellationTokenSource(_options.RefreshTimeout / 4, _clock);
                using var read = CancellationTokenSource.CreateLinkedTokenSource(token, readBudget.Token);
                var stored = await store.ReadAsync(_key.Provider, _key.Currency, read.Token);
                if (stored != null && IsUsableSnapshot(stored, _clock.GetUtcNow()))
                {
                    var recovered = stored.MergeWith(cached);
                    Remember(recovered);
                    return recovered;
                }
            }
            catch (Exception ex) when (IsRecoverableStoreFailure(ex))
            {
                _logger.LogWarning("Durable rate read failed ({FailureType}).", ex.GetType().Name);
            }

            return cached;
        }

        private async Task<RateSnapshot> FetchSnapshotAsync(IExchangeRateProvider provider, RateSnapshot? previous, CancellationToken token)
        {
            var table = await provider.GetLatestAsync(_key.Currency, token);
            token.ThrowIfCancellationRequested();
            if (table is null)
            {
                throw new InvalidDataException("Unusable exchange-rate table.");
            }

            var storedTable = new StoredExchangeRateTable(table.Provider, table.BaseCurrency, table.PublishedAt,
                table.BaseUnitsPerCurrencyUnit, table.NextUpdateAt);
            var candidate = new RateSnapshot(storedTable, _clock.GetUtcNow());
            if (!IsUsableSnapshot(candidate, candidate.RetrievedAt) || (previous != null && table.PublishedAt < previous.Table.PublishedAt))
            {
                throw new InvalidDataException("Unusable exchange-rate table.");
            }

            return candidate.MergeWith(previous);
        }

        private async Task<RateSnapshot> SaveSnapshotAsync(IRateSnapshotStore store, RateSnapshot candidate, CancellationToken token)
        {
            try
            {
                using var saveBudget = new CancellationTokenSource(_options.RefreshTimeout / 4, _clock);
                using var save = CancellationTokenSource.CreateLinkedTokenSource(token, saveBudget.Token);
                var winner = await store.SaveLatestAsync(candidate, save.Token);
                if (IsUsableSnapshot(winner, _clock.GetUtcNow()))
                {
                    return winner;
                }

                _logger.LogWarning("Durable rate store returned an unusable table.");
            }
            catch (Exception ex) when (IsRecoverableStoreFailure(ex))
            {
                _logger.LogWarning("Durable rate write failed ({FailureType}).", ex.GetType().Name);
            }

            return candidate;
        }

        private static bool IsRecoverableStoreFailure(Exception exception)
        {
            // The SQL Server execution strategy wraps transient query failures.
            return exception is DbException or InvalidDataException or TimeoutException or OperationCanceledException
                or InvalidOperationException { InnerException: DbException or TimeoutException };
        }

        private void ClearRetry()
        {
            lock (_gate)
            {
                _retryAt = default;
            }
        }

        private ResolvedRates Fallback(RateSnapshot? snapshot, Exception? cause = null)
        {
            if (snapshot == null || !IsWithinAgeLimit(snapshot, _clock.GetUtcNow()))
            {
                throw new RateUnavailableException(cause);
            }

            return new ResolvedRates(snapshot, true);
        }

        private bool IsFresh(RateSnapshot snapshot, DateTimeOffset now)
        {
            return IsWithinAgeLimit(snapshot, now) && now < RefreshDueAt(snapshot);
        }

        private DateTimeOffset RefreshDueAt(RateSnapshot snapshot)
        {
            var table = snapshot.Table;
            if (table.NextUpdateAt is not { } next || next <= snapshot.RetrievedAt)
            {
                return snapshot.RetrievedAt + _options.RefreshInterval;
            }

            var publicationInterval = next - table.PublishedAt;
            if (publicationInterval <= TimeSpan.Zero || publicationInterval > TimeSpan.FromHours(30))
            {
                return snapshot.RetrievedAt + _options.RefreshInterval;
            }

            return next;
        }

        private bool IsWithinAgeLimit(RateSnapshot snapshot, DateTimeOffset now)
        {
            return now - snapshot.Table.PublishedAt < _options.MaximumAge &&
                snapshot.Table.PublishedAt - now <= TimeSpan.FromMinutes(2);
        }

        private bool IsUsableSnapshot(RateSnapshot snapshot, DateTimeOffset now)
        {
            var table = snapshot.Table;
            if (!snapshot.IsValidAt(now))
            {
                return false;
            }

            return table.Provider == _key.Provider && table.BaseCurrency == _key.Currency && IsWithinAgeLimit(snapshot, now);
        }

        private void Remember(RateSnapshot snapshot)
        {
            var remaining = _options.MaximumAge - (_clock.GetUtcNow() - snapshot.Table.PublishedAt);
            if (remaining > TimeSpan.Zero)
            {
                _cache.Set(_key, snapshot, remaining);
            }
        }
    }
}
