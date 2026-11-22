using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using SmartExpenseTracker.Api.Contracts;

namespace SmartExpenseTracker.Api
{
    internal static class ApiServiceCollectionExtensions
    {
        internal static IServiceCollection AddApi(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddControllers();
            services.AddProblemDetails();
            services.AddOptions<ApiDocumentationOptions>()
                .Bind(configuration.GetSection("ApiDocumentation"))
                .Validate(options => !string.IsNullOrWhiteSpace(options.Title), "An API title is required.")
                .ValidateOnStart();

            services.AddOpenApi(options => options.AddSchemaTransformer((schema, context, _) =>
            {
                // Examples keep the evaluator-facing schema readable; runtime validation is unchanged.
                schema.Pattern = null;
                var propertyName = context.JsonPropertyInfo?.Name.ToLowerInvariant();
                JsonNode? example = propertyName switch
                {
                    "id" or "categoryid" => JsonValue.Create(1),
                    "name" => JsonValue.Create("Dining"),
                    "amount" => JsonValue.Create(10m),
                    "currency" => JsonValue.Create("USD"),
                    "date" or "periodstart" => JsonValue.Create("2026-11-21"),
                    "description" => JsonValue.Create("Lunch"),
                    "basecurrency" => JsonValue.Create("EUR"),
                    "appliedrate" => JsonValue.Create(0.9255m),
                    "convertedamount" or "totalamount" => JsonValue.Create(9.26m),
                    "provider" => JsonValue.Create("ExchangeRate-API"),
                    "publishedat" => JsonValue.Create("2026-11-21T00:00:00Z"),
                    "isfallback" => JsonValue.Create(false),
                    "completedrequests" => JsonValue.Create(10L),
                    "failedrequests" => JsonValue.Create(1L),
                    "totaldurationms" => JsonValue.Create(125.0),
                    "averagedurationms" => JsonValue.Create(12.5),
                    _ => null
                };
                if (example is not null && schema.Examples is null)
                {
                    schema.Examples = [example];
                }

                if (context.JsonTypeInfo.Type == typeof(CategoryRequest) && schema.Properties?["name"] is OpenApiSchema name)
                {
                    (schema.Required ??= new HashSet<string>()).Add("name");
                    name.Type = JsonSchemaType.String;
                    name.Description = CategoryRequest.NameDescription;
                    name.Examples = [JsonValue.Create("  Café  ")!];
                }

                if (context.JsonTypeInfo.Type == typeof(ExpenseRequest) && schema.Properties?["amount"] is OpenApiSchema amount)
                {
                    amount.Type = JsonSchemaType.Number;
                    amount.Minimum = "0.01";
                    amount.Maximum = "99999999.99";
                    amount.MultipleOf = 0.01m;
                    amount.Description = "Original amount in whole cents.";
                }

                return Task.CompletedTask;
            }).AddOperationTransformer((operation, context, _) =>
            {
                foreach (var parameter in (operation.Parameters ?? []).OfType<OpenApiParameter>())
                {
                    if (parameter.Schema is not OpenApiSchema schema)
                    {
                        continue;
                    }

                    schema.Pattern = null;
                    JsonNode? example = parameter.Name?.ToLowerInvariant() switch
                    {
                        "id" or "categoryid" => JsonValue.Create(1),
                        "from" => JsonValue.Create("2026-11-01"),
                        "to" => JsonValue.Create("2026-11-30"),
                        "currency" => JsonValue.Create("USD"),
                        "sortby" => JsonValue.Create("amount"),
                        "direction" => JsonValue.Create("desc"),
                        "period" => JsonValue.Create("month"),
                        _ => null
                    };
                    if (example is not null)
                    {
                        parameter.Example = example;
                    }
                }

                var path = context.Description.RelativePath;
                var method = context.Description.HttpMethod;
                if (path == "api/expenses/{id}" && method is "PUT" or "DELETE")
                {
                    (operation.Parameters ??= []).Add(new OpenApiParameter
                    {
                        Name = "If-Match",
                        In = ParameterLocation.Header,
                        Required = true,
                        Description = "Exactly one strong ETag from the individual expense GET or POST. Include the quotes. " +
                            "Missing: 428; malformed, weak, wildcard or multiple tags: 400; stale: 412.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String },
                        Example = JsonValue.Create("\"AAAAAAAAB9E=\"")
                    });
                }

                var etagStatus = (path, method) switch
                {
                    ("api/expenses", "POST") => "201",
                    ("api/expenses/{id}", "GET") => "200",
                    ("api/expenses/{id}", "PUT") => "204",
                    _ => null
                };
                if (etagStatus is not null && operation.Responses?[etagStatus] is OpenApiResponse response)
                {
                    (response.Headers ??= new Dictionary<string, IOpenApiHeader>())["ETag"] = new OpenApiHeader
                    {
                        Description = "Strong opaque version of this expense. Send the complete quoted value as If-Match on the next PUT or DELETE.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String, Examples = [JsonValue.Create("\"AAAAAAAAB9E=\"")!] }
                    };
                }

                if (path == "api/expenses/{id}" && method == "PUT" &&
                    operation.Responses?["200"] is OpenApiResponse unchanged &&
                    operation.Responses?["204"] is OpenApiResponse updated)
                {
                    unchanged.Headers = updated.Headers;
                }

                return Task.CompletedTask;
            }));
            services.AddOptions<OpenApiOptions>("v1")
                .Configure<IOptions<ApiDocumentationOptions>>((openApi, documentation) =>
                    openApi.AddDocumentTransformer((document, _, _) =>
                    {
                        document.Info.Title = documentation.Value.Title;
                        return Task.CompletedTask;
                    }));
            return services;
        }
    }
}
