using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SmartExpenseTracker.Api.Contracts;
using SmartExpenseTracker.Persistence;
using SmartExpenseTracker.Persistence.Categories;
using Testcontainers.MsSql;

namespace SmartExpenseTracker.Api.IntegrationTests
{
    [NUnit.Framework.Category("SqlServer")]
    public sealed class CategoryApiTests
    {
        private const string _route = "/api/categories";
        private MsSqlContainer _server = null!;
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private static CancellationToken Token => TestContext.CurrentContext.CancellationToken;

        [OneTimeSetUp]
        public async Task StartSqlServer()
        {
            _server = SqlServerTestDatabase.Create();
            await _server.StartAsync(Token);
        }

        [SetUp]
        public async Task MigrateEmptyDatabase()
        {
            var connection = new SqlConnectionStringBuilder(_server.GetConnectionString())
            {
                InitialCatalog = $"SmartExpenseTracker_{Guid.NewGuid():N}"
            };

            _factory = new CategoryApiFactory(connection.ConnectionString);
            _client = _factory.CreateClient();
            using var scope = _factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<SmartExpenseTrackerDbContext>();
            await context.Database.MigrateAsync(Token);
            (await context.Database.GetAppliedMigrationsAsync(Token)).Should().Contain("20261120000100_CreateCategories");
        }

        private sealed class CategoryApiFactory : WebApplicationFactory<Program>
        {
            private readonly string _connectionString;

            public CategoryApiFactory(string connectionString)
            {
                _connectionString = connectionString;
            }

            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["ConnectionStrings:SmartExpenseTracker"] = _connectionString }));
            }
        }

        [TearDown]
        public async Task DisposeHost()
        {
            _client.Dispose();
            await _factory.DisposeAsync();
        }

        [OneTimeTearDown]
        public async Task DisposeDatabaseContainer()
        {
            await _server.DisposeAsync();
        }

        [Test]
        public async Task CategorySurvivesIndependentRequestsAndCanBeRenamedAndDeleted()
        {
            // Arrange
            // Use the shared fixture and test-case inputs.

            // Act
            using var created = await Create("  Food ");

            // Assert
            created.StatusCode.Should().Be(HttpStatusCode.Created);
            var location = created.Headers.Location!;
            var category = (await created.Content.ReadFromJsonAsync<CategoryResponse>(Token))!;
            category.Id.Should().BePositive();
            category.Name.Should().Be("Food");
            location.AbsolutePath.Should().Be($"{_route}/{category.Id}");

            // Act
            var actual = (await _client.GetFromJsonAsync<CategoryResponse>(location, Token));

            // Assert
            actual.Should().Be(category);

            // Act
            using var second = await Create("Books");

            // Assert
            second.EnsureSuccessStatusCode();

            // Act
            var list = await _client.GetFromJsonAsync<CategoryResponse[]>(_route, Token);

            // Assert
            list!.Select(item => item.Name).Should().Equal("Food", "Books");

            // Act
            using var updated = await _client.PutAsJsonAsync(location, new { name = " Dining " }, Token);

            // Assert
            updated.StatusCode.Should().Be(HttpStatusCode.NoContent);

            // Act
            var actual2 = (await _client.GetFromJsonAsync<CategoryResponse>(location, Token))!.Name;

            // Assert
            actual2.Should().Be("Dining");

            // Act
            using var deleted = await _client.DeleteAsync(location, Token);

            // Assert
            deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);

            // Act
            using var missing = await _client.GetAsync(location, Token);

            // Assert
            await AssertProblem(missing, HttpStatusCode.NotFound);

            // Act
            using var missingUpdate = await _client.PutAsJsonAsync(location, new { name = "Dining" }, Token);

            // Assert
            await AssertProblem(missingUpdate, HttpStatusCode.NotFound);

            // Act
            using var missingDelete = await _client.DeleteAsync(location, Token);

            // Assert
            await AssertProblem(missingDelete, HttpStatusCode.NotFound);
        }

        [Test]
        public async Task InvalidNamesProduceValidationErrors()
        {
            // Arrange
            foreach (var name in new[] { null, "", " \t", new string('x', 101) })
            {
                // Act
                using var response = await Create(name);

                // Assert
                var problem = await AssertProblem(response, HttpStatusCode.BadRequest);
                problem.GetProperty("errors").GetProperty("name").GetArrayLength().Should().BePositive();
            }
        }

        [Test]
        public async Task ConcurrentDuplicateCreatesAndDuplicateUpdateRespectUniqueIndex()
        {
            // Arrange
            var barrier = new CategoryInsertBarrier();
            await using var concurrentHost = _factory.WithWebHostBuilder(builder =>
                builder.ConfigureServices(services => services.ConfigureDbContext<SmartExpenseTrackerDbContext>(
                    options => options.AddInterceptors(barrier))));
            using var client = concurrentHost.CreateClient();

            // Act
            var requests = new[]
            {
                client.PostAsJsonAsync(_route, new { name = " Travel " }, Token),
                client.PostAsJsonAsync(_route, new { name = "trAVEL" }, Token)
            };
            var responses = await Task.WhenAll(requests);

            // Assert
            barrier.BothWritesArrived.Should().BeTrue();
            using var created = responses.Single(response => response.StatusCode == HttpStatusCode.Created);
            using var conflict = responses.Single(response => response.StatusCode == HttpStatusCode.Conflict);
            await AssertProblem(conflict, HttpStatusCode.Conflict);

            // Act
            var list = await _client.GetFromJsonAsync<CategoryResponse[]>(_route, Token);

            // Assert
            list.Should().ContainSingle();

            // Act
            using var second = await Create("Food");
            using var update = await _client.PutAsJsonAsync(second.Headers.Location, new { name = " TRAVEL " }, Token);

            // Assert
            await AssertProblem(update, HttpStatusCode.Conflict);

            // Act
            var actual = (await _client.GetFromJsonAsync<CategoryResponse>(second.Headers.Location, Token))!.Name;

            // Assert
            actual.Should().Be("Food");
        }

        [Test]
        public async Task SqlConstraintRejectsNoncanonicalNamesFromDirectWriters()
        {
            // Arrange
            using var scope = _factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<SmartExpenseTrackerDbContext>();
            foreach (var name in new[] { "", "\u2003Food", "Food\t" })
            {
                // Act
                Func<Task> insert = () => context.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO [Categories] ([Name]) VALUES ({name})", Token);

                var failure = await insert.Should().ThrowAsync<SqlException>();

                // Assert
                failure.Which.Number.Should().Be(547);
            }
        }

        [TestCase("application/json")]
        [TestCase("application/problem+json")]
        [TestCase("application/xml")]
        public async Task CategoryFailuresRespectAcceptHeader(string accept)
        {
            // Arrange
            // Use the shared fixture and test-case inputs.

            // Act
            using var created = await Create("Dining");

            // Assert
            created.EnsureSuccessStatusCode();

            // Arrange
            _client.DefaultRequestHeaders.Accept.ParseAdd(accept);

            // Act
            using var invalid = await Create(" ");
            using var duplicate = await Create("DINING");

            // Assert
            invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
            if (accept == "application/xml")
            {
                var actual = (await invalid.Content.ReadAsByteArrayAsync(Token));

                actual.Should().BeEmpty();
                var actual2 = (await duplicate.Content.ReadAsByteArrayAsync(Token));

                actual2.Should().BeEmpty();
                return;
            }

            var validation = await AssertProblem(invalid, HttpStatusCode.BadRequest);
            validation.GetProperty("errors").GetProperty("name").GetArrayLength().Should().BePositive();
            var conflict = await AssertProblem(duplicate, HttpStatusCode.Conflict);
            conflict.GetProperty("title").GetString().Should().Be("A category with this name already exists.");
        }

        [Test]
        public async Task NamesPreserveAccentsAndIgnoreCaseAfterTrimming()
        {
            // Arrange
            // Use the shared fixture and test-case inputs.

            // Act
            using var plain = await Create(" Cafe ");
            using var accented = await Create(" Café ");

            // Assert
            plain.StatusCode.Should().Be(HttpStatusCode.Created);
            accented.StatusCode.Should().Be(HttpStatusCode.Created);

            // Act
            using var upper = await Create(" CAFÉ ");

            // Assert
            await AssertProblem(upper, HttpStatusCode.Conflict);

            // Act
            var list = await _client.GetFromJsonAsync<CategoryResponse[]>(_route, Token);

            // Assert
            list!.Select(category => category.Name).Should().Equal("Cafe", "Café");
        }

        [Test]
        public async Task PrimaryKeyCollisionPreservesSqlError()
        {
            // Arrange
            // Use the shared fixture and test-case inputs.

            // Act
            using var created = await Create("Travel");

            // Assert
            created.EnsureSuccessStatusCode();

            // Arrange
            using var scope = _factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<SmartExpenseTrackerDbContext>();
            await context.Database.ExecuteSqlRawAsync("DBCC CHECKIDENT ('dbo.Categories', RESEED, 0)", Token);

            var store = scope.ServiceProvider.GetRequiredService<ICategoryStore>();

            // Act
            Func<Task> insert = () => store.CreateAsync("IX_Categories_Name", Token);
            var failure = await insert.Should().ThrowAsync<DbUpdateException>();

            // Assert
            failure.Which.InnerException.Should().BeOfType<SqlException>().Which.Number.Should().Be(2627);
        }

        [Test]
        public async Task UnrelatedUniqueIndexOnRenamePreservesSqlError()
        {
            // Arrange
            // Use the shared fixture and test-case inputs.

            // Act
            using var first = await Create("A");
            using var second = await Create("B");

            // Assert
            first.EnsureSuccessStatusCode();
            second.EnsureSuccessStatusCode();
            var category = (await second.Content.ReadFromJsonAsync<CategoryResponse>(Token))!;

            // Arrange
            using var scope = _factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<SmartExpenseTrackerDbContext>();
            await context.Database.ExecuteSqlRawAsync(
                "ALTER TABLE dbo.Categories ADD Initial AS LEFT(Name, 1) PERSISTED", Token);
            await context.Database.ExecuteSqlRawAsync(
                "CREATE UNIQUE INDEX IX_Test_Initial ON dbo.Categories(Initial)", Token);

            var store = scope.ServiceProvider.GetRequiredService<ICategoryStore>();

            // Act
            Func<Task> rename = () => store.RenameAsync(category.Id, "Another", Token);
            var failure = await rename.Should().ThrowAsync<SqlException>();

            // Assert
            failure.Which.Number.Should().Be(2601);
        }

        [Test]
        public async Task PublishedMigrationsRollbackAndReapplyToUsableSchema()
        {
            // Arrange
            using (var scope = _factory.Services.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<SmartExpenseTrackerDbContext>();
                var migrator = context.GetService<IMigrator>();
                var expected = new[]
                {
                    "20261120000100_CreateCategories", "20261120155857_AddRateSnapshots",
                    "20261120180100_AddExpenses", "20261120210000_AddExpenseQueryIndexes"
                };

                // Act
                var actual = (await context.Database.GetAppliedMigrationsAsync(Token));

                // Assert
                actual.Should().Equal(expected);

                // Act
                await migrator.MigrateAsync("0", Token);
                var actual2 = (await context.Database.GetAppliedMigrationsAsync(Token));

                // Assert
                actual2.Should().BeEmpty();

                // Act
                var tables = await context.Database.SqlQuery<string>($"""
                    SELECT name AS [Value] FROM sys.tables
                    WHERE name IN ('Categories', 'RateSnapshots', 'Expenses', 'ExpenseDataset')
                    """).ToListAsync(Token);

                // Assert
                tables.Should().BeEmpty();

                // Act
                await context.Database.MigrateAsync(Token);
                await context.Database.MigrateAsync(Token);
                var actual3 = (await context.Database.GetAppliedMigrationsAsync(Token));

                // Assert
                actual3.Should().Equal(expected);
                context.Database.HasPendingModelChanges().Should().BeFalse();
            }

            // Act
            using var created = await Create("After migration");

            // Assert
            created.StatusCode.Should().Be(HttpStatusCode.Created);
            var category = (await created.Content.ReadFromJsonAsync<CategoryResponse>(Token))!;

            // Act
            var actual4 = (await _client.GetFromJsonAsync<CategoryResponse>(created.Headers.Location, Token));

            // Assert
            actual4.Should().Be(category);
        }

        private Task<HttpResponseMessage> Create(string? name)
        {
            return _client.PostAsJsonAsync(_route, new { name }, Token);
        }

        private static async Task<JsonElement> AssertProblem(HttpResponseMessage response, HttpStatusCode status)
        {
            response.StatusCode.Should().Be(status);
            response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
            problem.GetProperty("status").GetInt32().Should().Be((int)status);
            problem.GetProperty("title").GetString().Should().NotBeNullOrEmpty();
            return problem;
        }
    }
}
