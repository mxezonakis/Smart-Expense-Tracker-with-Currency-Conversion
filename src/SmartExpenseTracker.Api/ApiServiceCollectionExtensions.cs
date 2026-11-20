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
                if (context.JsonTypeInfo.Type == typeof(CategoryRequest) && schema.Properties?["name"] is OpenApiSchema name)
                {
                    (schema.Required ??= new HashSet<string>()).Add("name");
                    name.Type = JsonSchemaType.String;
                    name.Description = CategoryRequest.NameDescription;
                    name.Examples = [JsonValue.Create("  Café  ")!];
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
