# SmartExpenseTracker

Phase 1 provides the ASP.NET Core host and test foundation for an expense tracker with currency conversion. Install the .NET 10 SDK specified in global.json (10.0.404 or a later patch in that feature band).

## Run locally

From the repository root:

```powershell
dotnet restore SmartExpenseTracker.slnx
dotnet build SmartExpenseTracker.slnx --no-restore -warnaserror
dotnet run --project src/SmartExpenseTracker.Api --launch-profile SmartExpenseTracker.Api
```

Open [the OpenAPI document](http://localhost:54131/openapi/v1.json). The Phase 1 document has no business routes. The host validates the configured API documentation title at startup.

## Solution layout

Under `src/`, Api composes the host. Application owns workflow contracts. Persistence, Integrations and Caching are reserved for SQL Server, exchange-rate HTTP access and rate caching respectively. Under `tests/`, Application, Integrations and Caching each have a unit-test project. Api.IntegrationTests exercises host behavior.

## Verify

```powershell
dotnet test SmartExpenseTracker.slnx --no-restore
dotnet format SmartExpenseTracker.slnx --no-restore --verify-no-changes
dotnet package list --project SmartExpenseTracker.slnx --vulnerable --include-transitive
```

The host tests run without Docker, SQL Server or a live currency provider. In restricted Windows environments, VSTest may be unable to connect to its test host, and the formatter may be denied access to its build-host named pipe. Run those checks in a terminal or CI runner that permits local process communication. The vulnerability audit requires access to current advisory data.

See the [development workflow](AGENTS.md), [C# conventions](docs/DOTNET_BACKEND_INSTRUCTIONS.md) and [implementation plan](implementation-plan.md). The requirement source is the privately supplied assignment specification. Obtain it from the assignment owner and place the local copy under `docs/` when working on a phase.
