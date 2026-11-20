# SmartExpenseTracker implementation plan

The assignment DOCX under docs defines the deliverable. Consult [AGENTS.md](AGENTS.md) for project boundaries and [the backend conventions](docs/DOTNET_BACKEND_INSTRUCTIONS.md) for C# implementation rules. This plan records the approved design and how to verify each phase.

## Structure and responsibilities

SmartExpenseTracker.slnx will contain exactly five production projects under src and four test projects under tests. The first five rows are production projects.

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

Api is the DI composition root. Keep controllers thin. Api owns HTTP contracts, validation, ProblemDetails, OpenAPI, request statistics middleware and its read-only endpoint. Application owns business workflows, models, expense/category rules, conversion policies, reporting orchestration and the interfaces for provider access, cached rate resolution and persistence.

Persistence owns EF Core, SQL Server, DbContext, configurations, migrations, relational queries, database-backed reports and durable last-known rate storage. Integrations owns the typed HttpClient, provider contracts, response validation, normalization, timeouts, cancellation and HTTP errors. Caching owns IMemoryCache, freshness, single-flight refresh, stale fallback orchestration and thread-safe shared state. Inject the provider and durable store through Application interfaces. Only Api wires their implementations together.

Use EUR as the configurable base and bind it to the dataset. For positive expenses, use current provider-published rates on create and on changes to original amount or currency. Use rate 1 locally for base-to-base conversion. Store original amount/currency, applied rate (base units per original unit), converted amount/base currency and available metadata such as provider and quote time. Preserve this snapshot on metadata-only edits, including expense date changes. Use decimal arithmetic and document precision and rounding. Historical quotes and refunds are outside initial scope.

Enforce category name uniqueness and restrict deletion of referenced categories in SQL Server. Filter expenses by date range, category and original currency. Sort by date or converted amount with a stable ID tie-breaker and bounded pagination. Apply relevant filters before summing persisted converted amounts by category or by day, Monday-start week or month.

Configure a 6-hour fresh TTL and 48-hour maximum stale age. Reevaluate both against the selected provider's update cadence. Keep expired-freshness entries available until their stale limit. On a miss, recheck state after acquiring synchronization and coalesce refreshes per provider/base key. Validate quotes before replacing last-known data. Recover acceptable durable rates after restart. Never extend quote age on a fallback read. Reject invalid and excessively stale rates. Return 503 ProblemDetails when no acceptable rate exists. Resolve rates before opening a database transaction.

Propagate CancellationToken through supported I/O and allow each caller to cancel its wait independently. Bound synchronization state to configured keys or safely retire completed entries. Never run concurrent operations on one DbContext. Mandatory concurrency coverage includes shared cache, statistics and SQL constraints/atomic writes. ETags and rowversion are optional.

Measure completed requests, failures and response durations in Api middleware. Use a monotonic clock and a short lock around counter updates and snapshot reads. Expose a read-only aggregate snapshot. Counters reset on restart and cover one process.

Use .NET 10, ASP.NET Core, central package management in Directory.Packages.props, constructor injection, async I/O and validated strongly typed options. Avoid generic repositories, MediatR and unused abstractions. Keep JWT authentication, multi-user support and background services outside scope. Use only SmartExpenseTracker identifiers for project-owned code. Preserve unrelated changes and the supplied specification. Never import previous employer or project names, proprietary code, credentials, secrets or confidential configuration.

## Operational contracts

Use the provider publication time to bound quote age when available. Document the timestamp basis for feeds without this field. Track freshness and retention separately. At the fresh deadline attempt a refresh. At the maximum stale deadline reject the old quote. Eviction from memory must still allow recovery from SQL. A failed refresh cannot overwrite a usable snapshot. The shared fetch has a bounded timeout independent of the first caller's cancellation. Obtain a separate database scope for shared refresh work.

For statistics, propose GET /api/statistics returning completed request count, failure count and cumulative/mean duration in milliseconds. Count completed 4xx and 5xx responses as failures. Exclude the statistics endpoint from measurement. Place measurement around error handling so it observes the final response status. Report a mean of zero for an empty sample. Derive the mean from a single consistent snapshot.

## Implementation phases

| Phase | Acceptance criteria | Verification gate |
|---|---|---|
| 0. Contracts and documented assumptions | Document API shapes, status codes, query limits, monetary boundaries and example conversions. Record provider assumptions and source of quote age. | Walk through a foreign expense, metadata edit, outage and week boundary against the contracts and matrix. |
| 1. Solution skeleton | Nine projects have exactly the references above, centralized packages and a .NET 10 API host with DI and options validation. | Restore and build the solution, inspect references and start the API in a host test. |
| 2. Category CRUD and persistence | Migrations create SQL Server tables and unique category names. Create, read, update and delete available categories. | Migrate an empty isolated database, round-trip CRUD and race duplicate inserts. Verify restriction with a referenced category once expense storage exists. |
| 3. Exchange-rate HTTP provider | Implement at least one actual external provider behind the Application contract. Normalize valid quotes and distinguish bad payloads, HTTP failures, timeouts and caller cancellation. | Run Integrations.UnitTests with representative payloads and a controlled HTTP handler, including reciprocal-rate mapping. |
| 4. Caching and durable fallback | Separate fresh and stale windows, persist last-known quotes, coalesce refreshes and keep synchronization bounded. | Advance fake time across both deadlines. Overlap callers, cancel one waiter, fail refresh and restart the host with the same SQL data. Verify call counts and fallback age. |
| 5. Expense CRUD and currency conversion | Persist required expense fields and conversion snapshots atomically. Reprice only on monetary changes. Reject invalid inputs and prevent deleting referenced categories. | Run business rule tests and SQL-backed CRUD. Compare snapshots before/after edits. Verify identity conversion makes zero upstream calls and 503 leaves data intact. |
| 6. Filtering, sorting and pagination | Combine date, category and currency filters. Support date and converted-amount sorts and bounded deterministic pages. Add indexes suited to query predicates. | Query seeded mixed currencies and tied sort values in SQL Server. Check page limits, page boundaries and combined filters. Inspect generated query and schema. |
| 7. Reporting | Return spending per category and per daily, Monday-start weekly or monthly period using filtered stored conversions. | Reconcile SQL totals against fixture amounts across Sunday/Monday and month boundaries, including empty ranges. Verify zero provider calls. |
| 8. Request statistics | Ship middleware and a read-only endpoint with consistent counts, failures and durations per instance. | Send concurrent successes and failures to an isolated host. Assert exact deltas, coherent duration aggregates and exclusion of snapshot reads. |
| 9. Final tests, documentation and submission | Complete deterministic suites, OpenAPI, request/response examples, setup, migration and test instructions and documented limitations. Prepare the required Git-hosted submission. | Reproduce restore, database setup, build and tests from a clean checkout. Exercise documented examples and inspect the submission for confidential material. |

## Requirement traceability

Use NUnit, FakeItEasy and a license-approved FluentAssertions version. Pin the reviewed release centrally. Use controlled time, deterministic synchronization and fake HTTP responses. Use an isolated real SQL Server database, preferably Testcontainers, for API integration tests. All automated tests must run independently of live currency APIs.

Test suite names below use their project suffix. API tests exercise the composed host and SQL Server.

| Mandatory requirement or evaluation criterion | Responsible component | Verification |
|---|---|---|
| ASP.NET Core API, maintainable structure and replaceable provider | Api composition, Application contracts | Api.IntegrationTests host startup with a controlled upstream, phase 1 reference inspection |
| Actual latest-rate HTTP integration | Integrations | Integrations.UnitTests for request, mapping, direction, malformed responses, upstream errors, timeouts and cancellation |
| Expense CRUD with amount, currency, category, date and optional description | Application, Persistence, Api | Api.IntegrationTests round-trip each operation with and without description |
| Category CRUD, uniqueness and deletion restrictions | Application, Persistence, Api | Api.IntegrationTests for CRUD, duplicate writes and referenced deletion |
| Persistent data, efficient schema and filtering | Persistence | Api.IntegrationTests for migrations, data across scopes, SQL constraints and combined filters, phase 6 index review |
| Consistent automatic base conversion | Application, Integrations | Application.UnitTests for positive validation, rate direction, precision, rounding, identity rate and snapshot edit policy |
| Efficient conversion, freshness, invalidation and avoidance of redundant calls | Caching | Caching.UnitTests for fresh hits, expiry, single-flight refresh, replacement and independent waiter cancellation |
| Graceful errors and last-known rates | Caching, Persistence, Api | Caching.UnitTests for failed/invalid refresh and stale cutoff, Api.IntegrationTests for restart recovery and 503 ProblemDetails |
| Date/category/currency filters, amount/date sorting and bounded pagination | Application, Persistence, Api | Api.IntegrationTests for combined filters, equal sort keys, limits and deterministic ordering |
| Category and time-period spending totals | Application, Persistence, Api | Api.IntegrationTests for filtered category/day/week/month totals from stored conversions |
| Async I/O, appropriate parallelism and shared-state correctness | Integrations, Caching, Api, Persistence | Overlapping refresh and cancellation tests, concurrent statistics and database constraint tests, async call-chain review |
| Request statistics | Api | Api.IntegrationTests for concurrent counts, failures, durations and snapshot reads |
| Unit-test reliability and important HTTP workflows | All four test suites | Phase 9 deterministic full run with controlled HTTP and isolated SQL Server |
| API formats, setup instructions and repository delivery | Api OpenAPI, README, submission repository | Phase 9 clean-checkout walkthrough of documented commands and HTTP examples |

## Decisions still to record

Select the provider/feed and verify its update cadence, quote timestamps and suitability for the configured windows. Specify SQL precision and scale, supported input limits and midpoint rounding with examples. Finalize future-date handling, category name normalization, date-range inclusivity, page defaults/maximum and report filter parameters. Resolve each detail before implementing its affected phase. Phase 1 pins FluentAssertions 7.2.0 (Apache-2.0) in Directory.Packages.props.
