using System.Collections.Frozen;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SmartExpenseTracker.Api.Contracts;
using SmartExpenseTracker.Caching;
using SmartExpenseTracker.Integrations;
using SmartExpenseTracker.Persistence;
using SmartExpenseTracker.Persistence.ExchangeRates;
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
            // Arrange
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

            // Act
            var writes = Enumerable.Range(0, 8).Select(Write).ToArray();
            gate.SetResult();
            await Task.WhenAll(writes);
            await Write(0);

            // Assert
            await using var verification = host.Services.CreateAsyncScope();
            var table = await verification.ServiceProvider.GetRequiredService<IRateSnapshotStore>().ReadAsync("ExchangeRate-API", "EUR", Token);
            Assert.That(table!.Table.PublishedAt, Is.EqualTo(Published.AddHours(7)));
            Assert.That(table.Table.BaseUnitsPerCurrencyUnit["USD"], Is.EqualTo(8m));
            Assert.That(table.Table.BaseUnitsPerCurrencyUnit["GBP"], Is.EqualTo(16.12345678901234567890123456m));
        }

        [TestCase("{}", 0)]
        [TestCase("{\"EUR\":1,\"USD\":0}", 0)]
        [TestCase("{\"EUR\":1,\"USD\":2}", 72)]
        [TestCase("{\"EUR\":1,\"USD\":2,\"USD\":3}", 0)]
        public async Task LiveProviderRepairsInvalidRowForOfflineRestart(string rates, int futureHours)
        {
            // Arrange
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

                // Act
                var actual = await store.ReadAsync("ExchangeRate-API", "EUR", Token);

                // Assert
                Assert.That(actual, Is.Null);

                // Act
                var live = await host.Services.GetRequiredService<IRateResolver>().ResolveAsync(Token);

                // Assert
                Assert.That(live.IsFallback, Is.False);
                Assert.That(live.Snapshot.Table.PublishedAt, Is.EqualTo(Published));
            }

            await using var restarted = CreateHost(Published.AddMinutes(5));

            // Act
            var restored = await restarted.Services.GetRequiredService<IRateResolver>().ResolveAsync(Token);

            // Assert
            Assert.That(restored.IsFallback, Is.False);
            Assert.That(restored.Snapshot.Table.BaseUnitsPerCurrencyUnit["USD"], Is.EqualTo(1m));
        }

        [Test]
        public async Task RejectedSqlInsertStillServesLiveTableFromMemory()
        {
            // Arrange
            await using var host = CreateHost(Published, true);
            await using var scope = host.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<SmartExpenseTrackerDbContext>().Database;
            await database.ExecuteSqlRawAsync("ALTER TABLE RateSnapshots ADD CHECK (BaseCurrency <> 'EUR')", Token);
            var resolver = host.Services.GetRequiredService<IRateResolver>();

            // Act
            var live = await resolver.ResolveAsync(Token);

            // Assert
            Assert.That(live.IsFallback, Is.False);

            // Act
            var actual = (await resolver.ResolveAsync(Token)).Snapshot;

            // Assert
            Assert.That(actual, Is.SameAs(live.Snapshot));
            var store = scope.ServiceProvider.GetRequiredService<IRateSnapshotStore>();
            var actual2 = await store.ReadAsync("ExchangeRate-API", "EUR", Token);

            Assert.That(actual2, Is.Null);
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task MixedPublicationPreservesExpenseProvenanceAcrossRestartsAndWriteOrder(bool olderFirst)
        {
            // Arrange
            var older = CreateSnapshot(0);
            var latest = CreateSnapshot(47);
            latest = latest with
            {
                Table = latest.Table with
                {
                    BaseUnitsPerCurrencyUnit = new Dictionary<string, decimal>
                    {
                        ["EUR"] = 1m,
                        ["USD"] = 1000000m,
                        ["VND"] = 0.5m
                    }.ToFrozenDictionary()
                }
            };
            await using (var writer = CreateHost(latest.RetrievedAt))
            {
                await using var scope = writer.Services.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<IRateSnapshotStore>();
                await store.SaveLatestAsync(olderFirst ? older : latest, Token);
                await store.SaveLatestAsync(olderFirst ? latest : older, Token);
            }

            int categoryId;
            ExpenseRequest Input(string currency, int category) => new ExpenseRequest(10m, currency, category, new DateOnly(2030, 1, 2), null);

            // Act: the host has no memory cache and its provider is offline.
            await using (var restarted = CreateHost(latest.RetrievedAt))
            {
                using var client = restarted.CreateClient();
                using var category = await client.PostAsJsonAsync("/api/categories", new { name = "Travel" }, Token);
                category.EnsureSuccessStatusCode();
                categoryId = (await category.Content.ReadFromJsonAsync<CategoryResponse>(Token))!.Id;
                using var usd = await client.PostAsJsonAsync("/api/expenses", Input("USD", categoryId), Token);
                usd.EnsureSuccessStatusCode();
                var expense = (await usd.Content.ReadFromJsonAsync<ExpenseResponse>(Token))!;

                // Assert
                Assert.That(expense.IsFallback, Is.True);
                Assert.That(expense.PublishedAt, Is.EqualTo(older.Table.PublishedAt));
                Assert.That(expense.AppliedRate, Is.EqualTo(1m));
                Assert.That(expense.ConvertedAmount, Is.EqualTo(10m));
            }

            // Act: only the retained USD publication reaches the 48-hour cutoff.
            await using var atExpiry = CreateHost(Published.AddHours(48));
            using var next = atExpiry.CreateClient();
            using var expired = await next.PostAsJsonAsync("/api/expenses", Input("USD", categoryId), Token);
            using var vnd = await next.PostAsJsonAsync("/api/expenses", Input("VND", categoryId), Token);

            // Assert
            Assert.That(expired.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(expired.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/problem+json"));
            vnd.EnsureSuccessStatusCode();
            var fresh = (await vnd.Content.ReadFromJsonAsync<ExpenseResponse>(Token))!;
            Assert.That(fresh.IsFallback, Is.False);
            Assert.That(fresh.PublishedAt, Is.EqualTo(latest.Table.PublishedAt));
            Assert.That(fresh.ConvertedAmount, Is.EqualTo(5m));
        }

        [Test]
        public async Task RepairWaitsForConcurrentWriterAndPreservesNewerRetrievalAndExactDecimal()
        {
            // Arrange
            await using var host = CreateHost(Published.AddHours(1));
            await using var scope = host.Services.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IRateSnapshotStore>();
            var database = scope.ServiceProvider.GetRequiredService<SmartExpenseTrackerDbContext>().Database;
            await database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO RateSnapshots (Provider, BaseCurrency, PublishedAt, RetrievedAt, Rates)
                VALUES ('ExchangeRate-API', 'EUR', {Published}, {Published}, {"{}"})
                """, Token);

            // Act
            var actual = await store.ReadAsync("ExchangeRate-API", "EUR", Token);

            // Assert
            Assert.That(actual, Is.Null);

            // Arrange
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

            // Act
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

            // Assert
            Assert.That(winner!.RetrievedAt, Is.EqualTo(newer));
            Assert.That(winner.Table.BaseUnitsPerCurrencyUnit["USD"], Is.EqualTo(precise));
        }

        [TestCase(47, true)]
        [TestCase(48, false)]
        public async Task RestartDuringOutageUsesOnlyUnexpiredPublication(int hours, bool available)
        {
            // Arrange
            await using (var initial = CreateHost(Published))
            {
                await using var scope = initial.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IRateSnapshotStore>().SaveLatestAsync(CreateSnapshot(0), Token);
            }

            await using var restarted = CreateHost(Published.AddHours(hours));
            var resolver = restarted.Services.GetRequiredService<IRateResolver>();
            if (available)
            {
                // Act
                var result = await resolver.ResolveAsync(Token);

                // Assert
                Assert.That(result.IsFallback, Is.True);
                Assert.That(result.Snapshot.Table.PublishedAt.EqualsExact(Published), Is.True);
                Assert.That(result.Snapshot.RetrievedAt, Is.EqualTo(Published));
            }
            else
            {
                // Act
                AsyncTestDelegate act = () => resolver.ResolveAsync(Token);

                // Assert
                Assert.ThrowsAsync<RateUnavailableException>(act);
            }
        }

        [Test]
        public async Task InvalidCandidateCannotReplacePersistedSnapshot()
        {
            // Arrange
            await using var host = CreateHost(Published.AddHours(1));
            var original = CreateSnapshot(0);
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var store = scope.ServiceProvider.GetRequiredService<IRateSnapshotStore>();
                await store.SaveLatestAsync(original, Token);
                var invalid = original with
                {
                    Table = original.Table with
                    {
                        BaseUnitsPerCurrencyUnit = new Dictionary<string, decimal> { ["EUR"] = 1m, ["USD"] = 0m }.ToFrozenDictionary()
                    }
                };

                // Act
                AsyncTestDelegate act = () => store.SaveLatestAsync(invalid, Token);

                // Assert
                Assert.ThrowsAsync<InvalidDataException>(act);
            }

            // Assert
            await using var verification = host.Services.CreateAsyncScope();
            var saved = await verification.ServiceProvider.GetRequiredService<IRateSnapshotStore>()
                .ReadAsync("ExchangeRate-API", "EUR", Token);
            Assert.That(saved, Is.Not.Null);
            Assert.That(saved!.RetrievedAt, Is.EqualTo(original.RetrievedAt));
            Assert.That(saved.Table.PublishedAt, Is.EqualTo(original.Table.PublishedAt));
            Assert.That(saved.Table.BaseUnitsPerCurrencyUnit, Is.EquivalentTo(original.Table.BaseUnitsPerCurrencyUnit));
        }

        [Test]
        public async Task EqualPublicationUsesNewestRetrievalAndPreservesScheduleAndExactRates()
        {
            // Arrange
            await using var host = CreateHost(Published.AddHours(1));
            var first = CreateSnapshot(0);
            var newer = first with
            {
                RetrievedAt = Published.AddMinutes(1),
                Table = first.Table with
                {
                    NextUpdateAt = Published.AddDays(1),
                    BaseUnitsPerCurrencyUnit = new Dictionary<string, decimal>
                    {
                        ["EUR"] = 1m,
                        ["USD"] = 0.1234567890123456789012345678m
                    }.ToFrozenDictionary()
                }
            };
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var store = scope.ServiceProvider.GetRequiredService<IRateSnapshotStore>();

                // Act
                await store.SaveLatestAsync(first, Token);
                await store.SaveLatestAsync(newer, Token);
                foreach (var retrieval in new[] { first.RetrievedAt, newer.RetrievedAt })
                {
                    // Arrange
                    var loser = first with { RetrievedAt = retrieval };

                    // Act
                    var winner = await store.SaveLatestAsync(loser, Token);

                    // Assert
                    Assert.That(winner.RetrievedAt, Is.EqualTo(newer.RetrievedAt));
                    Assert.That(winner.Table.NextUpdateAt, Is.EqualTo(newer.Table.NextUpdateAt));
                    Assert.That(winner.Table.BaseUnitsPerCurrencyUnit, Is.EquivalentTo(newer.Table.BaseUnitsPerCurrencyUnit));
                }
            }

            // Assert
            await using var verification = host.Services.CreateAsyncScope();
            var saved = await verification.ServiceProvider.GetRequiredService<IRateSnapshotStore>()
                .ReadAsync("ExchangeRate-API", "EUR", Token);
            Assert.That(saved!.RetrievedAt, Is.EqualTo(newer.RetrievedAt));
            Assert.That(saved.Table.NextUpdateAt, Is.EqualTo(newer.Table.NextUpdateAt));
            Assert.That(saved.Table.BaseUnitsPerCurrencyUnit, Is.EquivalentTo(newer.Table.BaseUnitsPerCurrencyUnit));
        }

        [TestCase("exchangeRate-API", "EUR")]
        [TestCase("ExchangeRate-API", "eur")]
        public async Task CaseInsensitiveSqlMatchDoesNotReturnDifferentSnapshotKey(string provider, string currency)
        {
            // Arrange
            await using var host = CreateHost(Published);
            await using var scope = host.Services.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IRateSnapshotStore>();
            await store.SaveLatestAsync(CreateSnapshot(0), Token);
            var context = scope.ServiceProvider.GetRequiredService<SmartExpenseTrackerDbContext>();

            // Act
            var matches = await context.Database.SqlQuery<int>($"""
                SELECT COUNT(*) AS [Value] FROM RateSnapshots
                WHERE Provider = {provider} AND BaseCurrency = {currency}
                """).SingleAsync(Token);

            // Assert
            Assert.That(matches, Is.EqualTo(1), "SQL matches the key case-insensitively; decoding must still reject the different key.");

            // Act
            var actual = await store.ReadAsync(provider, currency, Token);

            // Assert
            Assert.That(actual, Is.Null);

            // Act
            var original = await store.ReadAsync("ExchangeRate-API", "EUR", Token);

            // Assert
            Assert.That(original, Is.Not.Null);
            Assert.That(original!.Table.Provider, Is.EqualTo("ExchangeRate-API"));
            Assert.That(original.Table.BaseCurrency, Is.EqualTo("EUR"));
            Assert.That(original.Table.BaseUnitsPerCurrencyUnit["USD"], Is.EqualTo(1m));
        }

        private static RateSnapshot CreateSnapshot(int version)
        {
            var rates = new Dictionary<string, decimal> { ["EUR"] = 1, ["USD"] = version + 1, ["GBP"] = (version + 1) * 2 + 0.12345678901234567890123456m };
            var published = Published.AddHours(version);
            return new RateSnapshot(new StoredExchangeRateTable("ExchangeRate-API", "EUR", published, rates.ToFrozenDictionary()), published);
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
                var stored = CreateSnapshot(0).Table;
                return Task.FromResult(new ExchangeRateTable(stored.Provider, stored.BaseCurrency, stored.PublishedAt,
                    stored.BaseUnitsPerCurrencyUnit, stored.NextUpdateAt));
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
