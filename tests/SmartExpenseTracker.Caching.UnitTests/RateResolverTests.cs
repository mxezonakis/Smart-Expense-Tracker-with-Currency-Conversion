using System.Collections.Frozen;
using System.Net;
using FakeItEasy;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;
using SmartExpenseTracker.Application.ExchangeRates;

namespace SmartExpenseTracker.Caching.UnitTests
{
    [CancelAfter(10000)]
    public sealed class RateResolverTests
    {
        private FakeTimeProvider _clock = null!;
        private IExchangeRateProvider _provider = null!;
        private IRateSnapshotStore _store = null!;
        private ServiceProvider _services = null!;
        private RateSnapshot? _saved;
        private ExchangeRateTable _table = null!;
        private static CancellationToken Token => TestContext.CurrentContext.CancellationToken;

        [SetUp]
        public void SetUp()
        {
            _clock = new FakeTimeProvider();
            _saved = null;
            _provider = A.Fake<IExchangeRateProvider>();
            _store = A.Fake<IRateSnapshotStore>();
            _table = new ExchangeRateTable("Test", "EUR", _clock.GetUtcNow(),
                new Dictionary<string, decimal> { ["EUR"] = 1, ["USD"] = 0.8m }.ToFrozenDictionary());
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).ReturnsLazily(() => Task.FromResult(_table));
            A.CallTo(() => _store.ReadAsync("Test", "EUR", A<CancellationToken>._)).ReturnsLazily(() => Task.FromResult(_saved));
            A.CallTo(() => _store.SaveLatestAsync(A<RateSnapshot>._, A<CancellationToken>._))
                .ReturnsLazily((RateSnapshot snapshot, CancellationToken _) => Task.FromResult(_saved = snapshot));
            _services = CreateHost();
        }

        private ServiceProvider CreateHost(TimeProvider? clock = null)
        {
            var services = new ServiceCollection();
            services.AddSingleton<TimeProvider>(clock ?? _clock);
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddScoped(_ => _store);
            services.AddScoped(_ => _provider);
            services.AddRateCaching(new ConfigurationBuilder().Build(), "Test");
            return services.BuildServiceProvider(true);
        }

        [TearDown]
        public void DisposeHost() => _services.Dispose();

        private Task<ResolvedRates> Resolve() => _services.GetRequiredService<IRateResolver>().ResolveAsync(Token);

        [TestCase(true, true)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(false, false)]
        public async Task OptionalStoreFailureStillServesLiveTable(bool read, bool slow)
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<RateSnapshot> Fail(CancellationToken token)
            {
                entered.TrySetResult();
                if (slow)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }

                throw new StoreException();
            }

            if (read)
            {
                A.CallTo(() => _store.ReadAsync("Test", "EUR", A<CancellationToken>._))
                    .ReturnsLazily(async (string _, string currency, CancellationToken token) => (RateSnapshot?)await Fail(token));
            }
            else
            {
                A.CallTo(() => _store.SaveLatestAsync(A<RateSnapshot>._, A<CancellationToken>._))
                    .ReturnsLazily((RateSnapshot _, CancellationToken token) => Fail(token));
            }

            var pending = Resolve();
            await entered.Task.WaitAsync(Token);
            _clock.Advance(TimeSpan.FromSeconds(8));
            var live = await pending.WaitAsync(Token);
            Assert.That(live.IsFallback, Is.False);
            Assert.That(live.Snapshot.Table, Is.EqualTo(_table));
            Assert.That(await Resolve(), Is.EqualTo(live));
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        }

        private sealed class StoreException : System.Data.Common.DbException
        {
        }

        [Test]
        public async Task SequentialOutageMissesRecheckOnceAtRetryAndRecover()
        {
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._))
                .ThrowsAsync(new HttpRequestException()).Once().Then.Returns(_table);
            for (var i = 0; i < 4; i++)
            {
                Assert.ThrowsAsync<RateUnavailableException>(() => Resolve());
            }

            A.CallTo(() => _store.ReadAsync("Test", "EUR", A<CancellationToken>._)).MustHaveHappenedOnceExactly();
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).MustHaveHappenedOnceExactly();
            _clock.Advance(TimeSpan.FromMinutes(20));
            var result = await Resolve();
            Assert.That(result.Snapshot, Is.EqualTo(_saved));
            Assert.That(result.IsFallback, Is.False);
            Assert.That(await Resolve(), Is.EqualTo(result));
            A.CallTo(() => _store.ReadAsync("Test", "EUR", A<CancellationToken>._)).MustHaveHappenedTwiceExactly();
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).MustHaveHappenedTwiceExactly();
        }

        [Test]
        public async Task ProviderRecoveryReplacesFallbackAndReusesNewSnapshot()
        {
            var original = await Resolve();
            _clock.Advance(TimeSpan.FromHours(6));
            _table = _table with { PublishedAt = _clock.GetUtcNow() };
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._))
                .ThrowsAsync(new HttpRequestException()).Once().Then.Returns(_table);
            Assert.That((await Resolve()).Snapshot, Is.EqualTo(original.Snapshot));
            _clock.Advance(TimeSpan.FromMinutes(20));
            var recovered = await Resolve();
            Assert.That(recovered.IsFallback, Is.False);
            Assert.That(recovered.Snapshot.Table, Is.EqualTo(_table));
            Assert.That(_saved, Is.EqualTo(recovered.Snapshot));
            Assert.That(await Resolve(), Is.EqualTo(recovered));
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).MustHaveHappened(3, Times.Exactly);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task CancellationDuringCacheLookupPreventsFastReturn(bool stale)
        {
            var clock = new LookupClock(_clock.GetUtcNow());
            using var host = CreateHost(clock);
            var resolver = host.GetRequiredService<IRateResolver>();
            await resolver.ResolveAsync(Token);
            if (stale)
            {
                clock.Now += TimeSpan.FromHours(6);
                A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).ThrowsAsync(new HttpRequestException());
                Assert.That((await resolver.ResolveAsync(Token)).IsFallback, Is.True);
            }

            using var caller = new CancellationTokenSource();
            clock.OnRead = caller.Cancel;
            Assert.CatchAsync<OperationCanceledException>(() => resolver.ResolveAsync(caller.Token));
        }

        private sealed class LookupClock : TimeProvider
        {
            public LookupClock(DateTimeOffset now)
            {
                Now = now;
            }

            public DateTimeOffset Now { get; set; }
            public Action? OnRead { get; set; }

            public override DateTimeOffset GetUtcNow()
            {
                OnRead?.Invoke();
                return Now;
            }
        }

        [TestCase(true, 24)]
        [TestCase(false, 6)]
        public async Task FreshHitsStopAtRefreshDeadline(bool advertised, int hours)
        {
            _table = _table with { NextUpdateAt = advertised ? _clock.GetUtcNow().AddHours(hours) : null };
            var initial = await Resolve();
            _clock.Advance(TimeSpan.FromHours(hours) - TimeSpan.FromSeconds(1));
            Assert.That((await Resolve()).IsFallback, Is.False);
            Assert.That(_saved, Is.EqualTo(initial.Snapshot));
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).MustHaveHappenedOnceExactly();

            _clock.Advance(TimeSpan.FromSeconds(1));
            Assert.That((await Resolve()).Snapshot.RetrievedAt, Is.EqualTo(_clock.GetUtcNow()));
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).MustHaveHappenedTwiceExactly();
        }

        [TestCase(-1)]
        [TestCase(1000)]
        public async Task UnusableNextUpdateUsesSixHourSchedule(int hours)
        {
            _table = _table with { NextUpdateAt = _clock.GetUtcNow().AddHours(hours) };
            await Resolve();
            _clock.Advance(TimeSpan.FromHours(5));
            await Resolve();
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).MustHaveHappenedOnceExactly();
            _clock.Advance(TimeSpan.FromHours(1));
            await Resolve();
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).MustHaveHappenedTwiceExactly();
        }

        [TestCase(429)]
        [TestCase(503)]
        [TestCase(0)]
        [TestCase(-1)]
        [TestCase(-2)]
        public async Task RefreshFailureReturnsStaleWithinLimitAndSuppressesRetries(int status)
        {
            var initial = await Resolve();
            _clock.Advance(TimeSpan.FromHours(6));
            Exception failure = status switch
            {
                -2 => new InvalidDataException(),
                -1 => new TimeoutException(),
                0 => new HttpRequestException(),
                _ => new HttpRequestException("Provider failure", null, (HttpStatusCode)status)
            };
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).ThrowsAsync(failure);
            Assert.That(await Resolve(), Is.EqualTo(new ResolvedRates(initial.Snapshot, true)));
            Assert.That((await Resolve()).IsFallback, Is.True);
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).MustHaveHappenedTwiceExactly();
            _clock.Advance(TimeSpan.FromMinutes(20));
            Assert.That((await Resolve()).IsFallback, Is.True);
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).MustHaveHappened(3, Times.Exactly);
        }

        [Test]
        public async Task InvalidRateCannotReplaceEligibleSnapshot()
        {
            var initial = await Resolve();
            _clock.Advance(TimeSpan.FromHours(6));
            _table = _table with { BaseCurrency = "GBP" };
            Assert.That((await Resolve()).IsFallback, Is.True);
            Assert.That(_saved, Is.EqualTo(initial.Snapshot));
        }

        [Test]
        public async Task RepeatedPublicationExpiresExactlyAtMaximumAge()
        {
            var first = await Resolve();
            _clock.Advance(TimeSpan.FromHours(48) - TimeSpan.FromSeconds(1));
            var repeated = await Resolve();
            Assert.That(repeated.Snapshot.RetrievedAt, Is.GreaterThan(first.Snapshot.RetrievedAt));
            Assert.That(repeated.Snapshot.Table.PublishedAt, Is.EqualTo(first.Snapshot.Table.PublishedAt));
            _clock.Advance(TimeSpan.FromSeconds(1));
            Assert.ThrowsAsync<RateUnavailableException>(() => Resolve());
        }

        [Test]
        public async Task FreshDurableSnapshotRecoversAfterEvictionAndRestart()
        {
            var first = await Resolve();
            _services.GetRequiredService<IMemoryCache>().Remove(("Test", "EUR"));
            Assert.That(await Resolve(), Is.EqualTo(first));
            using var restarted = CreateHost();
            Assert.That(await restarted.GetRequiredService<IRateResolver>().ResolveAsync(Token), Is.EqualTo(first));
            A.CallTo(() => _store.ReadAsync("Test", "EUR", A<CancellationToken>._)).MustHaveHappened(3, Times.Exactly);
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task ConcurrentWaitersShareFetchAndCancelIndependently()
        {
            var release = new TaskCompletionSource<ExchangeRateTable>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken shared = default;
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._))
                .ReturnsLazily((string _, CancellationToken token) =>
                {
                    shared = token;
                    return release.Task.WaitAsync(token);
                });
            using var caller = new CancellationTokenSource();
            var first = _services.GetRequiredService<IRateResolver>().ResolveAsync(caller.Token);
            var others = Enumerable.Range(0, 16).Select(_ => Resolve()).ToArray();
            await caller.CancelAsync();
            Assert.CatchAsync<OperationCanceledException>(async () => await first);
            Assert.That(shared.IsCancellationRequested, Is.False);
            release.SetResult(_table);
            Assert.That((await Task.WhenAll(others)).All(r => !r.IsFallback), Is.True);
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        }

        [Test]
        public void SharedTimeoutWithoutFallbackIsUnavailableAndSuppressesNextFetch()
        {
            var never = new TaskCompletionSource<ExchangeRateTable>(TaskCreationOptions.RunContinuationsAsynchronously);
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._))
                .ReturnsLazily((string _, CancellationToken token) => never.Task.WaitAsync(token));
            var waiting = Resolve();
            _clock.Advance(TimeSpan.FromSeconds(30));
            Assert.ThrowsAsync<RateUnavailableException>(async () => await waiting.WaitAsync(Token));
            Assert.ThrowsAsync<RateUnavailableException>(() => Resolve());
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        }
    }
}
