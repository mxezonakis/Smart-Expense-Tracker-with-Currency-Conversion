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
        [TestCase("from=2030-02-01&to=2030-01-01")]
        [TestCase("from=invalid")]
        [TestCase("categoryId=0")]
        [TestCase("currency=123")]
        [TestCase("currency=")]
        [TestCase("currency=%20%20")]
        [TestCase("currency=%20us%20")]
        [TestCase("currency=u%20s")]
        [TestCase("currency=%C5%BFsd")]
        [TestCase("sortBy=currency")]
        [TestCase("sortBy=date&direction=invalid")]
        [TestCase("direction=desc")]
        [TestCase("sortBy=99")]
        public async Task InvalidExpenseQueryReturnsValidationProblem(string query)
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            using var response = await client.GetAsync($"/api/expenses?{query}", TestContext.CurrentContext.CancellationToken);
            response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
            response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        }

        [Test]
        public async Task ExpenseJsonRejectsUnrepresentableFractionalCents()
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            using var body = new StringContent(
                """{"amount":1.00000000000000000000000000001,"currency":"EUR","categoryId":1,"date":"2026-01-01"}""",
                System.Text.Encoding.UTF8, "application/json");
            using var response = await client.PostAsync("/api/expenses", body, TestContext.CurrentContext.CancellationToken);
            response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        }

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

            var expenseSchema = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("ExpenseRequest");
            var amountSchema = expenseSchema.GetProperty("properties").GetProperty("amount");
            amountSchema.GetProperty("type").GetString().Should().Be("number");
            amountSchema.GetProperty("multipleOf").GetDecimal().Should().Be(0.01m);
            var paths = document.RootElement.GetProperty("paths");
            paths.GetProperty("/api/expenses").EnumerateObject().Select(operation => operation.Name)
                .Should().BeEquivalentTo("post", "get");
            var listParameters = paths.GetProperty("/api/expenses").GetProperty("get").GetProperty("parameters");
            listParameters.EnumerateArray().Select(parameter => parameter.GetProperty("name").GetString()!.ToLowerInvariant())
                .Should().BeEquivalentTo("from", "to", "categoryid", "currency", "sortby", "direction");
            var expenseItem = paths.GetProperty("/api/expenses/{id}");
            expenseItem.EnumerateObject().Select(operation => operation.Name).Should().BeEquivalentTo("get", "put", "delete");
            expenseItem.GetProperty("put").GetProperty("responses").TryGetProperty("503", out _).Should().BeTrue();
            paths.GetProperty("/api/categories").EnumerateObject().Select(operation => operation.Name)
                .Should().BeEquivalentTo("post", "get");
            paths.GetProperty("/api/categories/{id}").EnumerateObject().Select(operation => operation.Name)
                .Should().BeEquivalentTo("get", "put", "delete");
            document.RootElement.GetProperty("info").GetProperty("title").GetString().Should().Be("SmartExpenseTracker");
        }
    }
}
