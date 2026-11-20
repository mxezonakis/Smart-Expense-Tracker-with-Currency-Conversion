using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;
using SmartExpenseTracker.Api;

namespace SmartExpenseTracker.Api.IntegrationTests
{
    public sealed class ApiHostTests
    {
        [Test]
        public async Task OpenApiDocumentIdentifiesTheRunningApi()
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/openapi/v1.json", TestContext.CurrentContext.CancellationToken);

            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.CurrentContext.CancellationToken));
            document.RootElement.GetProperty("info").GetProperty("title").GetString().Should().Be("SmartExpenseTracker");
        }
    }
}
