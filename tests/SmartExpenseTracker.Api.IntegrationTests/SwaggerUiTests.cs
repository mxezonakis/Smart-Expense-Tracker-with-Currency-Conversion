using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;
using SmartExpenseTracker.Api.Contracts;

namespace SmartExpenseTracker.Api.IntegrationTests
{
    public sealed class SwaggerUiTests
    {
        private static CancellationToken Token => TestContext.CurrentContext.CancellationToken;

        [Test]
        public async Task DevelopmentServesSwaggerHtmlUsingExistingDocument()
        {
            await using var factory = new WebApplicationFactory<Program>();
            await using var host = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
            using var client = host.CreateClient();

            using var response = await client.GetAsync("/swagger", Token);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
            var html = await response.Content.ReadAsStringAsync(Token);
            html.Should().Contain("<html").And.Contain("Swagger UI").And.Contain("swagger-ui-bundle.js");
            using var script = await client.GetAsync("/swagger/index.js", Token);
            script.StatusCode.Should().Be(HttpStatusCode.OK);
            (await script.Content.ReadAsStringAsync(Token)).Should().Contain("/openapi/v1.json");
        }

        [TestCase("Development")]
        [TestCase("Production")]
        public async Task OpenApiRetainsAllBusinessOperations(string environment)
        {
            await using var factory = new WebApplicationFactory<Program>();
            await using var host = factory.WithWebHostBuilder(builder => builder.UseEnvironment(environment));
            using var client = host.CreateClient();

            using var response = await client.GetAsync("/openapi/v1.json", Token);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
            document.RootElement.GetProperty("openapi").GetString().Should().StartWith("3.");
            var paths = document.RootElement.GetProperty("paths");
            var operations = paths.EnumerateObject().SelectMany(path => path.Value.EnumerateObject()
                .Where(operation => operation.Name is "get" or "post" or "put" or "delete")
                .Select(operation => $"{operation.Name} {path.Name}"));
            operations.Should().BeEquivalentTo(
                "get /api/categories", "post /api/categories", "get /api/categories/{id}",
                "put /api/categories/{id}", "delete /api/categories/{id}",
                "get /api/expenses", "post /api/expenses", "get /api/expenses/{id}",
                "put /api/expenses/{id}", "delete /api/expenses/{id}",
                "get /api/reports/by-category", "get /api/reports/by-period", "get /api/statistics");
        }

        [Test]
        public async Task ProductionDoesNotExposeSwaggerUiOrAssets()
        {
            await using var factory = new WebApplicationFactory<Program>();
            await using var host = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Production"));
            using var client = host.CreateClient();

            foreach (var path in new[] { "/swagger", "/swagger/index.html", "/swagger/index.js", "/swagger/swagger-ui-bundle.js" })
            {
                using var response = await client.GetAsync(path, Token);
                response.StatusCode.Should().Be(HttpStatusCode.NotFound, "{0} is Development-only", path);
            }
        }

        [TestCase("Development", HttpStatusCode.OK, 1)]
        [TestCase("Production", HttpStatusCode.NotFound, 2)]
        public async Task UiRequestsPreserveProblemDetailsAndStatistics(string environment, HttpStatusCode uiStatus, int failures)
        {
            await using var factory = new WebApplicationFactory<Program>();
            await using var host = factory.WithWebHostBuilder(builder => builder.UseEnvironment(environment));
            using var client = host.CreateClient();

            using var ui = await client.GetAsync("/swagger/index.html", Token);
            ui.StatusCode.Should().Be(uiStatus);
            using var invalid = await client.GetAsync("/api/reports/by-period?period=invalid", Token);
            invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            invalid.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
            using var problem = JsonDocument.Parse(await invalid.Content.ReadAsStringAsync(Token));
            problem.RootElement.GetProperty("status").GetInt32().Should().Be(400);
            problem.RootElement.GetProperty("errors").TryGetProperty("period", out _).Should().BeTrue();
            var snapshot = await client.GetFromJsonAsync<RequestStatisticsSnapshot>("/api/statistics", Token);
            snapshot!.CompletedRequests.Should().Be(2);
            snapshot.FailedRequests.Should().Be(failures);
            (await client.GetFromJsonAsync<RequestStatisticsSnapshot>("/api/statistics", Token)).Should().Be(snapshot);
        }
    }
}
