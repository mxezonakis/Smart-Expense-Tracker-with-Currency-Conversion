# SmartExpenseTracker

The ASP.NET Core API provides SQL Server-backed category management. Install the .NET 10 SDK specified in global.json (10.0.404 or a later patch in that feature band).

## Run locally

Category requests require a SQL Server instance (SQL Server 2022 is used in tests). Configure the connection using the standard environment variable below. This example uses Windows integrated authentication and trusts a local development certificate. Supply deployment credentials through your environment's secret configuration.

From the repository root in PowerShell:

```powershell
dotnet restore SmartExpenseTracker.slnx
dotnet build SmartExpenseTracker.slnx --no-restore -warnaserror
$env:ConnectionStrings__SmartExpenseTracker = 'Server=localhost;Database=SmartExpenseTracker;Integrated Security=true;TrustServerCertificate=true'
# Install once if dotnet-ef is unavailable
dotnet tool install --global dotnet-ef --version 10.0.9
dotnet ef database update --project src/SmartExpenseTracker.Persistence --startup-project src/SmartExpenseTracker.Api
dotnet run --project src/SmartExpenseTracker.Api --launch-profile SmartExpenseTracker.Api
```

Open [the OpenAPI document](http://localhost:54131/openapi/v1.json). The document includes the five category operations. The host validates the configured API documentation title at startup.

## Categories

```powershell
$uri = 'http://localhost:54131/api/categories'
$created = Invoke-RestMethod -Method Post -Uri $uri -ContentType application/json -Body '{"name":" Food "}'
Invoke-RestMethod -Uri $uri
Invoke-RestMethod -Uri "$uri/$($created.id)"
Invoke-RestMethod -Method Put -Uri "$uri/$($created.id)" -ContentType application/json -Body '{"name":"Dining"}'
Invoke-RestMethod -Method Delete -Uri "$uri/$($created.id)"
```

POST returns 201, a Location header and an object such as `{"id":1,"name":"Food"}`. GET returns the object or a list ordered by ascending ID. PUT and DELETE return 204. Invalid names return 400, missing IDs return 404, and duplicate writes return 409 with ProblemDetails. Names preserve display case and internal spaces. Surrounding whitespace is trimmed before the 100 UTF-16 code-unit limit and case-insensitive uniqueness checks.

## Solution layout

Under `src/`, Api composes the host. Application owns workflow contracts. Persistence owns EF Core, migrations and SQL Server access. Integrations and Caching are reserved for exchange-rate HTTP access and rate caching. Under `tests/`, Application, Integrations and Caching each have a unit-test project. Api.IntegrationTests exercises host behavior.

## Verify

```powershell
dotnet test SmartExpenseTracker.slnx --no-restore
dotnet format SmartExpenseTracker.slnx --no-restore --verify-no-changes
dotnet package list --project SmartExpenseTracker.slnx --vulnerable --include-transitive
```

The full suite requires a running Docker Linux engine with support for the SQL Server 2022 x86-64 image, enough memory for SQL Server, and access to pull the image. The SqlServer fixture starts a disposable container and migrates an empty database per test. For the Docker-independent subset use `dotnet test SmartExpenseTracker.slnx --filter 'TestCategory!=SqlServer'`. The host tests run without Docker, SQL Server or a live currency provider. In restricted Windows environments, VSTest may be unable to connect to its test host, and the formatter may be denied access to its build-host named pipe. Run those checks in a terminal or CI runner that permits local process communication. The vulnerability audit requires access to current advisory data.

See the [development workflow](AGENTS.md), [C# conventions](docs/DOTNET_BACKEND_INSTRUCTIONS.md) and [implementation plan](implementation-plan.md). The requirement source is the privately supplied assignment specification. Obtain it from the assignment owner and place the local copy under `docs/` when working on a phase.
