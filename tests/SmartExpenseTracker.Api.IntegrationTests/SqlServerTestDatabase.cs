using Testcontainers.MsSql;

namespace SmartExpenseTracker.Api.IntegrationTests
{
    internal static class SqlServerTestDatabase
    {
        internal static MsSqlContainer Create()
        {
            return new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU16-ubuntu-22.04").Build();
        }
    }
}
