using Microsoft.Extensions.Options;
using SmartExpenseTracker.Application.Categories;
using SmartExpenseTracker.Application.Expenses;
using SmartExpenseTracker.Application.Reporting;
using SmartExpenseTracker.Caching;
using SmartExpenseTracker.Integrations;
using SmartExpenseTracker.Persistence;

namespace SmartExpenseTracker.Api
{
    public sealed class Program
    {
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
            app.MapControllers();
            app.MapOpenApi();
            app.Run();
        }
    }
}
