# SmartExpenseTracker development instructions

## Assignment compliance

The private original assignment DOCX under `docs/` is authoritative for functionality, optional work, evaluation criteria and delivery. This file records project boundaries and workflow. The [implementation plan](implementation-plan.md) records the approved approach and verification gates. Read and follow the [backend conventions](docs/DOTNET_BACKEND_INSTRUCTIONS.md) before modifying C# code.

Before starting **and before approving** each phase, read the original specification in full, this file, the plan and the backend conventions. Request the original if it is unavailable. Identify the applicable source section and requirement in the plan's traceability matrix. Distinguish mandatory outcomes and evaluation criteria, optional features, illustrative examples and project-specific decisions. Label unresolved assumptions explicitly.

Prefer the simplest maintainable approach that satisfies the assignment. Add functionality, dependencies or architectural complexity only when required for that outcome or explicitly approved. Preserve compatible approved decisions. If any decision conflicts with the assignment, report the discrepancy and obtain approval for the correction before changing the implementation.

During implementation and review, keep the requirement, responsible component and verification evidence linked. At phase close, compare actual behavior with the original, record deviations and remaining work, and check coverage of every evaluation criterion through final delivery. Explain substantial design changes before editing and report actual verification results.

## Allowed projects and references

Use exactly five production projects under src and four test projects under tests. The first five rows are production projects.

| Project | Direct project references (SmartExpenseTracker prefix omitted) |
|---|---|
| SmartExpenseTracker.Api | Application, Persistence, Integrations, Caching |
| SmartExpenseTracker.Application | None |
| SmartExpenseTracker.Persistence | Application |
| SmartExpenseTracker.Integrations | Application |
| SmartExpenseTracker.Caching | Application |
| SmartExpenseTracker.Application.UnitTests | Application |
| SmartExpenseTracker.Integrations.UnitTests | Integrations, Application |
| SmartExpenseTracker.Caching.UnitTests | Caching, Application |
| SmartExpenseTracker.Api.IntegrationTests | Api, Persistence, Application |

## Implementation rules

Api is the DI composition root. Keep controllers thin. Api owns HTTP contracts, validation, ProblemDetails, OpenAPI, request statistics middleware and its read-only endpoint. Application owns business workflows, models, expense/category rules, conversion policies, reporting orchestration and the interfaces for provider access, cached rate resolution and persistence.

Persistence owns EF Core, SQL Server, DbContext, configurations, migrations, relational queries, database-backed reports and durable last-known rate storage. Integrations owns the typed HttpClient, provider contracts, response validation, normalization, timeouts, cancellation and HTTP errors. Caching owns IMemoryCache, freshness, single-flight refresh, stale fallback orchestration and thread-safe shared state. Inject the provider and durable store through Application interfaces. Only Api wires their implementations together.

Use EUR as the configurable base and bind it to the dataset. For positive expenses, use current provider-published rates on create and on changes to original amount or currency. Use rate 1 locally for base-to-base conversion. Store original amount/currency, applied rate (base units per original unit), converted amount/base currency and available metadata such as provider and quote time. Preserve this snapshot on metadata-only edits, including expense date changes. Use decimal arithmetic and document precision and rounding. Historical quotes and refunds are outside initial scope.

Enforce category name uniqueness and restrict deletion of referenced categories in SQL Server. Filter expenses by date range, category and original currency. Sort by date or converted amount with an ascending ID tie-breaker, defaulting to ascending ID when no sort is specified. Reports aggregate persisted converted amounts across the whole dataset by category or by day, Monday-start week, or month.

Refresh on demand at a usable provider next-update time, or six hours from retrieval when that hint is unavailable. Enforce a separate 48-hour maximum age from original publication. Keep expired-freshness entries available until their stale limit. On a miss, recheck state after acquiring synchronization and coalesce refreshes per provider/base key. Validate quotes before replacing last-known data. Recover acceptable durable rates after restart. Never extend quote age on a fallback read. Reject invalid and excessively stale rates. Return 503 ProblemDetails when no acceptable rate exists. Resolve rates before opening a database transaction.

Propagate CancellationToken through supported I/O and allow each caller to cancel its wait independently. Bound synchronization state to configured keys or safely retire completed entries. Never run concurrent operations on one DbContext. Mandatory concurrency coverage includes shared cache, statistics and SQL constraints/atomic writes. ETags and rowversion are optional.

Measure completed requests, failures and response durations in Api middleware. Use a monotonic clock and a short lock around counter updates and snapshot reads. Expose a read-only aggregate snapshot. Counters reset on restart and cover one process.

Use .NET 10, ASP.NET Core, central package management in Directory.Packages.props, constructor injection, async I/O and validated strongly typed options. Avoid generic repositories, MediatR and unused abstractions. Keep JWT authentication, multi-user support and background services outside scope. Use only SmartExpenseTracker identifiers for project-owned code. Preserve unrelated changes and the supplied specification. Never import previous employer or project names, proprietary code, credentials, secrets or confidential configuration.

## Verification

Use NUnit, FakeItEasy and a license-approved FluentAssertions version. Pin the reviewed release centrally. Use controlled time, deterministic synchronization and fake HTTP responses. Use an isolated real SQL Server database, preferably Testcontainers, for API integration tests. All automated tests must run independently of live currency APIs.

Application.UnitTests covers validation, rate direction, precision, rounding and update policy. Integrations.UnitTests covers mapping, malformed responses, HTTP failures, timeouts and cancellation. Caching.UnitTests covers TTL boundaries, stale fallback, concurrent refresh, cancellation and failures. Api.IntegrationTests covers CRUD, persistence, constraints, filters, sorting, reports, errors and statistics. Keep OpenAPI and setup examples aligned with observable behaviour.
