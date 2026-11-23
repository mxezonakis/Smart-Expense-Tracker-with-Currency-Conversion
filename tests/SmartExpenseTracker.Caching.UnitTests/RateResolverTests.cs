using System.Collections.Frozen;
using System.Net;
using FakeItEasy;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;
using SmartExpenseTracker.Integrations;
using SmartExpenseTracker.Persistence.ExchangeRates;

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

        private ServiceProvider CreateHost(TimeProvider? clock = null, Dictionary<string, string?>? configuration = null)
        {
            var services = new ServiceCollection();
            services.AddSingleton<TimeProvider>(clock ?? _clock);
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddScoped(_ => _store);
            services.AddScoped(_ => _provider);
            services.AddRateCaching(new ConfigurationBuilder().AddInMemoryCollection(configuration ?? []).Build(), "Test");
            return services.BuildServiceProvider(true);
        }

        [TearDown]
        public void DisposeHost() => _services.Dispose();

        private Task<ResolvedRates> Resolve() => _services.GetRequiredService<IRateResolver>().ResolveAsync(Token);

        private static StoredExchangeRateTable ToStoredTable(ExchangeRateTable table)
        {
            return new StoredExchangeRateTable(table.Provider, table.BaseCurrency, table.PublishedAt,
                table.BaseUnitsPerCurrencyUnit, table.NextUpdateAt);
        }

        [Test]
        public void AlreadyCancelledCallerDoesNotReadOrRefresh()
        {
            // Arrange
            using var caller = new CancellationTokenSource();
            caller.Cancel();

            // Act
            AsyncTestDelegate act = () =>
                _services.GetRequiredService<IRateResolver>().ResolveAsync(caller.Token);

            // Assert
            Assert.CatchAsync<OperationCanceledException>(act);
            A.CallTo(() => _store.ReadAsync("Test", "EUR", A<CancellationToken>._)).MustNotHaveHappened();
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).MustNotHaveHappened();
            Assert.That(_saved, Is.Null);
        }

        [TestCase("BaseCurrency", "eur")]
        [TestCase("BaseCurrency", "EU")]
        [TestCase("BaseCurrency", "EÜR")]
        [TestCase("BaseCurrency", "")]
        [TestCase("RefreshInterval", "00:00:00")]
        [TestCase("RefreshInterval", "-00:00:01")]
        [TestCase("MaximumAge", "05:59:59")]
        [TestCase("MaximumAge", "365.00:00:00.0000001")]
        [TestCase("RefreshTimeout", "00:00:00")]
        [TestCase("RefreshTimeout", "-00:00:01")]
        [TestCase("RefreshTimeout", "00:05:00.0000001")]
        public void RejectsInvalidCachePolicy(string option, string value)
        {
            // Arrange
            using var host = CreateHost(configuration: new Dictionary<string, string?> { [$"RateCache:{option}"] = value });

            // Act
            TestDelegate act = () => host.GetRequiredService<IOptions<RateCacheOptions>>().Value.ToString();

            // Assert
            Assert.Throws<OptionsValidationException>(act);
        }

        [Test]
        public void AcceptsInclusiveCachePolicyLimits()
        {
            // Arrange
            using var host = CreateHost(configuration: new Dictionary<string, string?>
            {
                ["RateCache:RefreshInterval"] = "365.00:00:00",
                ["RateCache:MaximumAge"] = "365.00:00:00",
                ["RateCache:RefreshTimeout"] = "00:05:00"
            });

            // Act
            var options = host.GetRequiredService<IOptions<RateCacheOptions>>().Value;

            // Assert
            Assert.That(options.RefreshInterval, Is.EqualTo(TimeSpan.FromDays(365)));
            Assert.That(options.MaximumAge, Is.EqualTo(options.RefreshInterval));
            Assert.That(options.RefreshTimeout, Is.EqualTo(TimeSpan.FromMinutes(5)));
        }

        [TestCase(0, false, 6)]
        [TestCase(30, false, 30)]
        [TestCase(30, true, 6)]
        public async Task NextUpdateSanityBoundaryDeterminesExactRefreshDeadline(int hours, bool beyondLimit, int refreshHours)
        {
            // Arrange
            _table = _table with { NextUpdateAt = _clock.GetUtcNow().AddHours(hours).AddTicks(beyondLimit ? 1 : 0) };

            // Act
            var original = await Resolve();

            // Arrange
            _clock.Advance(TimeSpan.FromHours(refreshHours) - TimeSpan.FromTicks(1));

            // Act
            var actual = await Resolve();

            // Assert
            Assert.That(actual, Is.EqualTo(original));
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).MustHaveHappenedOnceExactly();

            // Arrange
            _clock.Advance(TimeSpan.FromTicks(1));

            // Act
            var refreshed = await Resolve();

            // Assert
            Assert.That(refreshed.IsFallback, Is.False);
            Assert.That(refreshed.Snapshot.RetrievedAt, Is.EqualTo(_clock.GetUtcNow()));
            Assert.That(refreshed.Snapshot.Table, Is.EqualTo(ToStoredTable(_table)));
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).MustHaveHappenedTwiceExactly();
        }

        [TestCase("provider")]
        [TestCase("older")]
        [TestCase("identity")]
        public async Task UnusableProviderRefreshPreservesLastKnownSnapshot(string invalid)
        {
            // Arrange
            // Use the shared fixture and test-case inputs.

            // Act
            var original = await Resolve();

            // Arrange
            _clock.Advance(TimeSpan.FromHours(6));
            _table = invalid switch
            {
                "provider" => _table with { Provider = "Other" },
                "older" => _table with { PublishedAt = _table.PublishedAt.AddTicks(-1) },
                _ => _table with { BaseUnitsPerCurrencyUnit = new Dictionary<string, decimal> { ["EUR"] = 2, ["USD"] = 0.9m }.ToFrozenDictionary() }
            };

            // Act
            var actual = await Resolve();

            // Assert
            Assert.That(actual, Is.EqualTo(new ResolvedRates(original.Snapshot, true)));
            Assert.That(_saved, Is.EqualTo(original.Snapshot));
            A.CallTo(() => _store.SaveLatestAsync(A<RateSnapshot>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task ColdRecoveryRejectsSnapshotForDifferentProviderOrCurrency(bool wrongProvider)
        {
            // Arrange
            _saved = new RateSnapshot(ToStoredTable(wrongProvider ? _table with { Provider = "Other" } : _table with
            {
                BaseCurrency = "USD",
                BaseUnitsPerCurrencyUnit = new Dictionary<string, decimal> { ["EUR"] = 1.25m, ["USD"] = 1 }.ToFrozenDictionary()
            }), _clock.GetUtcNow());

            // Act
            var resolved = await Resolve();

            // Assert
            Assert.That(resolved.IsFallback, Is.False);
            Assert.That(resolved.Snapshot.Table, Is.EqualTo(ToStoredTable(_table)));
            Assert.That(_saved, Is.EqualTo(resolved.Snapshot));
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task DurableWinnerIsUsedOnlyWhenValid(bool valid)
        {
            // Arrange
            var winner = new RateSnapshot(ToStoredTable(_table with
            {
                Provider = valid ? "Test" : "Other",
                BaseUnitsPerCurrencyUnit = new Dictionary<string, decimal> { ["EUR"] = 1, ["USD"] = 0.9m }.ToFrozenDictionary()
            }), _clock.GetUtcNow());
            A.CallTo(() => _store.SaveLatestAsync(A<RateSnapshot>._, A<CancellationToken>._)).Returns(winner);

            // Act
            var result = await Resolve();

            // Assert
            Assert.That(result.IsFallback, Is.False);
            Assert.That(result.Snapshot, Is.EqualTo(valid ? winner : new RateSnapshot(ToStoredTable(_table), _clock.GetUtcNow())));
            Assert.That(result.Snapshot.Table.BaseUnitsPerCurrencyUnit["USD"], Is.EqualTo(valid ? 0.9m : 0.8m));

            // Act
            var actual = await Resolve();

            // Assert
            Assert.That(actual, Is.EqualTo(result));
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task RecoveryKeepsNewerMemoryInsteadOfOlderDurableData(bool olderPublication)
        {
            // Arrange
            // Use the shared fixture and test-case inputs.

            // Act
            var original = await Resolve();

            // Arrange
            _clock.Advance(TimeSpan.FromHours(6));
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).ThrowsAsync(new HttpRequestException());

            // Act
            var actual = (await Resolve()).IsFallback;

            // Assert
            Assert.That(actual, Is.True);

            // Arrange
            _saved = olderPublication
                ? original.Snapshot with { Table = ToStoredTable(_table) with { PublishedAt = _table.PublishedAt.AddHours(-1) }, RetrievedAt = _clock.GetUtcNow() }
                : original.Snapshot with { RetrievedAt = original.Snapshot.RetrievedAt.AddTicks(-1) };
            _clock.Advance(TimeSpan.FromMinutes(2));

            // Act
            var actual2 = await Resolve();

            // Assert
            Assert.That(actual2, Is.EqualTo(new ResolvedRates(original.Snapshot, true)));
            A.CallTo(() => _store.ReadAsync("Test", "EUR", A<CancellationToken>._)).MustHaveHappenedTwiceExactly();
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).MustHaveHappened(3, Times.Exactly);
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task RecoveryUsesNewerDurableSnapshotWithoutProviderCall(bool newerPublication)
        {
            // Arrange
            // Use the shared fixture and test-case inputs.

            // Act
            await Resolve();

            // Arrange
            _clock.Advance(TimeSpan.FromHours(6));
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).ThrowsAsync(new HttpRequestException());

            // Act
            var actual = (await Resolve()).IsFallback;

            // Assert
            Assert.That(actual, Is.True);

            // Arrange
            _saved = new RateSnapshot(ToStoredTable(_table with
            {
                PublishedAt = newerPublication ? _clock.GetUtcNow() : _table.PublishedAt,
                BaseUnitsPerCurrencyUnit = new Dictionary<string, decimal> { ["EUR"] = 1, ["USD"] = 0.9m }.ToFrozenDictionary()
            }), _clock.GetUtcNow());
            _clock.Advance(TimeSpan.FromMinutes(2));

            // Act
            var recovered = await Resolve();

            // Assert
            Assert.That(recovered, Is.EqualTo(new ResolvedRates(_saved, false)));
            Assert.That(recovered.Snapshot.Table.BaseUnitsPerCurrencyUnit["USD"], Is.EqualTo(0.9m));

            // Act
            var actual2 = await Resolve();

            // Assert
            Assert.That(actual2, Is.EqualTo(recovered));
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).MustHaveHappenedTwiceExactly();
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task SharedDeadlineAfterDurableReadDoesNotStartProvider(bool hasFallback)
        {
            // Arrange
            var stored = hasFallback ? new RateSnapshot(ToStoredTable(_table with { PublishedAt = _clock.GetUtcNow().AddHours(-7) }),
                _clock.GetUtcNow().AddHours(-7)) : null;
            A.CallTo(() => _store.ReadAsync("Test", "EUR", A<CancellationToken>._))
                .ReturnsLazily(() =>
                {
                    _clock.Advance(TimeSpan.FromSeconds(30));
                    return Task.FromResult(stored);
                });
            if (hasFallback)
            {
                // Act
                var actual = await Resolve();

                // Assert
                Assert.That(actual, Is.EqualTo(new ResolvedRates(stored!, true)));
            }
            else
            {
                // Act
                AsyncTestDelegate act = () => Resolve();

                // Assert
                Assert.ThrowsAsync<RateUnavailableException>(act);
            }

            // Assert
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).MustNotHaveHappened();
            A.CallTo(() => _store.SaveLatestAsync(A<RateSnapshot>._, A<CancellationToken>._)).MustNotHaveHappened();
        }

        [Test]
        public void QuoteExpiringDuringDurableSaveIsNotReturnedOrCached()
        {
            // Arrange
            _table = _table with { PublishedAt = _clock.GetUtcNow().AddHours(-48).AddSeconds(1) };
            A.CallTo(() => _store.SaveLatestAsync(A<RateSnapshot>._, A<CancellationToken>._))
                .ReturnsLazily((RateSnapshot snapshot, CancellationToken _) =>
                {
                    _clock.Advance(TimeSpan.FromSeconds(1));
                    return Task.FromResult(snapshot);
                });

            // Act
            var failure = Assert.ThrowsAsync<RateUnavailableException>(() => Resolve());

            // Assert
            Assert.That(failure!.InnerException, Is.TypeOf<InvalidDataException>());
            Assert.That(_services.GetRequiredService<IMemoryCache>().Get<RateSnapshot>(("Test", "EUR")), Is.Null);

            // Act
            AsyncTestDelegate act = () => Resolve();

            // Assert
            Assert.ThrowsAsync<RateUnavailableException>(act);
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        }

        [TestCase(true, true)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(false, false)]
        public async Task OptionalStoreFailureStillServesLiveTable(bool read, bool slow)
        {
            // Arrange
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

            // Act
            var pending = Resolve();
            await entered.Task.WaitAsync(Token);
            _clock.Advance(TimeSpan.FromSeconds(8));
            var live = await pending.WaitAsync(Token);

            // Assert
            Assert.That(live.IsFallback, Is.False);
            Assert.That(live.Snapshot.Table, Is.EqualTo(ToStoredTable(_table)));

            // Act
            var actual = await Resolve();

            // Assert
            Assert.That(actual, Is.EqualTo(live));
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        }

        private sealed class StoreException : System.Data.Common.DbException
        {
        }

        [TestCase(429, 20)]
        [TestCase(503, 2)]
        public async Task SequentialOutageMissesRecheckOnceAtRetryAndRecover(int status, int minutes)
        {
            // Arrange
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._))
                .ThrowsAsync(new HttpRequestException("Provider failure", null, (HttpStatusCode)status)).Once().Then.Returns(_table);
            for (var i = 0; i < 4; i++)
            {
                // Act
                AsyncTestDelegate act = () => Resolve();

                // Assert
                Assert.ThrowsAsync<RateUnavailableException>(act);
            }

            // Assert
            A.CallTo(() => _store.ReadAsync("Test", "EUR", A<CancellationToken>._)).MustHaveHappenedOnceExactly();
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).MustHaveHappenedOnceExactly();

            // Arrange
            _clock.Advance(TimeSpan.FromMinutes(minutes) - TimeSpan.FromTicks(1));

            // Act
            AsyncTestDelegate act2 = () => Resolve();

            // Assert
            Assert.ThrowsAsync<RateUnavailableException>(act2);
            A.CallTo(() => _store.ReadAsync("Test", "EUR", A<CancellationToken>._)).MustHaveHappenedOnceExactly();
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).MustHaveHappenedOnceExactly();

            // Arrange
            _clock.Advance(TimeSpan.FromTicks(1));

            // Act
            var result = await Resolve();

            // Assert
            Assert.That(result.Snapshot, Is.EqualTo(_saved));
            Assert.That(result.IsFallback, Is.False);

            // Act
            var actual = await Resolve();

            // Assert
            Assert.That(actual, Is.EqualTo(result));
            A.CallTo(() => _store.ReadAsync("Test", "EUR", A<CancellationToken>._)).MustHaveHappenedTwiceExactly();
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).MustHaveHappenedTwiceExactly();
        }

        [Test]
        public async Task ProviderRecoveryReplacesFallbackAndReusesNewSnapshot()
        {
            // Arrange
            // Use the shared fixture and test-case inputs.

            // Act
            var original = await Resolve();

            // Arrange
            _clock.Advance(TimeSpan.FromHours(6));
            _table = _table with { PublishedAt = _clock.GetUtcNow() };
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._))
                .ThrowsAsync(new HttpRequestException()).Once().Then.Returns(_table);

            // Act
            var actual = (await Resolve()).Snapshot;

            // Assert
            Assert.That(actual, Is.EqualTo(original.Snapshot));

            // Arrange
            _clock.Advance(TimeSpan.FromMinutes(2));

            // Act
            var recovered = await Resolve();

            // Assert
            Assert.That(recovered.IsFallback, Is.False);
            Assert.That(recovered.Snapshot.Table, Is.EqualTo(ToStoredTable(_table)));
            Assert.That(_saved, Is.EqualTo(recovered.Snapshot));

            // Act
            var actual2 = await Resolve();

            // Assert
            Assert.That(actual2, Is.EqualTo(recovered));
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).MustHaveHappened(3, Times.Exactly);
            // A successful recovery clears retry state: the next ordinary refresh need not reread SQL.

            // Arrange
            _clock.Advance(TimeSpan.FromHours(6));
            _table = _table with { PublishedAt = _clock.GetUtcNow() };
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).Returns(_table);

            // Act
            var actual3 = (await Resolve()).IsFallback;

            // Assert
            Assert.That(actual3, Is.False);
            A.CallTo(() => _store.ReadAsync("Test", "EUR", A<CancellationToken>._)).MustHaveHappenedTwiceExactly();
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).MustHaveHappened(4, Times.Exactly);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task CancellationDuringCacheLookupPreventsFastReturn(bool stale)
        {
            // Arrange
            var clock = new LookupClock(_clock.GetUtcNow());
            using var host = CreateHost(clock);
            var resolver = host.GetRequiredService<IRateResolver>();

            // Act
            await resolver.ResolveAsync(Token);
            if (stale)
            {
                // Arrange
                clock.Now += TimeSpan.FromHours(6);
                A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).ThrowsAsync(new HttpRequestException());

                // Act
                var actual = (await resolver.ResolveAsync(Token)).IsFallback;

                // Assert
                Assert.That(actual, Is.True);
            }

            // Arrange
            using var caller = new CancellationTokenSource();
            clock.OnRead = caller.Cancel;

            // Act
            AsyncTestDelegate act = () => resolver.ResolveAsync(caller.Token);

            // Assert
            Assert.CatchAsync<OperationCanceledException>(act);
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
            // Arrange
            _table = _table with { NextUpdateAt = advertised ? _clock.GetUtcNow().AddHours(hours) : null };

            // Act
            var initial = await Resolve();

            // Arrange
            _clock.Advance(TimeSpan.FromHours(hours) - TimeSpan.FromSeconds(1));

            // Act
            var actual = (await Resolve()).IsFallback;

            // Assert
            Assert.That(actual, Is.False);
            Assert.That(_saved, Is.EqualTo(initial.Snapshot));
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).MustHaveHappenedOnceExactly();

            // Arrange
            _clock.Advance(TimeSpan.FromSeconds(1));

            // Act
            var actual2 = (await Resolve()).Snapshot.RetrievedAt;

            // Assert
            Assert.That(actual2, Is.EqualTo(_clock.GetUtcNow()));
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).MustHaveHappenedTwiceExactly();
        }

        [TestCase(-1)]
        [TestCase(1000)]
        public async Task UnusableNextUpdateUsesSixHourSchedule(int hours)
        {
            // Arrange
            _table = _table with { NextUpdateAt = _clock.GetUtcNow().AddHours(hours) };

            // Act
            await Resolve();

            // Arrange
            _clock.Advance(TimeSpan.FromHours(5));

            // Act
            await Resolve();

            // Assert
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).MustHaveHappenedOnceExactly();

            // Arrange
            _clock.Advance(TimeSpan.FromHours(1));

            // Act
            await Resolve();

            // Assert
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).MustHaveHappenedTwiceExactly();
        }

        [TestCase(200)]
        [TestCase(429)]
        [TestCase(503)]
        [TestCase(0)]
        [TestCase(-1)]
        [TestCase(-2)]
        [TestCase(-3)]
        public async Task RefreshFailureReturnsStaleWithinLimitAndSuppressesRetries(int status)
        {
            // Arrange
            // Use the shared fixture and test-case inputs.

            // Act
            var initial = await Resolve();

            // Arrange
            _clock.Advance(TimeSpan.FromHours(6));
            Exception failure = status switch
            {
                -3 => new OperationCanceledException(),
                -2 => new InvalidDataException(),
                -1 => new TimeoutException(),
                0 => new HttpRequestException("429 Too Many Requests is only message text"),
                _ => new HttpRequestException("Provider failure", null, (HttpStatusCode)status)
            };
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).ThrowsAsync(failure);

            // Act
            var actual = await Resolve();

            // Assert
            Assert.That(actual, Is.EqualTo(new ResolvedRates(initial.Snapshot, true)));

            // Act
            var actual2 = (await Resolve()).IsFallback;

            // Assert
            Assert.That(actual2, Is.True);
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).MustHaveHappenedTwiceExactly();

            // Arrange
            var cooldown = TimeSpan.FromMinutes(status == 429 ? 20 : 2);
            _clock.Advance(cooldown - TimeSpan.FromTicks(1));

            // Act
            var actual3 = await Resolve();

            // Assert
            Assert.That(actual3, Is.EqualTo(new ResolvedRates(initial.Snapshot, true)));
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).MustHaveHappenedTwiceExactly();

            // Arrange
            _clock.Advance(TimeSpan.FromTicks(1));

            // Act
            var actual4 = (await Resolve()).IsFallback;

            // Assert
            Assert.That(actual4, Is.True);
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).MustHaveHappened(3, Times.Exactly);
        }

        [TestCase(429, 7)]
        [TestCase(503, 3)]
        public async Task RetryIntervalsHonorIndependentConfiguration(int status, int minutes)
        {
            // Arrange
            using var host = CreateHost(configuration: new Dictionary<string, string?>
            {
                ["RateCache:RetryInterval"] = "00:03:00",
                ["RateCache:RateLimitRetryInterval"] = "00:07:00"
            });
            var resolver = host.GetRequiredService<IRateResolver>();
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._))
                .ThrowsAsync(new HttpRequestException("not a status message", null, (HttpStatusCode)status));

            // Act
            AsyncTestDelegate act = () => resolver.ResolveAsync(Token);

            // Assert
            Assert.ThrowsAsync<RateUnavailableException>(act);

            // Arrange
            _clock.Advance(TimeSpan.FromMinutes(minutes) - TimeSpan.FromTicks(1));

            // Act
            AsyncTestDelegate act2 = () => resolver.ResolveAsync(Token);

            // Assert
            Assert.ThrowsAsync<RateUnavailableException>(act2);
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).MustHaveHappenedOnceExactly();

            // Arrange
            _clock.Advance(TimeSpan.FromTicks(1));

            // Act
            AsyncTestDelegate act3 = () => resolver.ResolveAsync(Token);

            // Assert
            Assert.ThrowsAsync<RateUnavailableException>(act3);
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).MustHaveHappenedTwiceExactly();
        }

        [TestCase("RetryInterval", "00:00:00")]
        [TestCase("RetryInterval", "-00:01:00")]
        [TestCase("RetryInterval", "2.00:00:00")]
        [TestCase("RateLimitRetryInterval", "00:00:00")]
        [TestCase("RateLimitRetryInterval", "-00:01:00")]
        [TestCase("RateLimitRetryInterval", "2.00:00:00")]
        public void RejectsInvalidRetryIntervals(string option, string value)
        {
            // Arrange
            using var host = CreateHost(configuration: new Dictionary<string, string?> { [$"RateCache:{option}"] = value });

            // Act
            TestDelegate act = () => host.GetRequiredService<IOptions<RateCacheOptions>>().Value.ToString();

            // Assert
            Assert.Throws<OptionsValidationException>(act);
        }

        [TestCase(429)]
        [TestCase(503)]
        public async Task CooldownNeverExtendsFallbackPublicationAge(int status)
        {
            // Arrange
            // Use the shared fixture and test-case inputs.

            // Act
            var original = await Resolve();

            // Arrange
            _clock.Advance(TimeSpan.FromHours(48) - TimeSpan.FromMinutes(1));
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._))
                .ThrowsAsync(new HttpRequestException("Provider failure", null, (HttpStatusCode)status));

            // Act
            var actual = await Resolve();

            // Assert
            Assert.That(actual, Is.EqualTo(new ResolvedRates(original.Snapshot, true)));

            // Arrange
            _clock.Advance(TimeSpan.FromMinutes(1));

            // Act
            AsyncTestDelegate act = () => Resolve();

            // Assert
            Assert.ThrowsAsync<RateUnavailableException>(act);
            Assert.That(_saved, Is.EqualTo(original.Snapshot));
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).MustHaveHappenedTwiceExactly();
        }

        [TestCase(429, 20)]
        [TestCase(503, 2)]
        public async Task RetryBoundaryCoalescesCallersAndPreservesIndependentCancellation(int status, int minutes)
        {
            // Arrange
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._))
                .ThrowsAsync(new HttpRequestException("Provider failure", null, (HttpStatusCode)status));

            // Act
            AsyncTestDelegate act = () => Resolve();

            // Assert
            Assert.ThrowsAsync<RateUnavailableException>(act);

            // Arrange
            _clock.Advance(TimeSpan.FromMinutes(minutes));
            var release = new TaskCompletionSource<ExchangeRateTable>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken shared = default;
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._))
                .ReturnsLazily((string _, CancellationToken token) =>
                {
                    shared = token;
                    return release.Task.WaitAsync(token);
                });
            using var caller = new CancellationTokenSource();

            // Act
            var first = _services.GetRequiredService<IRateResolver>().ResolveAsync(caller.Token);
            var others = Enumerable.Range(0, 16).Select(_ => Resolve()).ToArray();
            await caller.CancelAsync();
            AsyncTestDelegate act2 = async () => await first;

            // Assert
            Assert.CatchAsync<OperationCanceledException>(act2);
            Assert.That(shared.IsCancellationRequested, Is.False);

            // Act
            release.SetResult(_table);
            var actual = (await Task.WhenAll(others)).All(r => !r.IsFallback);

            // Assert
            Assert.That(actual, Is.True);
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).MustHaveHappenedTwiceExactly();
            A.CallTo(() => _store.ReadAsync("Test", "EUR", A<CancellationToken>._)).MustHaveHappenedTwiceExactly();
        }

        [Test]
        public async Task InvalidRateCannotReplaceEligibleSnapshot()
        {
            // Arrange
            // Use the shared fixture and test-case inputs.

            // Act
            var initial = await Resolve();

            // Arrange
            _clock.Advance(TimeSpan.FromHours(6));
            _table = _table with { BaseCurrency = "GBP" };

            // Act
            var actual = (await Resolve()).IsFallback;

            // Assert
            Assert.That(actual, Is.True);
            Assert.That(_saved, Is.EqualTo(initial.Snapshot));

            // Arrange
            _clock.Advance(TimeSpan.FromMinutes(2) - TimeSpan.FromTicks(1));

            // Act
            var actual2 = (await Resolve()).IsFallback;

            // Assert
            Assert.That(actual2, Is.True);
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).MustHaveHappenedTwiceExactly();

            // Arrange
            _clock.Advance(TimeSpan.FromTicks(1));

            // Act
            var actual3 = (await Resolve()).IsFallback;

            // Assert
            Assert.That(actual3, Is.True);
            Assert.That(_saved, Is.EqualTo(initial.Snapshot));
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).MustHaveHappened(3, Times.Exactly);
        }

        [Test]
        public async Task RepeatedPublicationExpiresExactlyAtMaximumAge()
        {
            // Arrange
            // Use the shared fixture and test-case inputs.

            // Act
            var first = await Resolve();

            // Arrange
            _clock.Advance(TimeSpan.FromHours(48) - TimeSpan.FromSeconds(1));

            // Act
            var repeated = await Resolve();

            // Assert
            Assert.That(repeated.Snapshot.RetrievedAt, Is.GreaterThan(first.Snapshot.RetrievedAt));
            Assert.That(repeated.Snapshot.Table.PublishedAt, Is.EqualTo(first.Snapshot.Table.PublishedAt));

            // Arrange
            _clock.Advance(TimeSpan.FromSeconds(1));

            // Act
            AsyncTestDelegate act = () => Resolve();

            // Assert
            Assert.ThrowsAsync<RateUnavailableException>(act);
        }

        [Test]
        public async Task FreshDurableSnapshotRecoversAfterEvictionAndRestart()
        {
            // Arrange
            // Use the shared fixture and test-case inputs.

            // Act
            var first = await Resolve();

            // Arrange
            _services.GetRequiredService<IMemoryCache>().Remove(("Test", "EUR"));

            // Act
            var actual = await Resolve();

            // Assert
            Assert.That(actual, Is.EqualTo(first));

            // Arrange
            using var restarted = CreateHost();

            // Act
            var actual2 = await restarted.GetRequiredService<IRateResolver>().ResolveAsync(Token);

            // Assert
            Assert.That(actual2, Is.EqualTo(first));
            A.CallTo(() => _store.ReadAsync("Test", "EUR", A<CancellationToken>._)).MustHaveHappened(3, Times.Exactly);
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task ConcurrentWaitersShareFetchAndCancelIndependently()
        {
            // Arrange
            var release = new TaskCompletionSource<ExchangeRateTable>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken shared = default;
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._))
                .ReturnsLazily((string _, CancellationToken token) =>
                {
                    shared = token;
                    return release.Task.WaitAsync(token);
                });
            using var caller = new CancellationTokenSource();

            // Act
            var first = _services.GetRequiredService<IRateResolver>().ResolveAsync(caller.Token);
            var others = Enumerable.Range(0, 16).Select(_ => Resolve()).ToArray();
            await caller.CancelAsync();
            AsyncTestDelegate act = async () => await first;

            // Assert
            Assert.CatchAsync<OperationCanceledException>(act);
            Assert.That(shared.IsCancellationRequested, Is.False);

            // Act
            release.SetResult(_table);
            var actual = (await Task.WhenAll(others)).All(r => !r.IsFallback);

            // Assert
            Assert.That(actual, Is.True);
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task SharedTimeoutWithoutFallbackIsUnavailableAndSuppressesNextFetch()
        {
            // Arrange
            var never = new TaskCompletionSource<ExchangeRateTable>(TaskCreationOptions.RunContinuationsAsynchronously);
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._))
                .ReturnsLazily((string _, CancellationToken token) => never.Task.WaitAsync(token));

            // Act
            var waiting = Resolve();
            _clock.Advance(TimeSpan.FromSeconds(30));
            AsyncTestDelegate act = async () => await waiting.WaitAsync(Token);

            // Assert
            Assert.ThrowsAsync<RateUnavailableException>(act);

            // Act
            AsyncTestDelegate act2 = () => Resolve();

            // Assert
            Assert.ThrowsAsync<RateUnavailableException>(act2);
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).MustHaveHappenedOnceExactly();

            // Arrange
            _clock.Advance(TimeSpan.FromMinutes(2) - TimeSpan.FromTicks(1));

            // Act
            AsyncTestDelegate act3 = () => Resolve();

            // Assert
            Assert.ThrowsAsync<RateUnavailableException>(act3);
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).MustHaveHappenedOnceExactly();

            // Arrange
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).Returns(_table);
            _clock.Advance(TimeSpan.FromTicks(1));

            // Act
            var actual = (await Resolve()).IsFallback;

            // Assert
            Assert.That(actual, Is.False);
            A.CallTo(() => _provider.GetLatestAsync("EUR", A<CancellationToken>._)).MustHaveHappenedTwiceExactly();
        }
    }
}
