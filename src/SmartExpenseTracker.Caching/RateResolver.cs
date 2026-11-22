using System.Data.Common;
using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartExpenseTracker.Application.ExchangeRates;

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
                if (cached != null && Fresh(cached, now))
                {
                    return new ResolvedRates(cached, false);
                }

                if (now < _retryAt)
                {
                    return Fallback(cached);
                }

                flight = _flight ??= new Lazy<Task<ResolvedRates>>(RefreshAsync);
            }

            var resolved = await flight.Value.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!Eligible(resolved.Snapshot, _clock.GetUtcNow()))
            {
                throw new RateUnavailableException();
            }

            return resolved;
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
                bool recovering;
                lock (_gate)
                {
                    recovering = _retryAt != default;
                }

                if (recovering || previous == null || !Eligible(previous, _clock.GetUtcNow()))
                {
                    try
                    {
                        using var readBudget = new CancellationTokenSource(_options.RefreshTimeout / 4, _clock);
                        using var read = CancellationTokenSource.CreateLinkedTokenSource(token, readBudget.Token);
                        var stored = await store.ReadAsync(_key.Provider, _key.Currency, read.Token);
                        if (stored != null && Valid(stored, _clock.GetUtcNow()) &&
                            (previous == null || stored.Table.PublishedAt > previous.Table.PublishedAt ||
                            (stored.Table.PublishedAt == previous.Table.PublishedAt && stored.RetrievedAt >= previous.RetrievedAt)))
                        {
                            previous = stored;
                            Remember(stored);
                        }
                    }
                    catch (Exception ex) when (ex is DbException or InvalidDataException or TimeoutException or OperationCanceledException)
                    {
                        _logger.LogWarning("Durable rate read failed ({FailureType}).", ex.GetType().Name);
                    }
                }

                var now = _clock.GetUtcNow();
                if (previous != null && Fresh(previous, now))
                {
                    ClearRetry();
                    return new ResolvedRates(previous, false);
                }

                if (token.IsCancellationRequested)
                {
                    return Fallback(previous);
                }

                var provider = scope.ServiceProvider.GetRequiredService<IExchangeRateProvider>();
                var table = await provider.GetLatestAsync(_key.Currency, token);
                token.ThrowIfCancellationRequested();
                var candidate = new RateSnapshot(table, _clock.GetUtcNow());
                if (!Valid(candidate, candidate.RetrievedAt) || (previous != null && table.PublishedAt < previous.Table.PublishedAt))
                {
                    throw new InvalidDataException("Unusable exchange-rate table.");
                }

                try
                {
                    using var saveBudget = new CancellationTokenSource(_options.RefreshTimeout / 4, _clock);
                    using var save = CancellationTokenSource.CreateLinkedTokenSource(token, saveBudget.Token);
                    var winner = await store.SaveLatestAsync(candidate, save.Token);
                    if (Valid(winner, _clock.GetUtcNow()))
                    {
                        candidate = winner;
                    }
                    else
                    {
                        _logger.LogWarning("Durable rate store returned an unusable table.");
                    }
                }
                catch (Exception ex) when (ex is DbException or InvalidDataException or TimeoutException or OperationCanceledException)
                {
                    _logger.LogWarning("Durable rate write failed ({FailureType}).", ex.GetType().Name);
                }

                if (!Valid(candidate, _clock.GetUtcNow()))
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

        private void ClearRetry()
        {
            lock (_gate)
            {
                _retryAt = default;
            }
        }

        private ResolvedRates Fallback(RateSnapshot? snapshot, Exception? cause = null)
        {
            if (snapshot == null || !Eligible(snapshot, _clock.GetUtcNow()))
            {
                throw new RateUnavailableException(cause);
            }

            return new ResolvedRates(snapshot, true);
        }

        private bool Fresh(RateSnapshot snapshot, DateTimeOffset now)
        {
            var refreshAt = snapshot.Table.NextUpdateAt is { } next && next > snapshot.RetrievedAt &&
                next > snapshot.Table.PublishedAt && next - snapshot.Table.PublishedAt <= TimeSpan.FromHours(30)
                ? next : snapshot.RetrievedAt + _options.RefreshInterval;
            return Eligible(snapshot, now) && now < refreshAt;
        }

        private bool Eligible(RateSnapshot snapshot, DateTimeOffset now)
        {
            return now - snapshot.Table.PublishedAt < _options.MaximumAge &&
                snapshot.Table.PublishedAt - now <= TimeSpan.FromMinutes(2);
        }

        private bool Valid(RateSnapshot snapshot, DateTimeOffset now)
        {
            var table = snapshot.Table;
            return snapshot.IsValidAt(now) && table.Provider == _key.Provider && table.BaseCurrency == _key.Currency && Eligible(snapshot, now);
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
