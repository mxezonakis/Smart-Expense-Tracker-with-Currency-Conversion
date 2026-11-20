using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using SmartExpenseTracker.Api;

namespace SmartExpenseTracker.Api.IntegrationTests
{
    public sealed class ApiHostTests
    {
        [Test]
        public void BlankDocumentationTitleFailsDuringHostStartup()
        {
            using var factory = new WebApplicationFactory<Program>();
            using var invalidHost = factory.WithWebHostBuilder(builder =>
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ApiDocumentation:Title"] = " "
                    })));

            Action startHost = () => _ = invalidHost.Server;

            startHost.Should().Throw<OptionsValidationException>()
                .Which.Failures.Should().Contain("An API title is required.");
        }

        [Test]
        public async Task OpenApiDocumentIdentifiesTheRunningApi()
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/openapi/v1.json", TestContext.CurrentContext.CancellationToken);

            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.CurrentContext.CancellationToken));
            var request = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("CategoryRequest");
            request.GetProperty("required").EnumerateArray().Select(property => property.GetString()).Should().Contain("name");
            var name = request.GetProperty("properties").GetProperty("name");
            name.GetProperty("type").GetString().Should().Be("string");
            name.GetProperty("description").GetString().Should().Contain("UTF-16").And.Contain("accent-sensitive");
            name.GetProperty("examples")[0].GetString().Should().Be("  Café  ");
            name.TryGetProperty("maxLength", out _).Should().BeFalse();

            var paths = document.RootElement.GetProperty("paths");
            paths.GetProperty("/api/categories").EnumerateObject().Select(operation => operation.Name)
                .Should().BeEquivalentTo("post", "get");
            paths.GetProperty("/api/categories/{id}").EnumerateObject().Select(operation => operation.Name)
                .Should().BeEquivalentTo("get", "put", "delete");
            document.RootElement.GetProperty("info").GetProperty("title").GetString().Should().Be("SmartExpenseTracker");
        }
    }
}
