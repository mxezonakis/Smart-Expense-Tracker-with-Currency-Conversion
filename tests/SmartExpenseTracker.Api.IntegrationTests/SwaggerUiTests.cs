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
            // Arrange
            await using var factory = new WebApplicationFactory<Program>();
            await using var host = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
            using var client = host.CreateClient();

            // Act
            using var response = await client.GetAsync("/swagger", Token);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
            var html = await response.Content.ReadAsStringAsync(Token);
            html.Should().Contain("<html").And.Contain("Swagger UI").And.Contain("swagger-ui-bundle.js");

            // Act
            using var script = await client.GetAsync("/swagger/index.js", Token);

            // Assert
            script.StatusCode.Should().Be(HttpStatusCode.OK);
            var actual = (await script.Content.ReadAsStringAsync(Token));

            actual.Should().Contain("/openapi/v1.json");
        }

        [TestCase("Development")]
        [TestCase("Production")]
        public async Task OpenApiRetainsAllBusinessOperations(string environment)
        {
            // Arrange
            await using var factory = new WebApplicationFactory<Program>();
            await using var host = factory.WithWebHostBuilder(builder => builder.UseEnvironment(environment));
            using var client = host.CreateClient();

            // Act
            using var response = await client.GetAsync("/openapi/v1.json", Token);

            // Assert
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
            // Arrange
            await using var factory = new WebApplicationFactory<Program>();
            await using var host = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Production"));
            using var client = host.CreateClient();

            foreach (var path in new[] { "/swagger", "/swagger/index.html", "/swagger/index.js", "/swagger/swagger-ui-bundle.js" })
            {
                // Act
                using var response = await client.GetAsync(path, Token);

                // Assert
                response.StatusCode.Should().Be(HttpStatusCode.NotFound, "{0} is Development-only", path);
            }
        }

        [TestCase("Development", HttpStatusCode.OK, 1)]
        [TestCase("Production", HttpStatusCode.NotFound, 2)]
        public async Task UiRequestsPreserveProblemDetailsAndStatistics(string environment, HttpStatusCode uiStatus, int failures)
        {
            // Arrange
            await using var factory = new WebApplicationFactory<Program>();
            await using var host = factory.WithWebHostBuilder(builder => builder.UseEnvironment(environment));
            using var client = host.CreateClient();

            // Act
            using var ui = await client.GetAsync("/swagger/index.html", Token);

            // Assert
            ui.StatusCode.Should().Be(uiStatus);

            // Act
            using var invalid = await client.GetAsync("/api/reports/by-period?period=invalid", Token);

            // Assert
            invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            invalid.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
            using var problem = JsonDocument.Parse(await invalid.Content.ReadAsStringAsync(Token));
            problem.RootElement.GetProperty("status").GetInt32().Should().Be(400);
            problem.RootElement.GetProperty("errors").TryGetProperty("period", out _).Should().BeTrue();

            // Act
            var snapshot = await client.GetFromJsonAsync<RequestStatisticsSnapshot>("/api/statistics", Token);

            // Assert
            snapshot!.CompletedRequests.Should().Be(2);
            snapshot.FailedRequests.Should().Be(failures);

            // Act
            var actual = (await client.GetFromJsonAsync<RequestStatisticsSnapshot>("/api/statistics", Token));

            // Assert
            actual.Should().Be(snapshot);
        }
    }
}
