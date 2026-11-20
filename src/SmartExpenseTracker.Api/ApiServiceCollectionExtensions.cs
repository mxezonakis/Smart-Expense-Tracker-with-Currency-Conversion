using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Options;

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

            services.AddOpenApi();
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
