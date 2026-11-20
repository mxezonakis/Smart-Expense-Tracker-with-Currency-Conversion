# SmartExpenseTracker

The ASP.NET Core API provides SQL Server-backed category and expense management. Install the .NET 10 SDK specified in global.json (10.0.404 or a later patch in that feature band).

## Run locally

Category and expense requests require a SQL Server instance (SQL Server 2022 is used in tests). Configure the connection using the standard environment variable below. This example uses Windows integrated authentication and trusts a local development certificate. Supply deployment credentials through your environment's secret configuration.

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

Open [the OpenAPI document](http://localhost:54131/openapi/v1.json). The document includes category and expense operations. The host validates the configured API documentation title at startup.

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

Application SQL connections select `us_english` as their session language. SQL Server includes constraint names in diagnostic text. Persistence recognizes the expected error number and English diagnostic prefix for the specific constraint. Unrecognized diagnostics propagate as database errors. Other connections and server-wide settings are unaffected.

## Expenses

Apply migration `20261120180100_AddExpenses` with the database update command above. Create a category first and use its ID. For example, given category 1:

```http
POST /api/expenses
Content-Type: application/json

{"amount":10.00,"currency":"USD","categoryId":1,"date":"2026-11-20","description":"Train ticket"}
```

The 201 response includes a Location and the saved expense. GET /api/expenses lists expenses by ID. GET, PUT and DELETE /api/expenses/{id} read, replace and delete one expense. PUT takes the same body as POST. Successful PUT and DELETE return 204.

Amounts range from 0.01 to 99,999,999.99 in whole cents. The API trims and uppercases three-letter ASCII currency codes. Dates are calendar dates and may be in the future. Omit the description, supply null, or send blank text for no description. Otherwise its trimmed length may be up to 500 UTF-16 code units.

The first expense binds the database to the configured base currency. EUR is the default. An identity conversion uses rate 1 without a quote. Foreign expenses store the eligible provider rate rounded half-to-even to 12 decimal places, provider, original publication time and fallback flag. Multiply by that stored rate and round half-to-even to cents. For example, 10 USD at 0.9255 EUR/USD becomes 9.26 EUR. An eligible fallback uses the same arithmetic.

Changing amount or currency obtains a new conversion. Date, category and description edits preserve the entire snapshot even during an outage. An unavailable rate returns 503 and leaves the expense unchanged. Invalid fields, missing category references and unsupported currencies return 400. Missing expenses return 404. Deleting a referenced category, changing the dataset base for a create/update, or encountering a conflicting concurrent edit returns 409.

## Solution layout

Under `src/`, Api composes the host. Application owns workflow contracts. Persistence owns EF Core, migrations and SQL Server access. Integrations owns the exchange-rate HTTP client and Caching coordinates refresh and fallback. Under `tests/`, Application, Integrations and Caching each have a unit-test project. Api.IntegrationTests exercises host behavior.

## Exchange-rate provider

The registered provider retrieves a daily table for the requested base currency and retains the upstream UTC publication time. It exposes base currency units per unit of the quoted currency. For example, an upstream quote of 1.25 USD per EUR maps to 0.8 EUR per USD. Category requests and host startup do not contact the provider. The HTTP timeout defaults to 10 seconds and can be configured with `ExchangeRateApi__TimeoutSeconds` (1–120). No API key is required. [Rates By Exchange Rate API](https://www.exchangerate-api.com)

## Rate caching and durable fallback

Apply the EF Core migrations using the database update command above before resolving rates. Migration `20261120155857_AddRateSnapshots` targets SQL Server 2022 and stores one complete normalized table per provider and dataset base. The composite primary key and conditional update protect newer publications during concurrent saves.

The resolver fetches on demand. A daily table stays fresh until its usable advertised next update. If the hint is missing, already past, or more than 30 hours after publication, the default refresh deadline is six hours after retrieval. The hard cutoff is always 48 hours from original publication. Re-fetching the same publication, reading SQL and serving fallback never extend that cutoff.

During an outage, callers share one refresh per process. Eligible stale data carries a fallback flag. Without eligible data, resolution fails with `RateUnavailableException`. The default retry delay is 20 minutes and the shared I/O timeout is 30 seconds. A caller can cancel its own wait. Different API instances can refresh independently. Each optional SQL operation gets at most one quarter of the shared timeout (7.5 seconds by default). A failed or timed-out save leaves an eligible live table in memory. Restart recovery depends on a successful durable save. After provider failure, sequential misses skip both SQL and HTTP until the retry deadline. The next request then checks SQL again, so another process can supply a recovered snapshot.

Configure the dataset base with `RateCache__BaseCurrency` (EUR by default). Duration overrides use `RateCache__RefreshInterval`, `RateCache__MaximumAge`, `RateCache__RetryInterval` and `RateCache__RefreshTimeout` in .NET TimeSpan format. The provider schedule takes precedence over the configured refresh interval while the hard cutoff applies to every table. Phase 4 exposes the resolver through dependency injection. Host startup and category endpoints remain independent of exchange-rate availability.

## Verify

```powershell
dotnet test SmartExpenseTracker.slnx --no-restore
dotnet format SmartExpenseTracker.slnx --no-restore --verify-no-changes
dotnet package list --project SmartExpenseTracker.slnx --vulnerable --include-transitive
```

The full suite requires a running Docker Linux engine with support for the SQL Server 2022 x86-64 image, enough memory for SQL Server, and access to pull the image. The SqlServer fixture starts a disposable container and migrates an empty database per test. For the Docker-independent subset use `dotnet test SmartExpenseTracker.slnx --filter 'TestCategory!=SqlServer'`. The host tests run without Docker, SQL Server or a live currency provider. In restricted Windows environments, VSTest may be unable to connect to its test host, and the formatter may be denied access to its build-host named pipe. Run those checks in a terminal or CI runner that permits local process communication. The vulnerability audit requires access to current advisory data.

See the [development workflow](AGENTS.md), [C# conventions](docs/DOTNET_BACKEND_INSTRUCTIONS.md) and [implementation plan](implementation-plan.md). The requirement source is the privately supplied assignment specification. Obtain it from the assignment owner and place the local copy under `docs/` when working on a phase.
