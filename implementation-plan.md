# SmartExpenseTracker implementation plan

The assignment DOCX under docs defines the deliverable. Consult [AGENTS.md](AGENTS.md) for project boundaries and [the backend conventions](docs/DOTNET_BACKEND_INSTRUCTIONS.md) for C# implementation rules. This plan records the approved design and how to verify each phase.

## Approved project decisions and responsibilities

SmartExpenseTracker.slnx contains exactly five production projects under src and four test projects under tests. The first five rows are production projects.

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

Propagate CancellationToken through supported I/O and allow each caller to cancel its wait independently. Bound synchronization state to configured keys or safely retire completed entries. Never run concurrent operations on one DbContext. The approved project concurrency checks cover shared cache, statistics and SQL constraints/atomic writes. ETags and rowversion are optional.

Measure completed requests, failures and response durations in Api middleware. Use a monotonic clock and a short lock around counter updates and snapshot reads. Expose a read-only aggregate snapshot. Counters reset on restart and cover one process.

Use .NET 10, ASP.NET Core, central package management in Directory.Packages.props, constructor injection, async I/O and validated strongly typed options. Avoid generic repositories, MediatR and unused abstractions. Keep JWT authentication, multi-user support and background services outside scope. Use only SmartExpenseTracker identifiers for project-owned code. Preserve unrelated changes and the supplied specification. Never import previous employer or project names, proprietary code, credentials, secrets or confidential configuration.

## Operational contracts

Use the provider publication time to bound quote age when available. Document the timestamp basis for feeds without this field. Track freshness and retention separately. At the fresh deadline attempt a refresh. At the maximum stale deadline reject the old quote. Eviction from memory must still allow recovery from SQL. A failed refresh cannot overwrite a usable snapshot. The shared fetch has a bounded timeout independent of the first caller's cancellation. Obtain a separate database scope for shared refresh work.

For statistics, propose GET /api/statistics returning completed request count, failure count and cumulative/mean duration in milliseconds. Count completed 4xx and 5xx responses as failures. Exclude the statistics endpoint from measurement. Place measurement around error handling so it observes the final response status. Report a mean of zero for an empty sample. Derive the mean from a single consistent snapshot.

### Category contract (Phase 2)

SQL Server generates integer IDs. Trim leading and trailing .NET whitespace with String.Trim, then require 1â€“100 UTF-16 code units. Preserve case, accents and interior whitespace. Store this display name directly. A check constraint rejects empty names and outer Unicode whitespace even for direct SQL writes. The unique index uses Latin1_General_100_CI_AS_SC (case-insensitive and accent-sensitive) independent of database default collation. List by ascending ID. The unique index arbitrates concurrent inserts and renames.

POST /api/categories returns 201 with the resource and Location. GET collection/item returns 200. PUT replaces the name and DELETE removes the category, both returning 204. Missing items return 404, invalid writes return validation ProblemDetails (400), and duplicate creates/renames return conflict ProblemDetails (409). Phase 5 introduces the restrictive Expense foreign key and the referenced-category deletion conflict.

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
| 9. Final tests, documentation and submission | Complete deterministic suites, OpenAPI, request/response examples, setup, migration and test instructions and documented limitations. Prepare the GitHub repository for delivery (GitLab or a similar repository also satisfies the assignment). | Reproduce restore, database setup, build and tests from a clean checkout. Exercise documented examples and inspect the submission for confidential material. |

## Requirement traceability and Phase 1–2 alignment report

Reviewed against the private original, including its Requirements, Optional, Evaluation Criteria, Sample APIs and delivery instruction. The first column paraphrases the assignment. The remaining columns record our implementation and evidence. The phase gates above give the detailed test scenarios.

The reviewed Phase 2 head was `54d7e8d`. CI passed 6 application unit tests, 2 host tests and 10 real SQL Server integration tests. Provider, expense, cache, reporting and statistics behavior remains future work. [Phase 2 verification](https://github.com/mxezonakis/Smart-Expense-Tracker-with-Currency-Conversion/actions/runs/44749813794)

| Assignment requirement or evaluation criterion | Planned implementation and verification | Status after Phase 2 | Deviation, example or project decision | Corrective action or remaining gate |
|---|---|---|---|---|
| ASP.NET Core API, readable and maintainable code | Api composes Application and infrastructure. Review boundaries and test host startup. | Host, category controller and scoped persistence implemented. | .NET 10, MVC and the nine-project layout are approved choices. | Preserve boundaries in subsequent reviews. |
| Extensible provider integration and successful retrieval of real-time rates from at least one external API | Phase 3 typed HTTP adapter behind an Application contract. Integrations.UnitTests cover mapping, payloads and errors. | Integration project exists. No provider implemented. | ExchangeRate API and Fixer are examples. Historical data is a stated provider capability. Provider selection and publication cadence are open. | At Phase 3 verify a real provider can supply current rates and record evidence of successful integration. Keep automated tests independent of live services. |
| Expense CRUD with amount, currency, category, date and optional description | Phase 5 Application workflow, Persistence mapping and API round-trip tests. | Planned. | Positive amounts and update rules are approved decisions. | Verify all four operations and expenses with and without descriptions. |
| Expense-category CRUD | Phase 2 Application, Persistence and Api. Category API tests exercise create, list, retrieve, rename and delete. | Implemented and verified. | Integer IDs, trim/length rules, uniqueness and referenced-category restrictions are project decisions. | No Phase 2 correction. Verify restricted deletion in Phase 5 when expenses exist. |
| Durable data and efficient expense queries | Persistence schema and migrations in phases 2 and 5. Review SQL predicates and indexes in Phase 6. | Categories survive independent request scopes. Expense storage pending. | The assignment allows a database of choice. SQL Server and EF Core were approved. | Verify expense persistence and SQL filtering against the migrated schema. |
| Automatic, consistent and efficient base-currency conversion using external rates | Phases 3–5 resolve rates, cache them and persist the applied conversion. Application.UnitTests verify direction, rounding and updates. | Planned. | EUR and USD are examples. EUR, local identity conversion and stored snapshots are approved here. | Establish precision and rounding, then verify monetary and metadata-only edits. |
| Expense filters for date range, category and currency, and sorting by amount and date | Phase 6 SQL queries exposed by Api. Test combined filters and ties. | Planned. | Converted-amount sorting, bounded pagination and the ID tie-breaker are project decisions. | Resolve query boundaries and verify deterministic results. |
| Aggregated spending insights and correct reporting endpoints | Phase 7 Application orchestrates SQL totals. Reconcile filtered results against known amounts. | Planned. | Category and period totals are the supplied examples adopted by the project. Daily, weekly and monthly are example intervals. Monday-start weeks are our convention. | Verify totals across period boundaries and empty results. |
| Graceful external failures and effective fallback | Phases 3–5 validate upstream replies and recover acceptable last-known rates. Exercise failure paths with controlled responses and time. | Category HTTP errors verified. Provider failure handling pending. | Cached or last-known rates are examples. Durable fallback and 503 on exhaustion are approved choices. | Test refresh failure, restart and rejection of excessively stale data. |
| Effective caching with expiration and invalidation to reduce external calls | Phase 4 Caching refreshes or replaces entries and coordinates concurrent callers. Count upstream calls in Caching.UnitTests. | Planned. | In-memory caching, six-hour freshness and 48-hour stale retention are project defaults. | Reevaluate defaults against provider cadence and test both deadlines. |
| Appropriate async work, scalability and shared-state concurrency | Use cancellable I/O, independent scopes and SQL constraints. Test concurrent cache and statistics operations in phases 4 and 8. | Async category operations and concurrent duplicate creation verified. | Scoped DbContext and per-key refresh coordination are implementation choices. | Verify shared refresh and counter correctness as those components ship. |
| Request statistics with correct concurrent behavior (explicit evaluation criterion) | Phase 8 Api middleware and a read-only snapshot. Test concurrent successes and failures. | Planned. | Counts, timings, endpoint shape and single-process scope are project choices. | Verify exact counter deltas and document reset on restart. |
| Complete and correct unit tests | Application, Integrations and Caching unit suites plus API integration coverage as each phase ships. | Category rules, API contracts and SQL behavior covered. | NUnit, FakeItEasy, FluentAssertions and Testcontainers are approved tooling. | Add the relevant tests per phase and run the complete suite at submission. |
| Clear API documentation covering endpoints, request/response formats and setup | Api OpenAPI and README; Phase 9 walkthrough from a clean checkout. | Category contracts, setup, migration commands and examples available. | OpenAPI is the selected documentation mechanism. | Extend documentation alongside each implemented workflow and verify examples. |
| Deliver through GitHub, GitLab or a similar repository | Phase 9 submit the repository with reproducible setup and verification instructions. | GitHub review branch and PR exist. Final submission pending. | GitHub and the phased PR workflow are project choices. | Verify clean-checkout setup and deliver the repository link. |
| Optional features and optional evaluation criteria | JWT bearer authentication, isolated multi-user data and a scheduled rate refresh service. | All three excluded from the approved scope. | Optional in the assignment. | No implementation action without explicit approval. |

**Review outcome.** No Phase 1–2 production conflict identified. The previous matrix grouped category constraints and pagination under mandatory assignment requirements. This report attributes them to project decisions and separates provider examples from the required integration outcome. Future-phase rows record delivery work, not current defects.

## Decisions still to record

Select the provider/feed and verify its update cadence, quote timestamps and suitability for the configured windows. Specify SQL precision and scale, supported input limits and midpoint rounding with examples. Finalize future-date handling, date-range inclusivity, page defaults/maximum and report filter parameters. Resolve each detail before implementing its affected phase. Phase 1 pins FluentAssertions 7.2.0 (Apache-2.0) in Directory.Packages.props.
