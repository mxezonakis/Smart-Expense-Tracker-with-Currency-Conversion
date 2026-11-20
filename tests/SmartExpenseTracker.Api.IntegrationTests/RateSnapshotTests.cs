using System.Collections.Frozen;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SmartExpenseTracker.Application.ExchangeRates;
using SmartExpenseTracker.Persistence;
using Testcontainers.MsSql;

namespace SmartExpenseTracker.Api.IntegrationTests
{
    [NUnit.Framework.Category("SqlServer")]
    [CancelAfter(120000)]
    public sealed class RateSnapshotTests
    {
        private MsSqlContainer? _server;
        private string _connection = null!;
        private static CancellationToken Token => TestContext.CurrentContext.CancellationToken;
        private static DateTimeOffset Published => DateTimeOffset.UnixEpoch.AddYears(54);

        [OneTimeSetUp]
        public async Task StartDatabase()
        {
            _server = SqlServerTestDatabase.Create();
            await _server.StartAsync(Token);
        }

        [SetUp]
        public async Task ApplyMigrations()
        {
            _connection = new SqlConnectionStringBuilder(_server!.GetConnectionString())
            {
                InitialCatalog = $"SmartExpenseTracker_{Guid.NewGuid():N}"
            }.ConnectionString;
            await using var host = CreateHost(Published);
            await using var scope = host.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<SmartExpenseTrackerDbContext>().Database;
            await database.MigrateAsync(Token);
            Assert.That(await database.GetAppliedMigrationsAsync(Token), Does.Contain("20261120155857_AddRateSnapshots"));
        }

        [OneTimeTearDown]
        public async Task StopDatabase()
        {
            if (_server != null)
            {
                await _server.DisposeAsync();
            }
        }

        [Test]
        public async Task ConcurrentWritersKeepNewestCompleteTable()
        {
            await using var host = CreateHost(Published.AddHours(8));
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task Write(int version)
            {
                await using var scope = host.Services.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<IRateSnapshotStore>();
                var snapshot = CreateSnapshot(version);
                await gate.Task.WaitAsync(Token);
                await store.SaveLatestAsync(snapshot, Token);
            }

            var writes = Enumerable.Range(0, 8).Select(Write).ToArray();
            gate.SetResult();
            await Task.WhenAll(writes);
            await Write(0);

            await using var verification = host.Services.CreateAsyncScope();
            var table = await verification.ServiceProvider.GetRequiredService<IRateSnapshotStore>().ReadAsync("ExchangeRate-API", "EUR", Token);
            Assert.That(table!.Table.PublishedAt, Is.EqualTo(Published.AddHours(7)));
            Assert.That(table.Table.BaseUnitsPerCurrencyUnit["USD"], Is.EqualTo(8m));
            Assert.That(table.Table.BaseUnitsPerCurrencyUnit["GBP"], Is.EqualTo(16.12345678901234567890123456m));
        }

        [TestCase("{}", 0)]
        [TestCase("{\"EUR\":1,\"USD\":2}", 72)]
        [TestCase("{\"EUR\":1,\"USD\":2,\"USD\":3}", 0)]
        public async Task LiveProviderRepairsInvalidRowForOfflineRestart(string rates, int futureHours)
        {
            await using (var host = CreateHost(Published, true))
            {
                await using var scope = host.Services.CreateAsyncScope();
                var database = scope.ServiceProvider.GetRequiredService<SmartExpenseTrackerDbContext>().Database;
                var stamp = Published.AddHours(futureHours);
                await database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO RateSnapshots (Provider, BaseCurrency, PublishedAt, RetrievedAt, Rates)
                    VALUES ('ExchangeRate-API', 'EUR', {stamp}, {stamp}, {rates})
                    """, Token);
                var store = scope.ServiceProvider.GetRequiredService<IRateSnapshotStore>();
                Assert.That(await store.ReadAsync("ExchangeRate-API", "EUR", Token), Is.Null);
                var live = await host.Services.GetRequiredService<IRateResolver>().ResolveAsync(Token);
                Assert.That(live.IsFallback, Is.False);
                Assert.That(live.Snapshot.Table.PublishedAt, Is.EqualTo(Published));
            }

            await using var restarted = CreateHost(Published.AddMinutes(5));
            var restored = await restarted.Services.GetRequiredService<IRateResolver>().ResolveAsync(Token);
            Assert.That(restored.IsFallback, Is.False);
            Assert.That(restored.Snapshot.Table.BaseUnitsPerCurrencyUnit["USD"], Is.EqualTo(1m));
        }

        [Test]
        public async Task RejectedSqlInsertStillServesLiveTableFromMemory()
        {
            await using var host = CreateHost(Published, true);
            await using var scope = host.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<SmartExpenseTrackerDbContext>().Database;
            await database.ExecuteSqlRawAsync("ALTER TABLE RateSnapshots ADD CHECK (BaseCurrency <> 'EUR')", Token);
            var resolver = host.Services.GetRequiredService<IRateResolver>();
            var live = await resolver.ResolveAsync(Token);
            Assert.That(live.IsFallback, Is.False);
            Assert.That((await resolver.ResolveAsync(Token)).Snapshot, Is.SameAs(live.Snapshot));
            var store = scope.ServiceProvider.GetRequiredService<IRateSnapshotStore>();
            Assert.That(await store.ReadAsync("ExchangeRate-API", "EUR", Token), Is.Null);
        }

        [Test]
        public async Task RepairWaitsForConcurrentWriterAndPreservesNewerRetrievalAndExactDecimal()
        {
            await using var host = CreateHost(Published.AddHours(1));
            await using var scope = host.Services.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IRateSnapshotStore>();
            var database = scope.ServiceProvider.GetRequiredService<SmartExpenseTrackerDbContext>().Database;
            await database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO RateSnapshots (Provider, BaseCurrency, PublishedAt, RetrievedAt, Rates)
                VALUES ('ExchangeRate-API', 'EUR', {Published}, {Published}, {"{}"})
                """, Token);
            Assert.That(await store.ReadAsync("ExchangeRate-API", "EUR", Token), Is.Null);

            var newer = Published.AddMinutes(1);
            const decimal precise = 0.1234567890123456789012345678m;
            const string json = """{"EUR":1,"USD":0.1234567890123456789012345678}""";
            await using var writer = new SqlConnection(_connection);
            await writer.OpenAsync(Token);
            await using var transaction = (SqlTransaction)await writer.BeginTransactionAsync(Token);
            await using var update = writer.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE RateSnapshots SET RetrievedAt = @retrieved, Rates = @rates";
            update.Parameters.AddWithValue("@retrieved", newer);
            update.Parameters.AddWithValue("@rates", json);
            await update.ExecuteNonQueryAsync(Token);

            var repair = store.SaveLatestAsync(CreateSnapshot(0), Token);
            try
            {
                await using var observer = new SqlConnection(_connection);
                await observer.OpenAsync(Token);
                await using var blocked = observer.CreateCommand();
                blocked.CommandText = "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE blocking_session_id = @writer";
                blocked.Parameters.AddWithValue("@writer", writer.ServerProcessId);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                while (Convert.ToInt32(await blocked.ExecuteScalarAsync(timeout.Token)) == 0)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                }

                await transaction.CommitAsync(Token);
            }
            finally
            {
                if (transaction.Connection != null)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }

                await repair;
            }

            var winner = await store.ReadAsync("ExchangeRate-API", "EUR", Token);
            Assert.That(winner!.RetrievedAt, Is.EqualTo(newer));
            Assert.That(winner.Table.BaseUnitsPerCurrencyUnit["USD"], Is.EqualTo(precise));
        }

        [TestCase(47, true)]
        [TestCase(48, false)]
        public async Task RestartDuringOutageUsesOnlyUnexpiredPublication(int hours, bool available)
        {
            await using (var initial = CreateHost(Published))
            {
                await using var scope = initial.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IRateSnapshotStore>().SaveLatestAsync(CreateSnapshot(0), Token);
            }

            await using var restarted = CreateHost(Published.AddHours(hours));
            var resolver = restarted.Services.GetRequiredService<IRateResolver>();
            if (available)
            {
                var result = await resolver.ResolveAsync(Token);
                Assert.That(result.IsFallback, Is.True);
                Assert.That(result.Snapshot.Table.PublishedAt.EqualsExact(Published), Is.True);
                Assert.That(result.Snapshot.RetrievedAt, Is.EqualTo(Published));
            }
            else
            {
                Assert.ThrowsAsync<RateUnavailableException>(() => resolver.ResolveAsync(Token));
            }
        }

        private static RateSnapshot CreateSnapshot(int version)
        {
            var rates = new Dictionary<string, decimal> { ["EUR"] = 1, ["USD"] = version + 1, ["GBP"] = (version + 1) * 2 + 0.12345678901234567890123456m };
            var published = Published.AddHours(version);
            return new RateSnapshot(new ExchangeRateTable("ExchangeRate-API", "EUR", published, rates.ToFrozenDictionary()), published);
        }

        private WebApplicationFactory<Program> CreateHost(DateTimeOffset now, bool online = false)
        {
            return new RateTestHost(_connection, now, online);
        }

        private sealed class RateTestHost : WebApplicationFactory<Program>
        {
            private readonly string _connection;
            private readonly DateTimeOffset _now;

            private readonly bool _online;

            public RateTestHost(string connection, DateTimeOffset now, bool online)
            {
                _online = online;
                _connection = connection;
                _now = now;
            }

            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["ConnectionStrings:SmartExpenseTracker"] = _connection }));
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton<TimeProvider>(new FixedClock(_now));
                    services.AddSingleton<IExchangeRateProvider>(_ => _online ? new HealthyProvider() : new OfflineProvider());
                });
            }
        }

        private sealed class FixedClock : TimeProvider
        {
            private readonly DateTimeOffset _now;

            public FixedClock(DateTimeOffset now)
            {
                _now = now;
            }

            public override DateTimeOffset GetUtcNow() => _now;
        }

        private sealed class HealthyProvider : IExchangeRateProvider
        {
            public Task<ExchangeRateTable> GetLatestAsync(string baseCurrency, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(CreateSnapshot(0).Table);
            }
        }

        private sealed class OfflineProvider : IExchangeRateProvider
        {
            public Task<ExchangeRateTable> GetLatestAsync(string baseCurrency, CancellationToken cancellationToken)
            {
                throw new HttpRequestException("Simulated outage.");
            }
        }
    }
}
