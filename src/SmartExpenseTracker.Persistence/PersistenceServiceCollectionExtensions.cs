using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartExpenseTracker.Persistence.Categories;
using SmartExpenseTracker.Persistence.Expenses;
using SmartExpenseTracker.Persistence.Reporting;
using SmartExpenseTracker.Persistence.ExchangeRates;

namespace SmartExpenseTracker.Persistence
{
    public static class PersistenceServiceCollectionExtensions
    {
        public static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddDbContext<SmartExpenseTrackerDbContext>(options =>
            {
                var connection = configuration.GetConnectionString("SmartExpenseTracker");
                options.UseSqlServer(connection is null ? null : new SqlConnectionStringBuilder(connection)
                {
                    CurrentLanguage = "us_english"
                }.ConnectionString);
            });
            services.AddScoped<IExpenseStore, ExpenseStore>();
            services.AddScoped<IReportStore, ReportStore>();
            services.AddScoped<IRateSnapshotStore, RateSnapshotStore>();
            return services.AddScoped<ICategoryStore, CategoryStore>();
        }
    }
}
