using Microsoft.EntityFrameworkCore;

namespace SmartExpenseTracker.Persistence
{
    public sealed class SmartExpenseTrackerDbContext : DbContext
    {
        public SmartExpenseTrackerDbContext(DbContextOptions<SmartExpenseTrackerDbContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyConfiguration(new CategoryConfiguration());
        }
    }
}
