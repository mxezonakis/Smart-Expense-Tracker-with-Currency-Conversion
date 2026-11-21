using Microsoft.Extensions.Options;
using SmartExpenseTracker.Application.Categories;
using SmartExpenseTracker.Application.Expenses;
using SmartExpenseTracker.Application.Reporting;
using SmartExpenseTracker.Caching;
using SmartExpenseTracker.Integrations;
using SmartExpenseTracker.Persistence;

namespace SmartExpenseTracker.Api
{
    /// <summary>The API host entry point and integration-test host marker.</summary>
    public sealed class Program
    {
        /// <summary>Builds and runs the configured API host.</summary>
        /// <param name="args">Command-line configuration arguments.</param>
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddPersistence(builder.Configuration);
            builder.Services.AddIntegrations(builder.Configuration);
            builder.Services.AddRateCaching(builder.Configuration, "ExchangeRate-API");
            builder.Services.AddScoped<CategoryService>();
            builder.Services.AddSingleton(sp => new DatasetCurrency(sp.GetRequiredService<IOptions<RateCacheOptions>>().Value.BaseCurrency));
            builder.Services.AddScoped<ExpenseService>();
            builder.Services.AddScoped<ReportService>();
            builder.Services.AddSingleton<RequestStatistics>();
            builder.Services.AddExceptionHandler<ApiExceptionHandler>();
            builder.Services.AddApi(builder.Configuration);

            var app = builder.Build();
            app.UseMiddleware<RequestStatisticsMiddleware>();
            app.UseExceptionHandler();
            app.UseStatusCodePages();
            if (app.Environment.IsDevelopment())
            {
                app.UseSwaggerUI(options =>
                {
                    options.RoutePrefix = "swagger";
                    options.SwaggerEndpoint("/openapi/v1.json", "SmartExpenseTracker v1");
                });
            }

            app.MapControllers();
            app.MapOpenApi();
            app.Run();
        }
    }
}
