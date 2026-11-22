using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;

namespace SmartExpenseTracker.Api.IntegrationTests
{
    public sealed class OpenApiDocumentationTests
    {
        private static CancellationToken Token => TestContext.CurrentContext.CancellationToken;

        [Test]
        public async Task GeneratedDocumentExposesConditionalHeaders()
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/openapi/v1.json", Token);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
            var paths = document.RootElement.GetProperty("paths");
            foreach (var method in new[] { "put", "delete" })
            {
                var header = paths.GetProperty("/api/expenses/{id}").GetProperty(method).GetProperty("parameters")
                    .EnumerateArray().Single(parameter => parameter.GetProperty("name").GetString() == "If-Match");
                header.GetProperty("in").GetString().Should().Be("header");
                header.GetProperty("required").GetBoolean().Should().BeTrue();
                header.GetProperty("description").GetString().Should().Contain("strong").And.Contain("428").And.Contain("412");
                header.GetProperty("example").GetString().Should().Be("\"AAAAAAAAB9E=\"");
            }

            foreach (var (path, method, status) in new[] { ("/api/expenses", "post", "201"),
                ("/api/expenses/{id}", "get", "200"), ("/api/expenses/{id}", "put", "204") })
            {
                paths.GetProperty(path).GetProperty(method).GetProperty("responses").GetProperty(status)
                    .GetProperty("headers").GetProperty("ETag").GetProperty("description").GetString()
                    .Should().Contain("If-Match");
            }
        }

        [Test]
        public async Task GeneratedDocumentDescribesEveryOperationAndItsActualResponses()
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/openapi/v1.json", Token);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
            var paths = document.RootElement.GetProperty("paths");
            var expected = new (string Path, string Method, string Summary, string[] Codes)[]
            {
                ("/api/categories", "post", "Create a category.", ["201", "400", "409"]),
                ("/api/categories", "get", "List categories.", ["200"]),
                ("/api/categories/{id}", "get", "Get a category.", ["200", "404"]),
                ("/api/categories/{id}", "put", "Update a category name.", ["204", "400", "404", "409"]),
                ("/api/categories/{id}", "delete", "Delete a category.", ["204", "404", "409"]),
                ("/api/expenses", "post", "Create an expense with a conversion snapshot.", ["201", "400", "409", "503"]),
                ("/api/expenses", "get", "List, filter and sort expenses.", ["200", "400"]),
                ("/api/expenses/{id}", "get", "Get an expense and its stored conversion.", ["200", "404"]),
                ("/api/expenses/{id}", "put", "Update an expense.", ["204", "400", "404", "409", "412", "428", "503"]),
                ("/api/expenses/{id}", "delete", "Delete an expense.", ["204", "400", "412", "428"]),
                ("/api/reports/by-category", "get", "Get total spending by category.", ["200"]),
                ("/api/reports/by-period", "get", "Get total spending by calendar period.", ["200", "400"]),
                ("/api/statistics", "get", "Get request statistics.", ["200"])
            };

            paths.EnumerateObject().Sum(path => path.Value.EnumerateObject().Count()).Should().Be(13);
            foreach (var entry in expected)
            {
                var operation = paths.GetProperty(entry.Path).GetProperty(entry.Method);
                operation.GetProperty("summary").GetString().Should().Be(entry.Summary);
                var responses = operation.GetProperty("responses");
                responses.EnumerateObject().Select(item => item.Name).Should().BeEquivalentTo(entry.Codes);
                foreach (var item in responses.EnumerateObject())
                {
                    item.Value.GetProperty("description").GetString().Should().NotBeNullOrWhiteSpace()
                        .And.NotMatchRegex("^(OK|Created|No Content|Bad Request|Not Found|Conflict|Service Unavailable)$");
                }
            }

            paths.GetProperty("/api/expenses").GetProperty("post").GetProperty("responses")
                .GetProperty("503").GetProperty("description").GetString().Should().Be("No eligible exchange rate is available.");
            paths.GetProperty("/api/categories/{id}").GetProperty("delete").GetProperty("responses")
                .GetProperty("409").GetProperty("description").GetString().Should().Contain("Expenses still reference");
        }

        [Test]
        public async Task GeneratedDocumentDescribesInputsAndSnapshotSemantics()
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/openapi/v1.json", Token);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
            var paths = document.RootElement.GetProperty("paths");
            var list = paths.GetProperty("/api/expenses").GetProperty("get");
            list.GetProperty("description").GetString().Should().Contain("inclusive").And.Contain("No pagination")
                .And.Contain("descending").And.Contain("ascending ID");
            var parameters = list.GetProperty("parameters").EnumerateArray()
                .ToDictionary(parameter => parameter.GetProperty("name").GetString()!,
                    parameter => parameter.GetProperty("description").GetString()!, StringComparer.OrdinalIgnoreCase);
            parameters.Keys.Should().BeEquivalentTo("From", "To", "CategoryId", "Currency", "SortBy", "Direction");
            parameters["From"].Should().Contain("Inclusive earliest");
            parameters["To"].Should().Contain("Inclusive latest");
            parameters["CategoryId"].Should().Contain("positive category identifier");
            parameters["Currency"].Should().Contain("original currency").And.Contain("three ASCII letters").And.Contain("trimming");
            parameters["SortBy"].Should().Contain("stored converted amount");
            parameters["Direction"].Should().Contain("defaults to desc").And.Contain("ascending ID");
            paths.GetProperty("/api/reports/by-period").GetProperty("get").GetProperty("parameters")[0]
                .GetProperty("description").GetString().Should().Contain("day, week or month").And.Contain("Monday");
            paths.GetProperty("/api/expenses/{id}").GetProperty("get").GetProperty("parameters")[0]
                .GetProperty("description").GetString().Should().Be("The expense identifier.");
            paths.GetProperty("/api/expenses/{id}").GetProperty("put").GetProperty("description").GetString()
                .Should().Contain("preserve the stored rate");
            paths.GetProperty("/api/statistics").GetProperty("get").GetProperty("description").GetString()
                .Should().Contain("400-599").And.Contain("aborted requests").And.Contain("reset on restart");
            foreach (var path in new[] { "/api/categories", "/api/expenses" })
            {
                foreach (var (route, method) in new[] { (path, "post"), (path + "/{id}", "put") })
                {
                    paths.GetProperty(route).GetProperty(method).GetProperty("requestBody").GetProperty("description")
                        .GetString().Should().NotBeNullOrWhiteSpace().And.NotContain("Cancels")
                        .And.Contain(path.EndsWith("categories", StringComparison.Ordinal) ? "name" : method == "post" ? "original amount" : "expense fields");
                }
            }
        }

        [Test]
        public async Task GeneratedDocumentUsesExamplesInsteadOfPatternsWithoutChangingNumericTypes()
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/openapi/v1.json", Token);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
            document.RootElement.GetRawText().Should().NotContain("\"pattern\"");
            var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
            var expense = schemas.GetProperty("ExpenseResponse").GetProperty("properties");
            expense.GetProperty("id").GetProperty("type").EnumerateArray().Select(item => item.GetString())
                .Should().BeEquivalentTo("integer", "string");
            expense.GetProperty("id").GetProperty("format").GetString().Should().Be("int32");
            expense.GetProperty("currency").GetProperty("type").GetString().Should().Be("string");
            expense.GetProperty("currency").TryGetProperty("format", out _).Should().BeFalse();
            expense.GetProperty("amount").GetProperty("examples")[0].GetDecimal().Should().Be(10m);
            expense.GetProperty("currency").GetProperty("examples")[0].GetString().Should().Be("USD");
            expense.GetProperty("convertedAmount").GetProperty("examples")[0].GetDecimal().Should().Be(9.26m);
            foreach (var name in new[] { "CategoryRequest", "CategoryResponse", "ExpenseRequest", "ExpenseResponse",
                "CategoryTotalResponse", "PeriodTotalResponse", "RequestStatisticsSnapshot" })
            {
                foreach (var property in schemas.GetProperty(name).GetProperty("properties").EnumerateObject())
                {
                    property.Value.GetProperty("examples").GetArrayLength().Should().BeGreaterThan(0, "{0}.{1} needs an example", name, property.Name);
                }
            }

            foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
            {
                foreach (var operation in path.Value.EnumerateObject())
                {
                    if (operation.Value.TryGetProperty("parameters", out var parameters))
                    {
                        foreach (var parameter in parameters.EnumerateArray())
                        {
                            parameter.TryGetProperty("example", out _).Should().BeTrue("{0} needs an example", parameter.GetProperty("name").GetString());
                        }
                    }
                }
            }
        }

        [Test]
        public async Task GeneratedSchemasDescribeModelsAndPreserveExistingConstraints()
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/openapi/v1.json", Token);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
            document.RootElement.GetProperty("info").GetProperty("title").GetString().Should().Be("SmartExpenseTracker");
            var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
            foreach (var name in new[] { "CategoryRequest", "CategoryResponse", "ExpenseRequest", "ExpenseResponse",
                "CategoryTotalResponse", "PeriodTotalResponse", "RequestStatisticsSnapshot" })
            {
                var schema = schemas.GetProperty(name);
                schema.GetProperty("description").GetString().Should().NotBeNullOrWhiteSpace();
                foreach (var property in schema.GetProperty("properties").EnumerateObject())
                {
                    property.Value.GetProperty("description").GetString().Should().NotBeNullOrWhiteSpace("{0}.{1} needs documentation", name, property.Name);
                }
            }

            var expense = schemas.GetProperty("ExpenseResponse").GetProperty("properties");
            expense.GetProperty("appliedRate").GetProperty("description").GetString().Should().Contain("Base-currency units per original-currency unit").And.Contain("12 decimal places");
            expense.GetProperty("convertedAmount").GetProperty("description").GetString().Should().Contain("half-to-even").And.Contain("May be zero");
            expense.GetProperty("publishedAt").GetProperty("description").GetString().Should().Contain("publication timestamp in UTC");
            expense.GetProperty("isFallback").GetProperty("description").GetString().Should().Contain("last-known rate after refresh failure");
            schemas.GetProperty("PeriodTotalResponse").GetProperty("properties").GetProperty("periodStart")
                .GetProperty("description").GetString().Should().Contain("Monday").And.Contain("first day of the month");
            schemas.GetProperty("RequestStatisticsSnapshot").GetProperty("properties").GetProperty("averageDurationMs")
                .GetProperty("description").GetString().Should().Contain("zero when no requests");
            var request = schemas.GetProperty("ExpenseRequest");
            request.GetProperty("required").EnumerateArray().Select(item => item.GetString())
                .Should().Contain(["amount", "currency", "categoryId", "date"]);
            var amount = request.GetProperty("properties").GetProperty("amount");
            amount.GetProperty("type").GetString().Should().Be("number");
            amount.GetProperty("minimum").GetDecimal().Should().Be(0.01m);
            amount.GetProperty("maximum").GetDecimal().Should().Be(99999999.99m);
            amount.GetProperty("multipleOf").GetDecimal().Should().Be(0.01m);
            var category = schemas.GetProperty("CategoryRequest");
            category.GetProperty("required").EnumerateArray().Select(item => item.GetString()).Should().Contain("name");
            var categoryName = category.GetProperty("properties").GetProperty("name");
            categoryName.GetProperty("description").GetString().Should().Contain("UTF-16").And.Contain("accent-sensitive");
            categoryName.GetProperty("examples")[0].GetString().Should().Be("  Caf\u00e9  ");
        }
    }
}
