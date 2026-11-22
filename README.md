# SmartExpenseTracker

An ASP.NET Core API for category and expense CRUD, automatic currency conversion, expense filtering/sorting, spending reports and request statistics. It uses .NET 10, EF Core and SQL Server, with NUnit, FakeItEasy and FluentAssertions tests. Authentication, multiple users and scheduled background refresh are optional assignment features and are not implemented.

## Prerequisites

- Git and the .NET 10 SDK specified in global.json (10.0.404 or a later patch in that feature band).
- SQL Server 2022 or later, with permission to create the development database and apply migrations. The durable-rate JSON constraint requires SQL Server 2022.
- Docker with a Linux engine and x86-64 SQL Server image support for integration tests or the local database option below.
- Network access to NuGet and container registries for setup; provider access for live foreign-currency writes. Automated tests use controlled providers.
- Commands below use PowerShell from the repository root.

## Run locally

```powershell
git clone https://github.com/mxezonakis/Smart-Expense-Tracker-with-Currency-Conversion.git
cd Smart-Expense-Tracker-with-Currency-Conversion
```

Category and expense requests require a SQL Server instance (SQL Server 2022 is used in tests). Configure the connection using the standard environment variable below. For an existing local server, configure Windows integrated authentication with the following command (or use the container alternative below). Supply deployment credentials through your environment's secret configuration.

```powershell
$env:ConnectionStrings__SmartExpenseTracker = 'Server=localhost;Database=SmartExpenseTracker;Integrated Security=true;TrustServerCertificate=true'
```

If you do not have SQL Server installed, start a development container and use SQL authentication instead of the integrated-authentication connection above:

```powershell
$env:MSSQL_SA_PASSWORD = 'Aa1!' + [Guid]::NewGuid().ToString('N')
docker run --detach --name SmartExpenseTracker-dev -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD -p 127.0.0.1:14333:1433 mcr.microsoft.com/mssql/server:2022-CU16-ubuntu-22.04
$env:ConnectionStrings__SmartExpenseTracker = "Server=localhost,14333;Database=SmartExpenseTracker;User ID=sa;Password=$env:MSSQL_SA_PASSWORD;Encrypt=true;TrustServerCertificate=true"
Remove-Item Env:MSSQL_SA_PASSWORD
docker logs SmartExpenseTracker-dev
```

Wait for "SQL Server is now ready for client connections" before applying migrations and starting the API. Keep the connection variable in the same shell. The generated password is for this local disposable database; do not commit it. Stop/start this container to retain its data; removing it deletes its database. Accepting the SQL Server container EULA is required. The HTTP startup command avoids a development HTTPS certificate dependency. For the optional HTTPS launch profile, configure/trust a .NET development certificate first.

From the repository root in PowerShell, after configuring one of the connections above:

```powershell
dotnet restore SmartExpenseTracker.slnx -p:NuGetAudit=true -p:NuGetAuditMode=all -warnaserror
dotnet build SmartExpenseTracker.slnx --configuration Release --no-restore -warnaserror

# Install once if dotnet-ef is unavailable
dotnet tool install --global dotnet-ef --version 10.0.9
dotnet ef database update --project src/SmartExpenseTracker.Persistence --startup-project src/SmartExpenseTracker.Api --configuration Release --no-build
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run --project src/SmartExpenseTracker.Api --configuration Release --no-build --no-launch-profile --urls http://localhost:54131
```

Open [Swagger UI](http://localhost:54131/swagger) for interactive documentation and manual testing. Swagger UI is enabled only in Development; keep production deployments in Production. It uses the existing [OpenAPI JSON document](http://localhost:54131/openapi/v1.json), whose availability is unchanged. The document covers all 13 business operations across categories, expenses, reports and statistics. The host validates the configured API documentation title at startup.

## API reference

| Endpoint | Methods | Successful responses |
|---|---|---|
| /api/categories | GET, POST | 200 array; 201 object with Location |
| /api/categories/{id} | GET, PUT, DELETE | 200 object; 204; 204 |
| /api/expenses | GET, POST | 200 array; 201 object with Location |
| /api/expenses/{id} | GET, PUT, DELETE | 200 object; 204 changed or 200 unchanged; 204 |
| /api/reports/by-category | GET | 200 category totals |
| /api/reports/by-period?period=day | GET | 200 period totals; also week/month |
| /api/statistics | GET | 200 statistics snapshot |

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

Apply all migrations with the database update command above, including the expense table and query indexes. Create a category first and use its ID. For example, given category 1:

```http
POST /api/expenses
Content-Type: application/json

{"amount":10.00,"currency":"USD","categoryId":1,"date":"2026-11-20","description":"Train ticket"}
```

A base-currency request can use the same body with currency EUR. Its response shape is:

```json
{"id":1,"amount":10,"currency":"EUR","categoryId":1,"date":"2026-11-20","description":"Train ticket","baseCurrency":"EUR","appliedRate":1,"convertedAmount":10,"provider":null,"publishedAt":null,"isFallback":false}
```

A USD response has the same fields with its applied rate, converted amount, provider publication timestamp and fallback flag. IDs and live rate values vary. The 201 response includes Location, the saved expense and its strong ETag. Individual GET responses also include ETag; the JSON body is unchanged. GET /api/expenses returns a JSON array of expenses. GET, PUT and DELETE /api/expenses/{id} read, replace and delete one expense. PUT takes the same body as POST. Changed-value PUT and successful DELETE return 204. An unchanged PUT returns 200 with `{"updated":false,"reason":"unchanged"}` and its unchanged ETag.

Example: `GET /api/expenses?from=2026-01-01&to=2026-01-31&categoryId=2&currency=usd&sortBy=amount&direction=asc`.

All query parameters are optional. Both calendar-date boundaries are inclusive and can be supplied independently. Filters combine by intersection. Currency trims surrounding whitespace, requires three ASCII letters, and matches the original currency using its uppercase code. By default the array contains every matching expense in ascending ID order. Supply `sortBy=date` or `sortBy=amount` (stored converted amount) to order by that field. An explicit sort defaults to descending. Use `direction=asc` or `direction=desc` with a sort field. Ties use ascending ID. An empty result returns `[]`. Invalid filters and sort options return HTTP 400 with validation details.

Amounts range from 0.01 to 99,999,999.99 in whole cents. The API trims and uppercases three-letter ASCII currency codes. Dates are calendar dates and may be in the future. Omit the description, supply null, or send blank text for no description. Otherwise its trimmed length may be up to 500 UTF-16 code units.

The first expense binds the database to the configured base currency. EUR is the default. An identity conversion uses rate 1 without a quote. Foreign expenses store the eligible provider rate rounded half-to-even to 12 decimal places, provider, original publication time and fallback flag. Multiply by that stored rate and round half-to-even to cents. For example, 10 USD at 0.9255 EUR/USD becomes 9.26 EUR. An eligible fallback uses the same arithmetic.

Changing amount or currency obtains a new conversion. Date, category and description edits preserve the entire snapshot even during an outage. An unavailable rate returns 503 and leaves the expense unchanged. Invalid fields, missing category references and unsupported currencies return 400. Individual GET and initially missing PUT resources return 404. Deleting a referenced category or changing the dataset base for a create/update returns 409. Expense precondition failures return 412.

### Conditional expense updates and deletes

Every expense PUT and DELETE requires `If-Match` containing exactly one quoted strong ETag from that expense's GET or POST response. Missing headers return 428; malformed tags, weak tags, `*` and multiple tags return 400. Tags are opaque to clients. A stale tag returns 412 without changing data or resolving exchange rates. Clients using the list endpoint must retrieve the individual expense and its ETag before editing; the collection does not supply individual version tokens.

For example, retrieve the expense and copy the actual ETag response header (the token below is illustrative):

```http
GET /api/expenses/1

HTTP/1.1 200 OK
ETag: "AAAAAAAAB9E="
```

Send that complete quoted value with the replacement body:

```http
PUT /api/expenses/1
If-Match: "AAAAAAAAB9E="
Content-Type: application/json

{"amount":10.00,"currency":"USD","categoryId":1,"date":"2026-11-20","description":"Updated ticket"}
```

A PUT that changes persisted values returns 204 and a new ETag. A normalized, validated replacement equal to the original returns 200 with `{"updated":false,"reason":"unchanged"}` and the initially read ETag; it performs no UPDATE and does not advance rowversion. For deletion, GET the current expense again and use its returned ETag (or use the new ETag from the successful PUT):

```http
DELETE /api/expenses/1
If-Match: "AAAAAAAAB9I="
```

Successful DELETE returns 204. A delete with a nonmatching version, including an already absent expense, returns 412. It uses one atomic SQL DELETE with both ID and expected version, without a preceding read.

PUT reads the expense and SQL Server rowversion once, compares it with the client's tag before validation/rate resolution, and preserves that version until the guarded UPDATE. Concurrent modification or deletion before a changed-value PUT is persisted also returns 412. An unchanged PUT checks the client version at its initial read but does not recheck afterward: it can return 200 even if another request changes or deletes the expense after that check. It never overwrites that change and adds no locks, transaction or write to detect it. Changed-value writes protect both GET-to-PUT edits and changes during PUT; competing edits are not merged. On 412, retrieve current data and reconcile before retrying. Existing clients must supply these headers; unconditional expense writes are no longer supported. Category operations are unchanged.


## Solution layout

Under `src/`, Api composes the host. Application owns workflow contracts. Persistence owns EF Core, migrations and SQL Server access. Integrations owns the exchange-rate HTTP client and Caching coordinates refresh and fallback. Under `tests/`, Application, Integrations and Caching each have a unit-test project. Api.IntegrationTests exercises host behavior.

## Exchange-rate provider

The registered provider is ExchangeRate-API Open Access at https://open.er-api.com/v6/latest/{base}. It retrieves the latest published daily table and retains its UTC publication time. Rates follow the provider update schedule; they are not streaming or tick-by-tick quotes. Repeated requests may return the same publication. It exposes base currency units per unit of the quoted currency. For example, an upstream quote of 1.25 USD per EUR maps to 0.8 EUR per USD. Category requests and host startup do not contact the provider. The HTTP timeout defaults to 10 seconds and can be configured with `ExchangeRateApi__TimeoutSeconds` (1–120). No API key is required. [Rates By Exchange Rate API](https://www.exchangerate-api.com)

## Rate caching and durable fallback

Apply the EF Core migrations using the database update command above before resolving rates. Migration `20261120155857_AddRateSnapshots` targets SQL Server 2022 and stores one complete normalized table per provider and dataset base. The composite primary key and conditional update protect newer publications during concurrent saves.

The resolver fetches on demand. A daily table stays fresh until its usable advertised next update. If the hint is missing, already past, or more than 30 hours after publication, the default refresh deadline is six hours after retrieval. The maximum quote age defaults to 48 hours from original publication and is configurable through `RateCache__MaximumAge`. Re-fetching the same publication, reading SQL and serving fallback never extend that cutoff.

During an outage, callers share one refresh per process. Eligible stale data carries a fallback flag. Without eligible data, resolution fails with `RateUnavailableException`. The retry cooldown defaults to 20 minutes for HTTP 429 Too Many Requests and 2 minutes for other handled refresh failures (other HTTP errors, network failures, timeouts/cancellation of the shared refresh, and invalid responses). The cooldown starts when the failure is handled; the first request at or after its deadline can refresh. No automatic Retry-After handling is performed. The shared I/O timeout remains 30 seconds. A caller can cancel its own wait. Different API instances can refresh independently. Each optional SQL operation gets at most one quarter of the shared timeout (7.5 seconds by default). A failed or timed-out save leaves an eligible live table in memory. Restart recovery depends on a successful durable save. After provider failure, sequential misses skip both SQL and HTTP until the retry deadline. The next request then checks SQL again, so another process can supply a recovered snapshot.

Configure the dataset base with `RateCache__BaseCurrency` (EUR by default). Duration overrides use `RateCache__RefreshInterval`, `RateCache__MaximumAge`, `RateCache__RetryInterval`, `RateCache__RateLimitRetryInterval` and `RateCache__RefreshTimeout` in .NET TimeSpan format. The provider schedule takes precedence over the configured refresh interval while the hard cutoff applies to every table. Host startup and category endpoints remain independent of exchange-rate availability. Both retry intervals must be positive and less than MaximumAge. For example, these PowerShell settings explicitly select the defaults:

```powershell
$env:RateCache__RetryInterval = '00:02:00'
$env:RateCache__RateLimitRetryInterval = '00:20:00'
```

An existing RetryInterval override now applies only to non-429 failures; configure RateLimitRetryInterval separately for HTTP 429. Neither cooldown extends a quote's maximum acceptable age.

## Verify

```powershell
dotnet test SmartExpenseTracker.slnx --configuration Release --no-build --no-restore
dotnet ef migrations has-pending-model-changes --project src/SmartExpenseTracker.Persistence --startup-project src/SmartExpenseTracker.Api --configuration Release --no-build
dotnet format SmartExpenseTracker.slnx --no-restore --verify-no-changes
dotnet package list --project SmartExpenseTracker.slnx --no-restore --vulnerable --include-transitive
```

The full suite requires a running Docker Linux engine with support for the SQL Server 2022 x86-64 image, enough memory for SQL Server, and access to pull the image. The SqlServer fixture starts a disposable container and migrates an empty database per test. For the Docker-independent subset use `dotnet test SmartExpenseTracker.slnx --filter 'TestCategory!=SqlServer'`. The host tests run without Docker, SQL Server or a live currency provider. In restricted Windows environments, VSTest may be unable to connect to its test host, and the formatter may be denied access to its build-host named pipe. Run those checks in a terminal or CI runner that permits local process communication. The vulnerability audit requires access to current advisory data.

See the [development workflow](AGENTS.md), [C# conventions](docs/DOTNET_BACKEND_INSTRUCTIONS.md) and [implementation plan](implementation-plan.md). The requirement source is the privately supplied assignment specification. Obtain it from the assignment owner and place the local copy under `docs/` when working on a phase.

## Spending reports

| GET endpoint | Successful response example |
|---|---|
| `/api/reports/by-category` | `[{"categoryId":1,"totalAmount":19.26,"baseCurrency":"EUR"}]` |
| `/api/reports/by-period?period=week` | `[{"periodStart":"2028-12-25","totalAmount":19.26,"baseCurrency":"EUR"}]` |

Reports cover the entire dataset. Category groups use ascending category ID. The required `period` parameter accepts exactly `day`, `week`, or `month` (lowercase). Periods use the stored expense date, including future dates, and appear in chronological order. The start is the expense date, the Monday of its week, or the first of its calendar month respectively. A missing or invalid period returns HTTP 400 with validation ProblemDetails.

Each total is the decimal sum of the already rounded converted amounts in the dataset's persisted base currency. For example, 9.26 EUR and 10.00 EUR in one category give 19.26 EUR. A zero-cent snapshot contributes zero and still establishes its group. Groups without expenses are omitted. Both endpoints return HTTP 200 with `[]` for an empty dataset. Reading a report never fetches rates or updates an expense.

## Request statistics

`GET /api/statistics` returns HTTP 200 with an aggregate snapshot for this running API instance.

```http
GET /api/statistics HTTP/1.1
```

```json
{"completedRequests":4,"failedRequests":1,"totalDurationMs":30.5,"averageDurationMs":7.625}
```

- `completedRequests` counts requests whose downstream pipeline returned a final status. Requests aborted before completion are excluded. Reading statistics, including with a query string, leaves the snapshot unchanged.
- `failedRequests` counts completed responses with final HTTP status 400 through 599, including errors produced by exception handling.
- `totalDurationMs` sums monotonic elapsed milliseconds across completed requests, including overlapping requests. It measures server pipeline execution through error handling and response generation.
- `averageDurationMs` is the total divided by the completed count in the same snapshot. A fresh instance returns zero for all four fields.

Each API process has its own singleton collector. A short lock keeps updates and snapshot reads consistent while requests execute concurrently. Counters reset when the host restarts.

## Errors and limits

Expected failures use application/problem+json: 400 for validation, 404 for missing resources, 409 for conflicts and 503 when no eligible conversion quote exists. Unexpected failures use a generic 500 response. For example, a referenced-category deletion returns a 409 ProblemDetails object with title "The category is referenced by an expense." Validation responses also include an errors object keyed by field.

The base currency is configured once for a dataset and remains bound after all expenses are deleted. Expenses are positive, single-user records; refunds and historical-rate lookup are outside the approved scope. Lists return all matches without pagination. Reports cover the whole dataset. Cache coordination and statistics are process-local; there is no distributed coordination or persistent statistics. Current fallback age limits and retry behavior are described above.

The submission is source-only. Restore dependencies from NuGet rather than including binaries. FluentAssertions is pinned to Apache-2.0 version 7.2.0; other dependencies retain their own licenses, including Microsoft native transitive components. The private assignment document is not required to build or run and is excluded from version control.
