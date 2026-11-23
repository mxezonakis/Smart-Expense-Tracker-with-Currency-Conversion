using System.Collections.Frozen;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SmartExpenseTracker.Api.Contracts;
using SmartExpenseTracker.Application.ExchangeRates;
using SmartExpenseTracker.Persistence;
using Testcontainers.MsSql;

namespace SmartExpenseTracker.Api.IntegrationTests
{
    [NUnit.Framework.Category("SqlServer")]
    public sealed class ExpenseApiTests
    {
        private const string _route = "/api/expenses";
        private MsSqlContainer _server = null!;
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private ControlledRates _rates = null!;
        private string _connection = null!;
        private int _category;
        private static CancellationToken Token => TestContext.CurrentContext.CancellationToken;

        [OneTimeSetUp]
        public async Task StartServer()
        {
            _server = SqlServerTestDatabase.Create();
            await _server.StartAsync(Token);
        }

        [SetUp]
        public async Task MigrateDatabase()
        {
            _connection = new SqlConnectionStringBuilder(_server.GetConnectionString())
            {
                InitialCatalog = $"SmartExpenseTracker_{Guid.NewGuid():N}",
                CurrentLanguage = "German"
            }.ConnectionString;
            _rates = new ControlledRates();
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
                .ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:SmartExpenseTracker"] = _connection
                }))
                .ConfigureServices(services => services.AddSingleton<IRateResolver>(_rates)));
            _client = _factory.CreateClient();
            using var scope = _factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<SmartExpenseTrackerDbContext>();
            await context.Database.MigrateAsync(Token);
            var language = await context.Database.SqlQuery<string>($"SELECT @@LANGUAGE AS [Value]").SingleAsync(Token);
            language.Should().Be("us_english");
            (await context.Database.GetAppliedMigrationsAsync(Token)).Should().Contain(
                new[] { "20261120180100_AddExpenses", "20261120210000_AddExpenseQueryIndexes" });
            using var created = await _client.PostAsJsonAsync("/api/categories", new { name = "Travel" }, Token);
            created.EnsureSuccessStatusCode();
            _category = (await created.Content.ReadFromJsonAsync<CategoryResponse>(Token))!.Id;
        }

        [TearDown]
        public async Task DisposeHost()
        {
            _client.Dispose();
            await _factory.DisposeAsync();
        }

        [OneTimeTearDown]
        public async Task DisposeServer()
        {
            await _server.DisposeAsync();
        }

        private ExpenseRequest Input(string currency) => new ExpenseRequest(10m, currency, _category, new DateOnly(2030, 1, 2), null);

        private async Task<HttpResponseMessage> PutExpenseAsync(HttpClient client, Uri? uri, ExpenseRequest input, CancellationToken token)
        {
            using var current = await _client.GetAsync(uri, token);
            current.EnsureSuccessStatusCode();
            return await SendConditionalAsync(client, HttpMethod.Put, uri, input, current.Headers.ETag!.ToString(), token);
        }

        private async Task<HttpResponseMessage> DeleteExpenseAsync(HttpClient client, Uri? uri, CancellationToken token)
        {
            using var current = await _client.GetAsync(uri, token);
            current.EnsureSuccessStatusCode();
            return await SendConditionalAsync(client, HttpMethod.Delete, uri, null, current.Headers.ETag!.ToString(), token);
        }

        private static async Task<HttpResponseMessage> SendConditionalAsync(HttpClient client, HttpMethod method, Uri? uri,
            ExpenseRequest? input, string etag, CancellationToken token)
        {
            using var request = new HttpRequestMessage(method, uri);
            request.Headers.TryAddWithoutValidation("If-Match", etag);
            if (input is not null)
            {
                request.Content = JsonContent.Create(input);
            }

            return await client.SendAsync(request, token);
        }

        [Test]
        public async Task PostAndGetReturnExactPersistedEtagsWithoutExtraExpenseReads()
        {
            // Arrange
            var observer = new UpdateCommandObserver();
            await using var observed = ObserveUpdates(observer);
            using var client = observed.CreateClient();

            // Act
            using var created = await client.PostAsJsonAsync(_route, Input("EUR"), Token);

            // Assert
            created.StatusCode.Should().Be(HttpStatusCode.Created);
            var expense = (await created.Content.ReadFromJsonAsync<ExpenseResponse>(Token))!;
            var tag = created.Headers.ETag!;
            tag.IsWeak.Should().BeFalse();
            tag.Tag.Should().Be($"\"{Convert.ToBase64String(await ReadVersionAsync(expense.Id))}\"");
            observer.Commands.Should().NotContain(sql => sql.Contains("FROM [Expenses]", StringComparison.Ordinal));

            // Arrange
            observer.Commands.Clear();

            // Act
            using var first = await client.GetAsync(created.Headers.Location, Token);
            using var second = await _client.GetAsync(created.Headers.Location, Token);

            // Assert
            first.StatusCode.Should().Be(HttpStatusCode.OK);
            first.Headers.ETag.Should().Be(tag);
            second.Headers.ETag.Should().Be(tag);
            var actual = (await first.Content.ReadFromJsonAsync<ExpenseResponse>(Token));

            actual.Should().Be(expense);
            observer.Commands.Should().ContainSingle().Which.Should().Contain("[Version]").And.Contain("FROM [Expenses]");

            // Act
            using var list = await _client.GetAsync(_route, Token);

            // Assert
            list.Headers.ETag.Should().BeNull();
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task StaleClientCannotOverwriteOrDeleteNewerExpense(bool noOp)
        {
            // Arrange
            var input = Input("USD");

            // Act
            using var created = await _client.PostAsJsonAsync(_route, input, Token);

            // Assert
            created.EnsureSuccessStatusCode();

            // Act
            using var clientARead = await _client.GetAsync(created.Headers.Location, Token);
            using var clientBRead = await _client.GetAsync(created.Headers.Location, Token);

            // Assert
            clientARead.Headers.ETag.Should().Be(clientBRead.Headers.ETag);
            var staleTag = clientARead.Headers.ETag!.ToString();

            // Act
            using var changed = await SendConditionalAsync(_client, HttpMethod.Put, created.Headers.Location,
                input with { Amount = 20m }, clientBRead.Headers.ETag!.ToString(), Token);

            // Assert
            changed.StatusCode.Should().Be(HttpStatusCode.NoContent);
            changed.Headers.ETag!.ToString().Should().NotBe(staleTag);

            // Arrange
            var winner = (await _client.GetFromJsonAsync<ExpenseResponse>(created.Headers.Location, Token))!;
            var version = await ReadVersionAsync(winner.Id);
            var calls = _rates.Calls;
            _rates.Available = false;
            var observer = new UpdateCommandObserver();
            await using var observed = ObserveUpdates(observer);
            using var client = observed.CreateClient();

            // Act
            using var stalePut = await SendConditionalAsync(client, HttpMethod.Put, created.Headers.Location,
                input with { Amount = noOp ? 20m : 10m }, staleTag, Token);

            // Assert
            await AssertProblem(stalePut, HttpStatusCode.PreconditionFailed);
            observer.Commands.Should().ContainSingle().Which.Should().Contain("FROM [Expenses]");

            // Arrange
            observer.Commands.Clear();

            // Act
            using var staleDelete = await SendConditionalAsync(client, HttpMethod.Delete, created.Headers.Location, null, staleTag, Token);

            // Assert
            await AssertProblem(staleDelete, HttpStatusCode.PreconditionFailed);
            observer.Commands.Should().ContainSingle().Which.Should().Contain("DELETE").And.Contain("[Version]");
            _rates.Calls.Should().Be(calls);

            // Act
            var actual = (await _client.GetFromJsonAsync<ExpenseResponse>(created.Headers.Location, Token));

            // Assert
            actual.Should().Be(winner);
            var actual2 = (await ReadVersionAsync(winner.Id));

            actual2.Should().Equal(version);
        }

        [Test]
        public async Task ConditionalDeleteUsesClientVersionAtomicallyWithoutReadingExpense()
        {
            // Arrange
            // Use the shared fixture and test-case inputs.

            // Act
            using var created = await _client.PostAsJsonAsync(_route, Input("EUR"), Token);

            // Assert
            created.EnsureSuccessStatusCode();
            var expense = (await created.Content.ReadFromJsonAsync<ExpenseResponse>(Token))!;
            var version = await ReadVersionAsync(expense.Id);

            // Arrange
            var observer = new UpdateCommandObserver();
            await using var observed = ObserveUpdates(observer);
            using var client = observed.CreateClient();

            // Act
            using var deleted = await SendConditionalAsync(client, HttpMethod.Delete, created.Headers.Location, null,
                created.Headers.ETag!.ToString(), Token);

            // Assert
            deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
            observer.Commands.Should().ContainSingle().Which.Should().Contain("DELETE").And.Contain("[Id]").And.Contain("[Version]");
            observer.DeleteVersion.Should().Equal(version);
            TestContext.Out.WriteLine(observer.Commands.Single());

            // Act
            using var missing = await _client.GetAsync(created.Headers.Location, Token);

            // Assert
            await AssertProblem(missing, HttpStatusCode.NotFound);

            // Act
            using var repeat = await SendConditionalAsync(client, HttpMethod.Delete, created.Headers.Location, null,
                created.Headers.ETag.ToString(), Token);

            // Assert
            await AssertProblem(repeat, HttpStatusCode.PreconditionFailed);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task CompetingConditionalWritesAllowOnlyOneWinner(bool deleteCompetitor)
        {
            // Arrange
            var input = Input("EUR");

            // Act
            using var created = await _client.PostAsJsonAsync(_route, input, Token);

            // Assert
            created.EnsureSuccessStatusCode();
            var etag = created.Headers.ETag!.ToString();
            var version = Convert.FromBase64String(etag[1..^1]);

            // Arrange
            var bothReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var arrived = 0;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            async Task Gate(CancellationToken token)
            {
                if (Interlocked.Increment(ref arrived) == 2)
                {
                    bothReady.SetResult();
                }

                await bothReady.Task.WaitAsync(token);
            }

            var firstObserver = new UpdateCommandObserver { BeforeUpdate = Gate };
            var secondObserver = new UpdateCommandObserver { BeforeUpdate = Gate, BeforeDelete = Gate };
            await using var firstHost = ObserveUpdates(firstObserver);
            await using var secondHost = ObserveUpdates(secondObserver);
            using var firstClient = firstHost.CreateClient();
            using var secondClient = secondHost.CreateClient();
            var firstInput = input with { Description = "First writer" };
            var secondInput = input with { Description = "Second writer" };

            // Act
            var firstTask = SendConditionalAsync(firstClient, HttpMethod.Put, created.Headers.Location, firstInput, etag, deadline.Token);
            var secondTask = SendConditionalAsync(secondClient, deleteCompetitor ? HttpMethod.Delete : HttpMethod.Put,
                created.Headers.Location, deleteCompetitor ? null : secondInput, etag, deadline.Token);
            await Task.WhenAll(firstTask, secondTask);
            using var first = await firstTask;
            using var second = await secondTask;

            // Assert
            new[] { first.StatusCode, second.StatusCode }.Should().BeEquivalentTo(new[] { HttpStatusCode.NoContent, HttpStatusCode.PreconditionFailed });
            firstObserver.AssertSingleReadAndVersion(version);
            if (deleteCompetitor)
            {
                secondObserver.Commands.Should().ContainSingle().Which.Should().Contain("DELETE");
                secondObserver.DeleteVersion.Should().Equal(version);
            }
            else
            {
                secondObserver.AssertSingleReadAndVersion(version);
            }

            if (deleteCompetitor && second.IsSuccessStatusCode)
            {
                // Act
                using var missing = await _client.GetAsync(created.Headers.Location, Token);

                // Assert
                await AssertProblem(missing, HttpStatusCode.NotFound);
            }
            else
            {
                // Act
                var persisted = await _client.GetFromJsonAsync<ExpenseResponse>(created.Headers.Location, Token);

                // Assert
                persisted!.Description.Should().Be(first.IsSuccessStatusCode ? "First writer" : "Second writer");
                persisted.Amount.Should().Be(10m);
                persisted.ConvertedAmount.Should().Be(10m);
            }
        }

        [Test]
        public async Task MatchingVersionStillReturnsConflictForDifferentDatasetBase()
        {
            // Arrange
            // Use the shared fixture and test-case inputs.

            // Act
            using var created = await _client.PostAsJsonAsync(_route, Input("EUR"), Token);

            // Assert
            created.EnsureSuccessStatusCode();
            var before = (await created.Content.ReadFromJsonAsync<ExpenseResponse>(Token))!;

            // Arrange
            await using var changed = WithBaseCurrency("GBP");
            using var client = changed.CreateClient();

            // Act
            using var response = await SendConditionalAsync(client, HttpMethod.Put, created.Headers.Location, Input("EUR"),
                created.Headers.ETag!.ToString(), Token);

            // Assert
            await AssertProblem(response, HttpStatusCode.Conflict);

            // Act
            var actual = (await _client.GetFromJsonAsync<ExpenseResponse>(created.Headers.Location, Token));

            // Assert
            actual.Should().Be(before);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task UpdateReadsExpenseOnceAndWritesOnlyEffectiveChanges(bool noOp)
        {
            // Arrange
            var input = Input("USD");

            // Act
            using var created = await _client.PostAsJsonAsync(_route, input, Token);

            // Assert
            created.EnsureSuccessStatusCode();
            var original = (await created.Content.ReadFromJsonAsync<ExpenseResponse>(Token))!;
            var version = await ReadVersionAsync(original.Id);

            // Arrange
            var observer = new UpdateCommandObserver();
            await using var observed = ObserveUpdates(observer);
            using var client = observed.CreateClient();
            var replacement = noOp ? input : input with { Description = "Updated", Date = input.Date.AddDays(1) };

            // Act
            using var response = await PutExpenseAsync(client, created.Headers.Location, replacement, Token);

            // Assert
            response.StatusCode.Should().Be(noOp ? HttpStatusCode.OK : HttpStatusCode.NoContent);
            TestContext.Out.WriteLine(string.Join("\n---\n", observer.Commands));
            var updatedVersion = await ReadVersionAsync(original.Id);
            if (noOp)
            {
                await AssertUnchanged(response, version);
                observer.AssertReadOnly();
                updatedVersion.Should().Equal(version);
            }
            else
            {
                observer.AssertSingleReadAndVersion(version);
                updatedVersion.Should().NotEqual(version);
                response.Headers.ETag!.Tag.Should().Be($"\"{Convert.ToBase64String(updatedVersion)}\"");
                observer.Commands.Single(sql => sql.Contains("UPDATE [Expenses]", StringComparison.Ordinal))
                    .Should().NotContain("SET [Amount]");
            }

            // Act
            var actual = (await _client.GetFromJsonAsync<ExpenseResponse>(created.Headers.Location, Token));

            // Assert
            actual.Should()
                .Be(original with { Description = replacement.Description, Date = replacement.Date });
            _rates.Calls.Should().Be(1);
        }

        [TestCase(false, false, false)]
        [TestCase(false, false, true)]
        [TestCase(false, true, false)]
        [TestCase(false, true, true)]
        [TestCase(true, false, false)]
        [TestCase(true, false, true)]
        [TestCase(true, true, false)]
        [TestCase(true, true, true)]
        public async Task PutNeverOverwritesConcurrentChangesAndNoOpDoesNotRecheck(bool duringPersistence, bool delete, bool noOp)
        {
            // Arrange
            var input = Input("EUR");

            // Act
            using var created = await _client.PostAsJsonAsync(_route, input, Token);

            // Assert
            created.EnsureSuccessStatusCode();
            var original = (await created.Content.ReadFromJsonAsync<ExpenseResponse>(Token))!;
            var version = await ReadVersionAsync(original.Id);

            // Arrange
            var competitor = input with { Description = "Committed competitor", Amount = 25m };
            var competed = false;
            async Task Compete(CancellationToken token)
            {
                using var response = delete
                    ? await DeleteExpenseAsync(_client, created.Headers.Location, token)
                    : await PutExpenseAsync(_client, created.Headers.Location, competitor, token);
                response.StatusCode.Should().Be(HttpStatusCode.NoContent);
                competed = true;
            }

            var observer = new UpdateCommandObserver();
            if (duringPersistence)
            {
                observer.BeforeUpdate = Compete;
            }
            else
            {
                observer.BeforeCategoryValidation = Compete;
            }

            await using var observed = ObserveUpdates(observer);
            using var client = observed.CreateClient();

            // Act
            using var stale = await PutExpenseAsync(client, created.Headers.Location,
                noOp ? input : input with { Description = "Stale writer" }, Token);

            // Assert
            if (noOp)
            {
                await AssertUnchanged(stale, version);
                observer.AssertReadOnly();
                competed.Should().Be(!duringPersistence, "a no-op never reaches the SQL UPDATE interception point");
                if (duringPersistence)
                {
                    // Act
                    var actual = (await ReadVersionAsync(original.Id));

                    // Assert
                    actual.Should().Equal(version);

                    // Act
                    var actual2 = (await _client.GetFromJsonAsync<ExpenseResponse>(created.Headers.Location, Token));

                    // Assert
                    actual2.Should().Be(original);
                    return;
                }
            }
            else
            {
                await AssertProblem(stale, HttpStatusCode.PreconditionFailed);
                competed.Should().BeTrue();
                observer.AssertSingleReadAndVersion(version);
            }
            if (delete)
            {
                // Act
                using var missing = await _client.GetAsync(created.Headers.Location, Token);

                // Assert
                await AssertProblem(missing, HttpStatusCode.NotFound);
            }
            else
            {
                // Act
                var actual3 = (await _client.GetFromJsonAsync<ExpenseResponse>(created.Headers.Location, Token));

                // Assert
                actual3.Should().Be(original with
                {
                    Description = competitor.Description,
                    Amount = 25m,
                    ConvertedAmount = 25m
                });
            }
        }

        [Test]
        public async Task UpdateKeepsInitialVersionAcrossRateResolutionEvenWhenValuesAreRestored()
        {
            // Arrange
            var input = Input("USD");

            // Act
            using var created = await _client.PostAsJsonAsync(_route, input, Token);

            // Assert
            created.EnsureSuccessStatusCode();
            var original = (await created.Content.ReadFromJsonAsync<ExpenseResponse>(Token))!;
            var version = await ReadVersionAsync(original.Id);

            // Arrange
            var competed = false;

            // Assert
            _rates.BeforeQuote = async token =>
            {
                using var changed = await PutExpenseAsync(_client, created.Headers.Location,
                    input with { Description = "Temporary change" }, token);
                changed.StatusCode.Should().Be(HttpStatusCode.NoContent);
                using var restored = await PutExpenseAsync(_client, created.Headers.Location, input, token);
                restored.StatusCode.Should().Be(HttpStatusCode.NoContent);
                competed = true;
            };

            // Arrange
            var observer = new UpdateCommandObserver();
            await using var observed = ObserveUpdates(observer);
            using var client = observed.CreateClient();

            // Act
            using var response = await PutExpenseAsync(client, created.Headers.Location, input with { Amount = 20m }, Token);

            // Assert
            await AssertProblem(response, HttpStatusCode.PreconditionFailed);
            competed.Should().BeTrue();
            observer.AssertSingleReadAndVersion(version);

            // Act
            var actual = (await _client.GetFromJsonAsync<ExpenseResponse>(created.Headers.Location, Token));

            // Assert
            actual.Should().Be(original);
            var actual2 = (await ReadVersionAsync(original.Id));

            actual2.Should().NotEqual(version);
            _rates.Calls.Should().Be(2);
        }

        [TestCase("amount", HttpStatusCode.BadRequest)]
        [TestCase("category", HttpStatusCode.BadRequest)]
        [TestCase("rates", HttpStatusCode.ServiceUnavailable)]
        public async Task FailedUpdateDoesNotWriteOrAdvanceVersion(string failure, HttpStatusCode status)
        {
            // Arrange
            var input = Input("USD");

            // Act
            using var created = await _client.PostAsJsonAsync(_route, input, Token);

            // Assert
            created.EnsureSuccessStatusCode();
            var original = (await created.Content.ReadFromJsonAsync<ExpenseResponse>(Token))!;
            var version = await ReadVersionAsync(original.Id);

            // Arrange
            _rates.Available = false;
            var observer = new UpdateCommandObserver();
            await using var observed = ObserveUpdates(observer);
            using var client = observed.CreateClient();
            var replacement = failure switch
            {
                "amount" => input with { Amount = -1m },
                "category" => input with { CategoryId = int.MaxValue },
                _ => input with { Amount = 20m }
            };

            // Act
            using var response = await PutExpenseAsync(client, created.Headers.Location, replacement, Token);

            // Assert
            await AssertProblem(response, status);
            observer.Commands.Should().NotContain(sql => sql.Contains("UPDATE [Expenses]", StringComparison.Ordinal));
            var actual = (await ReadVersionAsync(original.Id));

            actual.Should().Equal(version);

            // Act
            var actual2 = (await _client.GetFromJsonAsync<ExpenseResponse>(created.Headers.Location, Token));

            // Assert
            actual2.Should().Be(original);
        }

        [Test]
        public async Task UpdateMissingAtInitialReadReturnsNotFoundWithoutWriting()
        {
            // Arrange
            var observer = new UpdateCommandObserver();
            await using var observed = ObserveUpdates(observer);
            using var client = observed.CreateClient();

            // Act
            using var response = await SendConditionalAsync(client, HttpMethod.Put, new Uri($"{_route}/{int.MaxValue}", UriKind.Relative), Input("USD"), "\"AAAAAAAAAAA=\"", Token);

            // Assert
            await AssertProblem(response, HttpStatusCode.NotFound);
            observer.Commands.Should().ContainSingle().Which.Should().Contain("FROM [Expenses]");
            _rates.Calls.Should().Be(0);
        }

        private WebApplicationFactory<Program> ObserveUpdates(UpdateCommandObserver observer)
        {
            return _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                services.ConfigureDbContext<SmartExpenseTrackerDbContext>(options => options.AddInterceptors(observer))));
        }

        private async Task<byte[]> ReadVersionAsync(int id)
        {
            await using var connection = new SqlConnection(_connection);
            await connection.OpenAsync(Token);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Version FROM dbo.Expenses WHERE Id = @id";
            command.Parameters.AddWithValue("@id", id);
            return (byte[])(await command.ExecuteScalarAsync(Token))!;
        }

        private static async Task AssertUnchanged(HttpResponseMessage response, byte[] version)
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var body = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
            body.RootElement.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo("updated", "reason");
            body.RootElement.GetProperty("updated").GetBoolean().Should().BeFalse();
            body.RootElement.GetProperty("reason").GetString().Should().Be("unchanged");
            response.Headers.ETag!.Tag.Should().Be($"\"{Convert.ToBase64String(version)}\"");
        }

        [Test]
        public async Task NormalizedEquivalentPutDoesNotWriteOrAdvanceVersion()
        {
            // Arrange
            var input = Input("USD");

            // Act
            using var created = await _client.PostAsJsonAsync(_route, input, Token);

            // Assert
            created.EnsureSuccessStatusCode();
            var original = (await created.Content.ReadFromJsonAsync<ExpenseResponse>(Token))!;
            var version = await ReadVersionAsync(original.Id);

            // Arrange
            var observer = new UpdateCommandObserver();
            await using var observed = ObserveUpdates(observer);
            using var client = observed.CreateClient();
            _rates.Available = false;

            // Act
            using var response = await PutExpenseAsync(client, created.Headers.Location,
                input with { Amount = 10.000m, Currency = " uSd ", Description = "   " }, Token);

            // Assert
            await AssertUnchanged(response, version);
            observer.AssertReadOnly();
            var actual = (await ReadVersionAsync(original.Id));

            actual.Should().Equal(version);

            // Act
            var actual2 = (await _client.GetFromJsonAsync<ExpenseResponse>(created.Headers.Location, Token));

            // Assert
            actual2.Should().Be(original);
            _rates.Calls.Should().Be(1);
            TestContext.Out.WriteLine(string.Join("\n---\n", observer.Commands));
        }

        private sealed class UpdateCommandObserver : DbCommandInterceptor
        {
            internal List<string> Commands { get; } = [];
            internal byte[]? UpdateVersion { get; private set; }
            internal byte[]? DeleteVersion { get; private set; }
            internal Func<CancellationToken, Task>? BeforeCategoryValidation { get; set; }
            internal Func<CancellationToken, Task>? BeforeUpdate { get; set; }
            internal Func<CancellationToken, Task>? BeforeDelete { get; set; }

            public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
                CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
            {
                Commands.Add(command.CommandText);
                if (command.CommandText.Contains("DELETE", StringComparison.Ordinal) &&
                    command.CommandText.Contains("[Expenses]", StringComparison.Ordinal))
                {
                    var parameter = command.Parameters.Cast<DbParameter>().Single(p => p.Value is byte[]);
                    DeleteVersion = ((byte[])parameter.Value!).ToArray();
                    command.CommandText.Should().Contain($"[Version] = {parameter.ParameterName}");
                    if (BeforeDelete is { } delete)
                    {
                        BeforeDelete = null;
                        await delete(cancellationToken);
                    }
                }

                return result;
            }

            public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
                CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
            {
                Commands.Add(command.CommandText);
                if (command.CommandText.Contains("FROM [Categories]", StringComparison.Ordinal) && BeforeCategoryValidation is { } validate)
                {
                    BeforeCategoryValidation = null;
                    await validate(cancellationToken);
                }

                if (command.CommandText.Contains("UPDATE [Expenses]", StringComparison.Ordinal))
                {
                    var parameter = command.Parameters.Cast<DbParameter>().Single(p => p.Value is byte[]);
                    UpdateVersion = ((byte[])parameter.Value!).ToArray();
                    command.CommandText.Should().Contain($"[Version] = {parameter.ParameterName}");
                    if (BeforeUpdate is { } update)
                    {
                        BeforeUpdate = null;
                        await update(cancellationToken);
                    }
                }

                return result;
            }

            internal void AssertReadOnly()
            {
                Commands.Should().HaveCount(2);
                Commands.Count(sql => sql.Contains("FROM [Expenses]", StringComparison.Ordinal)).Should().Be(1);
                Commands.Count(sql => sql.Contains("FROM [Categories]", StringComparison.Ordinal)).Should().Be(1);
                Commands.Should().OnlyContain(sql => sql.TrimStart().StartsWith("SELECT", StringComparison.Ordinal));
                UpdateVersion.Should().BeNull();
            }

            internal void AssertSingleReadAndVersion(byte[] expected)
            {
                Commands.Count(sql => sql.Contains("FROM [Expenses]", StringComparison.Ordinal)).Should().Be(1);
                Commands.Count(sql => sql.Contains("UPDATE [Expenses]", StringComparison.Ordinal)).Should().Be(1);
                UpdateVersion.Should().Equal(expected);
            }
        }

        [Test]
        public async Task SqlLikeCategoryAndDescriptionRoundTripAsLiteralText()
        {
            // Arrange
            const string literal = "O'Brien; SELECT N'fare' --";

            // Act
            using var created = await _client.PostAsJsonAsync("/api/categories", new { name = literal }, Token);

            // Assert
            created.StatusCode.Should().Be(HttpStatusCode.Created);
            var category = (await created.Content.ReadFromJsonAsync<CategoryResponse>(Token))!;

            // Act
            using var expense = await _client.PostAsJsonAsync(_route, Input("EUR") with { CategoryId = category.Id, Description = literal }, Token);

            // Assert
            expense.StatusCode.Should().Be(HttpStatusCode.Created);

            // Act
            var stored = await _client.GetFromJsonAsync<ExpenseResponse>(expense.Headers.Location, Token);

            // Assert
            stored!.Description.Should().Be(literal);
            stored.CategoryId.Should().Be(category.Id);

            // Act
            var categories = await _client.GetFromJsonAsync<CategoryResponse[]>("/api/categories", Token);

            // Assert
            categories.Should().BeEquivalentTo(new[] { new CategoryResponse(_category, "Travel"), new CategoryResponse(category.Id, literal) });
        }

        [Test]
        public async Task MigratedExpensesTableHasExpectedQueryIndexKeys()
        {
            // Arrange
            using var scope = _factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<SmartExpenseTrackerDbContext>();

            // Act
            var keys = await context.Database.SqlQuery<string>($"""
                SELECT CONCAT(i.name, '.', c.name) AS [Value]
                FROM sys.indexes AS i
                JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE i.object_id = OBJECT_ID(N'dbo.Expenses', N'U')
                  AND i.name IN ('IX_Expenses_Date', 'IX_Expenses_Currency', 'IX_Expenses_ConvertedAmount')
                  AND ic.key_ordinal > 0
                """).ToListAsync(Token);

            // Assert
            keys.Should().BeEquivalentTo("IX_Expenses_Date.Date", "IX_Expenses_Currency.Currency",
                "IX_Expenses_ConvertedAmount.ConvertedAmount");
        }

        [Test]
        public async Task ListCombinesInclusiveFiltersAndSortsStoredValues()
        {
            // Arrange
            async Task<int> Add(ExpenseRequest expense)
            {
                using var response = await _client.PostAsJsonAsync(_route, expense, Token);
                response.EnsureSuccessStatusCode();
                return (await response.Content.ReadFromJsonAsync<ExpenseResponse>(Token))!.Id;
            }

            async Task<int[]> Ids(string query)
            {
                var rows = await _client.GetFromJsonAsync<ExpenseResponse[]>($"{_route}?{query}", Token);
                return rows!.Select(row => row.Id).ToArray();
            }

            // Act
            using var createCategory = await _client.PostAsJsonAsync("/api/categories", new { name = "Other" }, Token);

            // Assert
            createCategory.EnsureSuccessStatusCode();
            int otherCategory = (await createCategory.Content.ReadFromJsonAsync<CategoryResponse>(Token))!.Id;

            // Arrange
            var usd = Input("USD");
            int first = await Add(usd);
            int second = await Add(usd);
            int euro = await Add(Input("EUR") with { Amount = 9.50m });
            int later = await Add(usd with { Date = usd.Date.AddDays(1) });
            int other = await Add(usd with { CategoryId = otherCategory });
            _rates.Available = false;

            // Act
            var usdIds = await Ids("currency=USD");

            // Assert
            usdIds.Should().Equal(first, second, later, other);

            // Act
            var actual = (await Ids("currency=%20uSd%20"));

            // Assert
            actual.Should().Equal(usdIds);

            // Act
            using var malformed = await _client.GetAsync($"{_route}?currency=%20US1%20", Token);

            // Assert
            await AssertProblem(malformed, HttpStatusCode.BadRequest);

            // Arrange
            string filters = $"from=2030-01-02&to=2030-01-02&categoryId={_category}&currency=usd";

            // Act
            var actual2 = (await Ids(filters));

            // Assert
            actual2.Should().Equal(first, second);

            // Act
            var actual3 = (await Ids("from=2030-01-03"));

            // Assert
            actual3.Should().Equal(later);

            // Act
            var actual4 = (await Ids("to=2030-01-01"));

            // Assert
            actual4.Should().BeEmpty();

            // Act
            var actual5 = (await Ids("sortBy=amount&direction=asc"));

            // Assert
            actual5.Should().Equal(first, second, later, other, euro);

            // Act
            var actual6 = (await Ids("sortBy=amount&direction=desc"));

            // Assert
            actual6.Should().Equal(euro, first, second, later, other);

            // Act
            var actual7 = (await Ids("sortBy=date&direction=asc"));

            // Assert
            actual7.Should().Equal(first, second, euro, other, later);

            // Act
            var actual8 = (await Ids("sortBy=date"));

            // Assert
            actual8.Should().Equal(later, first, second, euro, other);

            // Act
            var actual9 = (await Ids(""));

            // Assert
            actual9.Should().Equal(first, second, euro, later, other);
            _rates.Calls.Should().Be(4);
        }

        [Test]
        public async Task ListReturnsEveryMatchingExpenseInIdOrder()
        {
            // Arrange
            var ids = new List<int>();
            for (int index = 0; index < 101; index++)
            {
                // Act
                using var created = await _client.PostAsJsonAsync(_route, Input("EUR"), Token);

                // Assert
                created.EnsureSuccessStatusCode();

                // Arrange
                ids.Add((await created.Content.ReadFromJsonAsync<ExpenseResponse>(Token))!.Id);
            }

            // Act
            var matches = await _client.GetFromJsonAsync<ExpenseResponse[]>($"{_route}?categoryId={_category}&currency=EUR", Token);

            // Assert
            matches!.Select(row => row.Id).Should().Equal(ids);
            _rates.Calls.Should().Be(0);
        }

        [Test]
        public async Task ReportsSumPersistedSnapshotsInSqlAcrossCalendarBoundaries()
        {
            // Arrange
            // Use the shared fixture and test-case inputs.

            // Act
            using var category = await _client.PostAsJsonAsync("/api/categories", new { name = "Other" }, Token);

            // Assert
            category.EnsureSuccessStatusCode();
            int other = (await category.Content.ReadFromJsonAsync<CategoryResponse>(Token))!.Id;

            // Arrange
            var january = new DateOnly(2028, 1, 1);
            var sunday = new DateOnly(2028, 12, 31);
            var monday = sunday.AddDays(1);
            var saturday = sunday.AddDays(-1);

            // Insert in reverse chronological order. CHF 0.01 at 0.5 rounds half-even to EUR 0.00.
            foreach (var expense in new[]
            {
                Input("EUR") with { Date = monday, Amount = 8m },
                Input("EUR") with { Date = sunday, Amount = 1m },
                Input("EUR") with { Date = saturday, Amount = 1m },
                Input("USD") with { Date = sunday },
                Input("CHF") with { Date = january, Amount = 0.01m, CategoryId = other }
            })
            {
                // Act
                using var result = await _client.PostAsJsonAsync(_route, expense, Token);

                // Assert
                result.EnsureSuccessStatusCode();
            }

            var before = await _client.GetFromJsonAsync<ExpenseResponse[]>(_route, Token);
            _rates.Available = false;
            int calls = _rates.Calls;
            var commands = new System.Collections.Concurrent.ConcurrentQueue<string>();
            using var reportHost = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                services.ConfigureDbContext<SmartExpenseTrackerDbContext>(options => options.LogTo(commands.Enqueue,
                    new[] { RelationalEventId.CommandExecuted }))));
            using var reports = reportHost.CreateClient();

            // Act
            var categories = await reports.GetFromJsonAsync<CategoryTotalResponse[]>("/api/reports/by-category", Token);

            // Assert
            categories!.Should().Equal(new CategoryTotalResponse(_category, 19.26m, "EUR"), new CategoryTotalResponse(other, 0m, "EUR"));

            // Arrange
            async Task CheckPeriods(string period, params PeriodTotalResponse[] expected)
            {
                var totals = await reports.GetFromJsonAsync<PeriodTotalResponse[]>($"/api/reports/by-period?period={period}", Token);
                totals!.Should().Equal(expected);
            }

            // Act
            await CheckPeriods("day", new(january, 0m, "EUR"), new(saturday, 1m, "EUR"),
                new(sunday, 10.26m, "EUR"), new(monday, 8m, "EUR"));
            await CheckPeriods("week", new(new DateOnly(2027, 12, 27), 0m, "EUR"),
                new(new DateOnly(2028, 12, 25), 11.26m, "EUR"), new(monday, 8m, "EUR"));
            await CheckPeriods("month", new(january, 0m, "EUR"), new(new DateOnly(2028, 12, 1), 11.26m, "EUR"), new(monday, 8m, "EUR"));

            // Assert
            commands.Should().HaveCount(4);
            foreach (var command in commands)
            {
                command.Should().Contain("GROUP BY").And.Contain("SUM(").And.Contain("ConvertedAmount").And.Contain("ORDER BY");
            }

            // Act
            var actual = (await _client.GetFromJsonAsync<ExpenseResponse[]>(_route, Token))!;

            // Assert
            actual.Should().Equal(before!);
            _rates.Calls.Should().Be(calls);
        }

        [Test]
        public async Task ReportsReturnEmptyArraysForEmptyDataset()
        {
            // Arrange
            foreach (string route in new[] { "by-category", "by-period?period=day", "by-period?period=week", "by-period?period=month" })
            {
                // Act
                using var response = await _client.GetAsync($"/api/reports/{route}", Token);

                // Assert
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                var actual = (await response.Content.ReadFromJsonAsync<object[]>(Token));

                actual.Should().BeEmpty();
            }

            // Act
            using var invalid = await _client.GetAsync("/api/reports/by-period?period=year", Token);

            // Assert
            await AssertProblem(invalid, HttpStatusCode.BadRequest);
            _rates.Calls.Should().Be(0);
        }

        [Test]
        public async Task IdentityCrudAndReferencedCategoryRestriction()
        {
            // Arrange
            var input = Input(" eur ");

            // Act
            using var created = await _client.PostAsJsonAsync(_route, input, Token);

            // Assert
            created.StatusCode.Should().Be(HttpStatusCode.Created);
            var location = created.Headers.Location!;
            var item = (await created.Content.ReadFromJsonAsync<ExpenseResponse>(Token))!;
            location.AbsolutePath.Should().Be($"{_route}/{item.Id}");
            item.Currency.Should().Be("EUR");
            item.ConvertedAmount.Should().Be(10m);
            item.AppliedRate.Should().Be(1m);
            item.Provider.Should().BeNull();
            item.PublishedAt.Should().BeNull();
            item.Description.Should().BeNull();

            // Act
            var actual = (await _client.GetFromJsonAsync<ExpenseResponse[]>(_route, Token))!;

            // Assert
            actual.Should().Equal(item);

            // Act
            using var blocked = await _client.DeleteAsync($"/api/categories/{_category}", Token);

            // Assert
            await AssertProblem(blocked, HttpStatusCode.Conflict);

            // Act
            using var updated = await PutExpenseAsync(_client, location, input with { Description = "  Train  " }, Token);

            // Assert
            updated.StatusCode.Should().Be(HttpStatusCode.NoContent);

            // Act
            var actual2 = (await _client.GetFromJsonAsync<ExpenseResponse>(location, Token))!.Description;

            // Assert
            actual2.Should().Be("Train");

            // Act
            using var deleted = await DeleteExpenseAsync(_client, location, Token);

            // Assert
            deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);

            // Act
            using var missing = await _client.GetAsync(location, Token);

            // Assert
            await AssertProblem(missing, HttpStatusCode.NotFound);

            // Act
            using var categoryDeleted = await _client.DeleteAsync($"/api/categories/{_category}", Token);

            // Assert
            categoryDeleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
            _rates.Calls.Should().Be(0);
        }

        [Test]
        public async Task StoredDecimalsAndProvenanceSurviveOutageAndMetadataEdits()
        {
            // Arrange
            var input = Input("usd");

            // Act
            using var created = await _client.PostAsJsonAsync(_route, input, Token);

            // Assert
            created.StatusCode.Should().Be(HttpStatusCode.Created);
            var location = created.Headers.Location!;
            var first = (await created.Content.ReadFromJsonAsync<ExpenseResponse>(Token))!;
            first.AppliedRate.Should().Be(0.925500000001m);
            first.ConvertedAmount.Should().Be(9.26m);
            first.Provider.Should().Be("Test");
            first.IsFallback.Should().BeTrue();
            first.PublishedAt.Should().Be(ControlledRates.Publication);

            await using var connection = new SqlConnection(_connection);
            await connection.OpenAsync(Token);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Amount, AppliedRate, ConvertedAmount FROM Expenses WHERE Id = @id";
            command.Parameters.AddWithValue("@id", first.Id);
            await using (var reader = await command.ExecuteReaderAsync(Token))
            {
                var actual = (await reader.ReadAsync(Token));

                actual.Should().BeTrue();
                reader.GetDecimal(0).Should().Be(10m);
                reader.GetDecimal(1).Should().Be(0.925500000001m);
                reader.GetDecimal(2).Should().Be(9.26m);
            }

            // Arrange
            _rates.Available = false;
            var metadata = input with { Description = "Ticket", Date = input.Date.AddYears(-10) };

            // Act
            using var updated = await PutExpenseAsync(_client, location, metadata, Token);

            // Assert
            updated.StatusCode.Should().Be(HttpStatusCode.NoContent);
            _rates.Calls.Should().Be(1);

            // Arrange
            var expected = first with { Description = metadata.Description, Date = metadata.Date };

            // Act
            var actual2 = (await _client.GetFromJsonAsync<ExpenseResponse>(location, Token));

            // Assert
            actual2.Should().Be(expected);

            // Act
            using var failed = await PutExpenseAsync(_client, location, input with { Amount = 20m }, Token);

            // Assert
            await AssertProblem(failed, HttpStatusCode.ServiceUnavailable);

            // Act
            var actual3 = (await _client.GetFromJsonAsync<ExpenseResponse>(location, Token));

            // Assert
            actual3.Should().Be(expected);

            // Arrange
            _rates.Available = true;

            // Act
            using var repriced = await PutExpenseAsync(_client, location, input with { Currency = "GBP" }, Token);

            // Assert
            repriced.StatusCode.Should().Be(HttpStatusCode.NoContent);

            // Act
            var latest = (await _client.GetFromJsonAsync<ExpenseResponse>(location, Token))!;

            // Assert
            latest.AppliedRate.Should().Be(1.25m);
            latest.ConvertedAmount.Should().Be(12.50m);
            _rates.Calls.Should().Be(3);
        }

        [Test]
        public async Task UnknownCurrencyMissingCategoryAndUnavailableQuotesLeaveNoExpenses()
        {
            // Arrange
            // Use the shared fixture and test-case inputs.

            // Act
            using var invalid = await _client.PostAsJsonAsync(_route, Input("ZZZ"), Token);

            // Assert
            await AssertProblem(invalid, HttpStatusCode.BadRequest);

            // Act
            using var missing = await _client.PostAsJsonAsync(_route, Input("EUR") with { CategoryId = int.MaxValue }, Token);

            // Assert
            await AssertProblem(missing, HttpStatusCode.BadRequest);

            // Arrange
            _rates.Available = false;

            // Act
            using var unavailable = await _client.PostAsJsonAsync(_route, Input("GBP"), Token);

            // Assert
            await AssertProblem(unavailable, HttpStatusCode.ServiceUnavailable);

            // Act
            var actual = (await _client.GetFromJsonAsync<ExpenseResponse[]>(_route, Token))!;

            // Assert
            actual.Should().BeEmpty();
        }

        [Test]
        public async Task CategoryDeletedDuringRateResolutionLeavesNoExpense()
        {
            // Arrange
            // Use the shared fixture and test-case inputs.

            // Act
            var actual = (await ReadDatasetAsync());

            // Assert
            actual.Should().BeEmpty();

            // Arrange
            var observer = new FirstExpenseWriteObserver();
            await using var observed = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                services.ConfigureDbContext<SmartExpenseTrackerDbContext>(options => options.AddInterceptors(observer))));
            using var observedClient = observed.CreateClient();
            var categoryDeleted = false;

            // Assert
            _rates.BeforeQuote = async token =>
            {
                using var removed = await _client.DeleteAsync($"/api/categories/{_category}", token);
                removed.StatusCode.Should().Be(HttpStatusCode.NoContent);
                categoryDeleted = true;
            };

            // Act
            using var create = await observedClient.PostAsJsonAsync(_route, Input("USD"), Token);

            // Assert
            create.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            create.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
            _rates.Calls.Should().Be(1);
            categoryDeleted.Should().BeTrue();

            // Act
            using var missing = await _client.GetAsync($"/api/categories/{_category}", Token);

            // Assert
            missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
            var validation = await create.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ValidationProblemDetails>(Token);
            validation!.Status.Should().Be(400);
            validation.Title.Should().NotBeNullOrWhiteSpace();
            validation.Detail.Should().BeNull();
            validation.Errors.Should().ContainKey("categoryId").WhoseValue.Should().NotBeEmpty();
            observer.PendingBase.Should().Be("EUR");
            observer.FailedTransaction.Should().NotBeNull().And.Be(observer.PendingTransaction);
            observer.SqlError.Should().Be(547);

            // Act
            var actual2 = (await _client.GetFromJsonAsync<ExpenseResponse[]>(_route, Token))!;

            // Assert
            actual2.Should().BeEmpty();
            var actual3 = (await ReadDatasetAsync());

            actual3.Should().BeEmpty();

            // Arrange
            await using var changed = WithBaseCurrency("GBP");
            using var client = changed.CreateClient();

            // Act
            using var category = await client.PostAsJsonAsync("/api/categories", new { name = "Meals" }, Token);

            // Assert
            category.EnsureSuccessStatusCode();
            var replacementCategory = (await category.Content.ReadFromJsonAsync<CategoryResponse>(Token))!.Id;

            // Act
            using var retry = await client.PostAsJsonAsync(_route, Input("GBP") with { CategoryId = replacementCategory }, Token);

            // Assert
            retry.StatusCode.Should().Be(HttpStatusCode.Created);
            var actual4 = (await ReadDatasetAsync());

            actual4.Should().Equal("GBP");
        }

        [Test]
        public async Task ReassignedCategoryDeletedDuringPutLeavesOriginalExpenseAndVersionUnchanged()
        {
            // Arrange
            // Use the shared fixture and test-case inputs.

            // Act
            using var created = await _client.PostAsJsonAsync(_route, Input("EUR"), Token);

            // Assert
            created.EnsureSuccessStatusCode();
            var original = (await created.Content.ReadFromJsonAsync<ExpenseResponse>(Token))!;
            var version = await ReadVersionAsync(original.Id);

            // Act
            using var category = await _client.PostAsJsonAsync("/api/categories", new { name = "Replacement" }, Token);

            // Assert
            category.EnsureSuccessStatusCode();
            var replacementCategory = (await category.Content.ReadFromJsonAsync<CategoryResponse>(Token))!.Id;
            _rates.BeforeQuote = async token =>
            {
                using var removed = await _client.DeleteAsync(category.Headers.Location, token);
                removed.StatusCode.Should().Be(HttpStatusCode.NoContent);
            };

            // Act
            using var updated = await SendConditionalAsync(_client, HttpMethod.Put, created.Headers.Location,
                Input("USD") with { CategoryId = replacementCategory, Description = "Must not persist" },
                created.Headers.ETag!.ToString(), Token);

            // Assert
            updated.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            updated.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
            var problem = await updated.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ValidationProblemDetails>(Token);
            problem!.Status.Should().Be(400);
            problem.Detail.Should().BeNull();
            problem.Errors.Should().ContainKey("categoryId");
            _rates.Calls.Should().Be(1);

            // Act
            var actual = (await _client.GetFromJsonAsync<ExpenseResponse>(created.Headers.Location, Token));

            // Assert
            actual.Should().Be(original);
            var actual2 = (await ReadVersionAsync(original.Id));

            actual2.Should().Equal(version);

            // Act
            using var removedCategory = await _client.GetAsync(category.Headers.Location, Token);

            // Assert
            removedCategory.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Test]
        public async Task UnrelatedExpenseUniqueConstraintIsNotReportedAsMissingCategory()
        {
            // Arrange
            var firstInput = Input("EUR") with { Description = "First" };
            var secondInput = Input("EUR") with { Description = "Second" };

            // Act
            using var firstCreated = await _client.PostAsJsonAsync(_route, firstInput, Token);
            using var secondCreated = await _client.PostAsJsonAsync(_route, secondInput, Token);

            // Assert
            firstCreated.EnsureSuccessStatusCode();
            secondCreated.EnsureSuccessStatusCode();
            var first = (await firstCreated.Content.ReadFromJsonAsync<ExpenseResponse>(Token))!;
            var second = (await secondCreated.Content.ReadFromJsonAsync<ExpenseResponse>(Token))!;
            var firstVersion = await ReadVersionAsync(first.Id);
            var secondVersion = await ReadVersionAsync(second.Id);

            // Arrange
            using (var scope = _factory.Services.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<SmartExpenseTrackerDbContext>();
                await context.Database.ExecuteSqlRawAsync(
                    "CREATE UNIQUE INDEX IX_Test_ExpenseDescription ON dbo.Expenses(Description)", Token);
            }

            var observer = new UpdateCommandObserver();
            var failure = new SqlFailureObserver();
            await using var production = _factory.WithWebHostBuilder(builder => builder.UseEnvironment("Production")
                .ConfigureServices(services => services.ConfigureDbContext<SmartExpenseTrackerDbContext>(
                    options => options.AddInterceptors(observer, failure))));
            using var client = production.CreateClient();

            // Act
            using var update = await SendConditionalAsync(client, HttpMethod.Put, secondCreated.Headers.Location,
                secondInput with { Description = firstInput.Description }, secondCreated.Headers.ETag!.ToString(), Token);

            // Assert
            failure.SqlError.Should().Be(2601);
            observer.AssertSingleReadAndVersion(secondVersion);
            update.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            update.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
            var body = await update.Content.ReadAsStringAsync(Token);
            body.Should().NotContain("IX_Test_ExpenseDescription").And.NotContain(nameof(SqlException));
            var problem = System.Text.Json.JsonSerializer.Deserialize<Microsoft.AspNetCore.Mvc.ProblemDetails>(body,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
            problem.Status.Should().Be(500);
            problem.Detail.Should().BeNull();

            // Act
            var actual = (await _client.GetFromJsonAsync<ExpenseResponse>(firstCreated.Headers.Location, Token));

            // Assert
            actual.Should().Be(first);

            // Act
            var actual2 = (await _client.GetFromJsonAsync<ExpenseResponse>(secondCreated.Headers.Location, Token));

            // Assert
            actual2.Should().Be(second);
            var actual3 = (await ReadVersionAsync(first.Id));

            actual3.Should().Equal(firstVersion);
            var actual4 = (await ReadVersionAsync(second.Id));

            actual4.Should().Equal(secondVersion);
            _rates.Calls.Should().Be(0);
        }

        [Test]
        public async Task ReportsHandleCalendarExtremesAndLeapDayWithoutDateOverflow()
        {
            // Arrange
            var dates = new[] { DateOnly.MinValue, new DateOnly(2024, 2, 29), DateOnly.MaxValue };
            for (int index = 0; index < dates.Length; index++)
            {
                // Act
                using var created = await _client.PostAsJsonAsync(_route,
                    Input("EUR") with { Date = dates[index], Amount = index + 1 }, Token);

                // Assert
                created.StatusCode.Should().Be(HttpStatusCode.Created);
            }

            async Task Check(string period, params DateOnly[] starts)
            {
                var totals = await _client.GetFromJsonAsync<PeriodTotalResponse[]>(
                    $"/api/reports/by-period?period={period}", Token);
                totals.Should().Equal(starts.Select((date, index) => new PeriodTotalResponse(date, index + 1, "EUR")));
            }

            // Act
            await Check("day", dates);
            await Check("week", DateOnly.MinValue, new DateOnly(2024, 2, 26), new DateOnly(9999, 12, 27));
            await Check("month", DateOnly.MinValue, new DateOnly(2024, 2, 1), new DateOnly(9999, 12, 1));

            // Assert
            _rates.Calls.Should().Be(0);
        }

        [Test]
        public async Task DatasetCurrencySurvivesLastDeletionAndRejectsConflictingBase()
        {
            // Arrange
            // Use the shared fixture and test-case inputs.

            // Act
            using var created = await _client.PostAsJsonAsync(_route, Input("EUR"), Token);

            // Assert
            created.EnsureSuccessStatusCode();

            // Act
            using var removed = await DeleteExpenseAsync(_client, created.Headers.Location, Token);

            // Assert
            removed.EnsureSuccessStatusCode();

            // Arrange
            await using var changed = WithBaseCurrency("GBP");
            using var client = changed.CreateClient();
            _rates.Available = false;
            foreach (var currency in new[] { "GBP", "USD" })
            {
                // Act
                using var conflict = await client.PostAsJsonAsync(_route, Input(currency), Token);

                // Assert
                await AssertProblem(conflict, HttpStatusCode.Conflict);
            }

            // Assert
            _rates.Calls.Should().Be(0);

            // Act
            var actual = (await _client.GetFromJsonAsync<ExpenseResponse[]>(_route, Token))!;

            // Assert
            actual.Should().BeEmpty();
            var actual2 = (await ReadDatasetAsync());

            actual2.Should().Equal("EUR");

            // Arrange
            _rates.Available = true;

            // Act
            using var matching = await _client.PostAsJsonAsync(_route, Input("USD"), Token);

            // Assert
            matching.StatusCode.Should().Be(HttpStatusCode.Created);
            _rates.Calls.Should().Be(1);
        }

        private WebApplicationFactory<Program> WithBaseCurrency(string currency)
        {
            return _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["RateCache:BaseCurrency"] = currency })));
        }

        private async Task<string[]> ReadDatasetAsync()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SmartExpenseTrackerDbContext>();
            return await db.Database.SqlQuery<string>($"SELECT BaseCurrency AS [Value] FROM dbo.ExpenseDataset").ToArrayAsync(Token);
        }

        [Test]
        public async Task ConcurrentFirstInsertsCommitOnlyTheWinningBaseAndExpense()
        {
            // Arrange
            await using var changed = WithBaseCurrency("GBP");
            using var client = changed.CreateClient();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            var token = deadline.Token;
            await using var observer = new SqlConnection(_connection);
            await observer.OpenAsync(token);
            await using var gate = (SqlTransaction)await observer.BeginTransactionAsync(token);
            using var command = observer.CreateCommand();
            command.Transaction = gate;
            // This shared table lock admits ordinary reads but blocks the initialization update locks.
            command.CommandText = "SELECT COUNT(*) FROM dbo.ExpenseDataset WITH (TABLOCK, HOLDLOCK)";

            // Act
            var actual = ((int)(await command.ExecuteScalarAsync(token))!);

            // Assert
            actual.Should().Be(0);

            // Act
            var eur = _client.PostAsJsonAsync(_route, Input("EUR"), token);
            var gbp = client.PostAsJsonAsync(_route, Input("GBP"), token);
            try
            {
                // Observe both SQL sessions inside the initialization batch before releasing either writer.
                command.CommandText = """
                    SELECT COUNT(DISTINCT r.session_id)
                    FROM sys.dm_exec_requests AS r
                    CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) AS sqltext
                    WHERE r.database_id = DB_ID() AND r.session_id <> @@SPID AND r.wait_type LIKE 'LCK%'
                      AND sqltext.text LIKE '%UPDLOCK, HOLDLOCK%';
                    """;
                while ((int)(await command.ExecuteScalarAsync(token))! < 2)
                {
                    token.ThrowIfCancellationRequested();
                }
            }
            finally
            {
                await gate.RollbackAsync(CancellationToken.None);
            }

            await Task.WhenAll(eur, gbp).WaitAsync(token);
            using var euroResponse = await eur;
            using var poundResponse = await gbp;

            // Assert
            new[] { euroResponse.StatusCode, poundResponse.StatusCode }.Should()
                .BeEquivalentTo(new[] { HttpStatusCode.Created, HttpStatusCode.Conflict });
            var euroWon = euroResponse.StatusCode == HttpStatusCode.Created;
            var winner = euroWon ? "EUR" : "GBP";
            await AssertProblem(euroWon ? poundResponse : euroResponse, HttpStatusCode.Conflict);
            var actual2 = (await ReadDatasetAsync());

            actual2.Should().Equal(winner);

            // Act
            var expenses = await _client.GetFromJsonAsync<ExpenseResponse[]>(_route, token);

            // Assert
            expenses!.Should().ContainSingle().Which.BaseCurrency.Should().Be(winner);
        }

        private static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode expected)
        {
            response.StatusCode.Should().Be(expected);
            response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
            var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>(Token);
            problem!.Status.Should().Be((int)expected);
            problem.Title.Should().NotBeNullOrWhiteSpace();
            problem.Detail.Should().BeNull();
        }

        // Installed only on the failing POST host. Read the binding from the expense write's own transaction.
        private sealed class SqlFailureObserver : SaveChangesInterceptor
        {
            internal int? SqlError { get; private set; }

            public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
            {
                SqlError = (eventData.Exception.GetBaseException() as SqlException)?.Number;
                return Task.CompletedTask;
            }
        }

        private sealed class FirstExpenseWriteObserver : SaveChangesInterceptor
        {
            internal string? PendingBase { get; private set; }
            internal Guid? PendingTransaction { get; private set; }
            internal Guid? FailedTransaction { get; private set; }
            internal int? SqlError { get; private set; }

            public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
                InterceptionResult<int> result, CancellationToken cancellationToken = default)
            {
                var database = eventData.Context!.Database;
                PendingTransaction = database.CurrentTransaction?.TransactionId;
                PendingBase = await database.SqlQuery<string>($"SELECT BaseCurrency AS [Value] FROM dbo.ExpenseDataset")
                    .SingleOrDefaultAsync(cancellationToken);
                return result;
            }

            public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
            {
                FailedTransaction = eventData.Context!.Database.CurrentTransaction?.TransactionId;
                SqlError = (eventData.Exception.GetBaseException() as SqlException)?.Number;
                return Task.CompletedTask;
            }
        }

        private sealed class ControlledRates : IRateResolver
        {
            internal static DateTimeOffset Publication => DateTimeOffset.UnixEpoch.AddYears(55);
            internal bool Available { get; set; } = true;
            internal int Calls { get; private set; }
            internal Func<CancellationToken, Task>? BeforeQuote { get; set; }

            public async Task<ResolvedRates> ResolveAsync(CancellationToken cancellationToken)
            {
                Calls++;
                if (BeforeQuote is not null)
                {
                    await BeforeQuote(cancellationToken);
                }

                if (!Available)
                {
                    throw new RateUnavailableException();
                }

                var table = new ExchangeRateTable("Test", "EUR", Publication, new Dictionary<string, decimal>
                {
                    ["EUR"] = 1m,
                    ["USD"] = 0.925500000001m,
                    ["CHF"] = 0.5m,
                    ["GBP"] = 1.25m
                }.ToFrozenDictionary());
                return new ResolvedRates(new RateSnapshot(table, Publication), true);
            }
        }
    }
}
