# SmartExpenseTracker implementation plan

The assignment DOCX under docs defines the deliverable. Consult [AGENTS.md](AGENTS.md) for project boundaries and [the backend conventions](docs/DOTNET_BACKEND_INSTRUCTIONS.md) for C# implementation rules. This plan records the approved design and how to verify each phase.

## Approved project decisions and responsibilities

SmartExpenseTracker.slnx contains exactly five production projects under src and four test projects under tests. The first five rows are production projects.

| Project | Direct project references (SmartExpenseTracker prefix omitted) |
|---|---|
| SmartExpenseTracker.Api | Application, Persistence, Integrations, Caching |
| SmartExpenseTracker.Application | Caching, Persistence |
| SmartExpenseTracker.Persistence | None |
| SmartExpenseTracker.Integrations | None |
| SmartExpenseTracker.Caching | Integrations, Persistence |
| SmartExpenseTracker.Application.UnitTests | Application, Caching, Persistence |
| SmartExpenseTracker.Integrations.UnitTests | Integrations |
| SmartExpenseTracker.Caching.UnitTests | Caching, Integrations, Persistence |
| SmartExpenseTracker.Api.IntegrationTests | Api, Caching, Integrations, Persistence |

Api is the DI composition root. Keep controllers thin. Api owns HTTP contracts, validation, ProblemDetails, OpenAPI, request statistics middleware and its read-only endpoint. Application owns business workflows, application inputs, expense/category rules, conversion policies and reporting orchestration. It consumes the interfaces exposed by Caching and Persistence.

Persistence owns all store interfaces, their records and failure types, EF Core, SQL Server, DbContext, configurations, migrations, relational queries, database-backed reports and durable last-known rate storage. Integrations owns IExchangeRateProvider, ExchangeRateTable, the typed HttpClient, response validation, normalization, timeouts, cancellation and HTTP errors. Caching owns IRateResolver, ResolvedRates, RateUnavailableException, IMemoryCache, freshness, single-flight refresh, stale fallback orchestration and thread-safe shared state. It consumes the provider and store interfaces from their owning projects and maps ExchangeRateTable to Persistence's StoredExchangeRateTable. Api wires the implementations together.

Use EUR as the configurable base and bind it to the dataset. For positive expenses, use current provider-published rates on create and on changes to original amount or currency. Use rate 1 locally for base-to-base conversion. Store original amount/currency, applied rate (base units per original unit), converted amount/base currency and available metadata such as provider and quote time. Preserve this snapshot on metadata-only edits, including expense date changes. Use decimal arithmetic and document precision and rounding. Historical quotes and refunds are outside initial scope.

Enforce category name uniqueness and restrict deletion of referenced categories in SQL Server. Filter expenses by date range, category and original currency. Sort by date or converted amount with an ascending ID tie-breaker, defaulting to ascending ID when no sort is specified. Sum persisted converted amounts across the dataset by category or by day, Monday-start week or month.

Refresh on demand at a usable provider next-update time, or six hours from retrieval when that hint is unavailable. Enforce a separate 48-hour maximum age from original publication. Keep expired-freshness entries available until their stale limit. On a miss, recheck state after acquiring synchronization and coalesce refreshes per provider/base key. Validate quotes before replacing last-known data. Recover acceptable durable rates after restart. Never extend quote age on a fallback read. Reject invalid and excessively stale rates. Return 503 ProblemDetails when no acceptable rate exists. Resolve rates before opening a database transaction.

Propagate CancellationToken through supported I/O and allow each caller to cancel its wait independently. Bound synchronization state to configured keys or safely retire completed entries. Never run concurrent operations on one DbContext. The approved project concurrency checks cover shared cache, statistics and SQL constraints/atomic writes. ETags and rowversion are optional.

Measure completed requests, failures and response durations in Api middleware. Use a monotonic clock and a short lock around counter updates and snapshot reads. Expose a read-only aggregate snapshot. Counters reset on restart and cover one process.

Use .NET 10, ASP.NET Core, central package management in Directory.Packages.props, constructor injection, async I/O and validated strongly typed options. Avoid generic repositories, MediatR and unused abstractions. Keep JWT authentication, multi-user support and background services outside scope. Use only SmartExpenseTracker identifiers for project-owned code. Preserve unrelated changes and the supplied specification. Never import previous employer or project names, proprietary code, credentials, secrets or confidential configuration.

## Operational contracts

Use the provider publication time to bound quote age when available. Document the timestamp basis for feeds without this field. Track freshness and retention separately. At the fresh deadline attempt a refresh. At the maximum stale deadline reject the old quote. Eviction from memory must still allow recovery from SQL. A failed refresh cannot overwrite a usable snapshot. The shared fetch has a bounded timeout independent of the first caller's cancellation. Obtain a separate database scope for shared refresh work.

GET /api/statistics returns a consistent host-local snapshot: `completedRequests`, `failedRequests`, `totalDurationMs` and `averageDurationMs`. The middleware times downstream execution with a monotonic clock and records final statuses after error handling. Completed HTTP 400–599 responses are failures. Aborted requests without completion and GET statistics reads (including query strings) are excluded. A short lock protects compound updates and reads. Mean milliseconds equal total milliseconds divided by completed requests, or zero for an empty sample. All counters reset on restart. See the [HTTP and JSON example](README.md#request-statistics).

### Category contract (Phase 2)

SQL Server generates integer IDs. Trim leading and trailing .NET whitespace with String.Trim, then require 1-100 UTF-16 code units. Preserve case, accents and interior whitespace. Store this display name directly. A check constraint rejects empty names and outer Unicode whitespace even for direct SQL writes. The unique index uses Latin1_General_100_CI_AS_SC (case-insensitive and accent-sensitive) independent of database default collation. List by ascending ID. The unique index arbitrates concurrent inserts and renames.

POST /api/categories returns 201 with the resource and Location. GET collection/item returns 200. PUT replaces the name and DELETE removes the category, both returning 204. Missing items return 404, invalid writes return validation ProblemDetails (400), and duplicate creates/renames return conflict ProblemDetails (409). Phase 5 introduces the restrictive Expense foreign key and the referenced-category deletion conflict.

## Implementation phases

| Phase | Acceptance criteria | Verification gate |
|---|---|---|
| 0. Contracts and documented assumptions | Document API shapes, status codes, query limits, monetary boundaries and example conversions. Record provider assumptions and source of quote age. | Walk through a foreign expense, metadata edit, outage and week boundary against the contracts and matrix. |
| 1. Solution skeleton | Nine projects have exactly the references above, centralized packages and a .NET 10 API host with DI and options validation. | Restore and build the solution, inspect references and start the API in a host test. |
| 2. Category CRUD and persistence | Migrations create SQL Server tables and unique category names. Create, read, update and delete available categories. | Migrate an empty isolated database, round-trip CRUD and race duplicate inserts. Verify restriction with a referenced category once expense storage exists. |
| 3. Exchange-rate HTTP provider | Implement at least one actual external provider behind the Integrations-owned IExchangeRateProvider contract. Normalize valid quotes and distinguish bad payloads, HTTP failures, timeouts and caller cancellation. | Run Integrations.UnitTests with representative payloads and a controlled HTTP handler, including reciprocal-rate mapping. |
| 4. Caching and durable fallback | Separate fresh and stale windows, persist last-known quotes, coalesce refreshes and keep synchronization bounded. | Advance fake time across both deadlines. Overlap callers, cancel one waiter, fail refresh and restart the host with the same SQL data. Verify call counts and fallback age. |
| 5. Expense CRUD and currency conversion | Persist required expense fields and conversion snapshots atomically. Reprice only on monetary changes. Reject invalid inputs and prevent deleting referenced categories. | Run business rule tests and SQL-backed CRUD. Compare snapshots before/after edits. Verify identity conversion makes zero upstream calls and 503 leaves data intact. |
| 6. Filtering and sorting | Combine date, category and currency filters. Support date and converted-amount sorts. Return all matching expenses. Add indexes suited to query predicates. | Query seeded mixed currencies and tied sort values in SQL Server. Check complete result sets above 100 matches, combined filters and default ID order. Inspect generated query and schema. |
| 7. Reporting | Return spending per category and per daily, Monday-start weekly or monthly period using persisted converted amounts across the dataset. | Reconcile SQL totals against fixture amounts across Sunday/Monday and month boundaries, including an empty dataset. Verify zero provider calls. |
| 8. Request statistics | Ship middleware and a read-only endpoint with consistent counts, failures and durations per instance. | Send concurrent successes and failures to an isolated host. Assert exact deltas, coherent duration aggregates and exclusion of snapshot reads. |
| 9. Final tests, documentation and submission | Complete deterministic suites, OpenAPI, request/response examples, setup, migration and test instructions and documented limitations. Prepare the GitHub repository for delivery (GitLab or a similar repository also satisfies the assignment). | Reproduce restore, database setup, build and tests from a clean checkout. Exercise documented examples and inspect the submission for confidential material. |

## Historical requirement traceability and phase alignment report

Reviewed against the private original, including its Requirements, Optional, Evaluation Criteria, Sample APIs and delivery instruction. The first column paraphrases the assignment. The remaining columns record our implementation and evidence. The phase gates above give the detailed test scenarios.

Architecture descriptions and test results in this historical report refer to those phases. The current reference direction and interface ownership are recorded above and in the architecture revision below.

The reviewed Phase 2 head was `54d7e8d`. CI passed 6 application unit tests, 2 host tests and 10 real SQL Server integration tests. Provider, expense, cache, reporting and statistics behavior remains future work. [Phase 2 verification](https://github.com/mxezonakis/Smart-Expense-Tracker-with-Currency-Conversion/actions/runs/44749813794)

| Assignment requirement or evaluation criterion | Planned implementation and verification | Status after Phase 2 | Deviation, example or project decision | Corrective action or remaining gate |
|---|---|---|---|---|
| ASP.NET Core API, readable and maintainable code | Api composes Application and infrastructure. Review boundaries and test host startup. | Host, category and expense controllers and scoped persistence implemented. | .NET 10, MVC and the nine-project layout are approved choices. | Preserve boundaries in subsequent reviews. |
| Extensible provider integration and successful retrieval of real-time rates from at least one external API | Phase 3 typed HTTP adapter behind an Application contract. Integrations.UnitTests cover mapping, payloads and errors. | Phase 3 integrated ExchangeRate-API Open Access. | ExchangeRate API and Fixer are examples. Historical data is a stated provider capability. The selected provider publishes approximately daily. | At Phase 3 verify a real provider can supply current rates and record evidence of successful integration. Keep automated tests independent of live services. |
| Expense CRUD with amount, currency, category, date and optional description | Phase 5 Application workflow, Persistence mapping and API round-trip tests. | Implemented. SQL Server runtime verification pending. | Positive amounts and update rules are approved decisions. | Verify all four operations and expenses with and without descriptions. |
| Expense-category CRUD | Phase 2 Application, Persistence and Api. Category API tests exercise create, list, retrieve, rename and delete. | Implemented and verified. | Integer IDs, trim/length rules, uniqueness and referenced-category restrictions are project decisions. | No Phase 2 correction. Phase 5 adds a restrictive foreign key and HTTP conflict test. SQL runtime verification is pending. |
| Durable data and efficient expense queries | Persistence schema and migrations in phases 2 and 5. Review SQL predicates and indexes in Phase 6. | Categories survive independent request scopes. Expense mapping and migration implemented. Expense SQL runtime verification pending. | The assignment allows a database of choice. SQL Server and EF Core were approved. | Verify expense persistence and SQL filtering against the migrated schema. |
| Automatic, consistent and efficient base-currency conversion using external rates | Phases 3–5 resolve rates, cache them and persist the applied conversion. Application.UnitTests verify direction, rounding and updates. | Implemented. SQL Server runtime verification pending. | EUR and USD are examples. EUR, local identity conversion and stored snapshots are approved here. | Establish precision and rounding, then verify monetary and metadata-only edits. |
| Expense filters for date range, category and currency, and sorting by amount and date | Phase 6 SQL queries exposed by Api. Test combined filters and ties. | Implemented in ExpenseStore and GET /api/expenses. EF SQL translation, index DDL and model/snapshot consistency verified offline. Docker access blocked SQL Server runtime verification. | Converted-amount sorting and the ID tie-breaker are project decisions. | ExpenseApiTests covers inclusive combined filters, stored amounts, ties, default ID order and complete result sets. ApiHostTests covers invalid query parameters. |
| Aggregated spending insights and correct reporting endpoints | Phase 7 Application orchestrates SQL totals. ReportService and ReportStore sum persisted decimals. | Implemented; live SQL verification pending. | Category and period totals are the supplied examples adopted by the project. Daily, weekly and monthly are example intervals. Monday-start weeks are our convention. | ExpenseApiTests exercises mixed currencies, zero-cent groups, calendar boundaries, snapshot preservation and SQL GROUP BY/SUM. ReportServiceTests and ApiHostTests cover period validation and forwarding. |
| Graceful external failures and effective fallback | Phases 3–5 validate upstream replies and recover acceptable last-known rates. Exercise failure paths with controlled responses and time. | The resolver supports eligible fallback. Expense writes map exhaustion to HTTP 503. | Cached or last-known rates are examples. Durable fallback and 503 on exhaustion are approved choices. | Test refresh failure, restart and rejection of excessively stale data. |
| Effective caching with expiration and invalidation to reduce external calls | Phase 4 shares refreshes and prefers an eligible provider next-update hint. With an unusable hint, refresh is due six hours after retrieval. | Phase 4 caching and durable fallback implemented following the Phase 2 review. | The refresh interval, SQL Server last-known storage and the 48-hour maximum quote age are approved engineering choices. The age limit runs from original publication, including after repeated retrieval or restart. | Phase 4 CI verified the cache and durable fallback. |
| Appropriate async work, scalability and shared-state concurrency | Use cancellable I/O, independent scopes and SQL constraints. Test concurrent cache and statistics operations in phases 4 and 8. | Async category operations and concurrent duplicate creation verified. | Scoped DbContext and per-key refresh coordination are implementation choices. | Verify shared refresh and counter correctness as those components ship. |
| Request statistics with correct concurrent behavior (explicit evaluation criterion) | Phase 8 Api middleware and a read-only snapshot. Test concurrent successes and failures. | Implemented in Phase 8. | Counts, timings, endpoint shape and single-process scope are project choices. | RequestStatisticsTests verify atomic updates and snapshots. StatisticsApiTests verify overlapping requests, final error statuses, excluded reads and host isolation. |
| Complete and correct unit tests | Application, Integrations and Caching unit suites plus API integration coverage as each phase ships. | Category rules, API contracts and SQL behavior covered. | NUnit, FakeItEasy, FluentAssertions and Testcontainers are approved tooling. | Add the relevant tests per phase and run the complete suite at submission. |
| Clear API documentation covering endpoints, request/response formats and setup | Api OpenAPI and README; Phase 9 walkthrough from a clean checkout. | Category and expense contracts, setup, migration commands and examples available. | OpenAPI is the selected documentation mechanism. | Extend documentation alongside each implemented workflow and verify examples. |
| Deliver through GitHub, GitLab or a similar repository | Phase 9 submit the repository with reproducible setup and verification instructions. | GitHub review branch and PR exist. Final submission pending. | GitHub and the phased PR workflow are project choices. | Verify clean-checkout setup and deliver the repository link. |
| Optional features and optional evaluation criteria | JWT bearer authentication, isolated multi-user data and a scheduled rate refresh service. | All three excluded from the approved scope. | Optional in the assignment. | No implementation action without explicit approval. |

**Review outcome.** No Phase 1–2 production conflict identified. The previous matrix grouped category constraints under mandatory assignment requirements. This report attributes them to project decisions and separates provider examples from the required integration outcome. Future-phase rows record delivery work, not current defects.

## Phase 3 implementation and verification

The selected provider is the ExchangeRate-API open access daily feed. The adapter retrieves the latest table for the requested base, uses the provider's Unix publication timestamp and returns immutable decimal reciprocals in base units per original unit. A manual call through the registered adapter retrieved 166 EUR-based entries published at 00:02:32 UTC on 20 November 2026. Automated provider tests use controlled HTTP responses. [Provider contract](https://www.exchangerate-api.com/docs/free)

The daily feed can return the same publication at several successive six-hour checks. The Phase 4 policy below uses the advertised schedule to avoid these requests.

Phase 3 accepts an upstream UTC publication timestamp up to two minutes ahead of the local UTC clock, inclusive, to tolerate minor clock differences. The adapter preserves the supplied timestamp and rejects a later publication as invalid data. This tolerance is a project decision.

The Open Access contract documents an error envelope and HTTP 429 for rate limiting. Since it does not specify the status for every error code, the adapter recognizes a declared failure on either successful or unsuccessful HTTP statuses. It retains the HTTP status and a recognized error code (including `unsupported-code`), mapping unrecognized codes to `unknown`. An unsuccessful status with no usable error envelope remains an HTTP failure. A successful status with malformed success data is invalid provider data.

## Phase 4 implementation and verification

The assignment requires caching to reduce redundant external calls, expiration and invalidation, graceful provider failures, and safe shared state. Memory caching, SQL Server durability, these time limits and the single configured provider/base pair are project choices.

- Publication time is the upstream UTC timestamp. Retrieval time records a successful fetch. SQL recovery and fallback preserve both. A successful repeat can advance retrieval time only.
- Refresh on the first request at or after the advertised next update if it follows both publication and retrieval and falls within 30 hours of publication. This project-specific sanity limit allows schedule variation around the daily update. Otherwise refresh six hours after retrieval. A schedule hint can delay detection of an unscheduled correction.
- At exactly 48 hours from publication the quote is ineligible, even when retrieved again recently. Refresh and retention are checked separately on every resolution. A usable, newly obtained table replaces the complete previous snapshot.
- A singleton resolver coordinates one on-demand task for the configured provider and dataset base. The 30-second shared deadline covers cooperative SQL and HTTP I/O. Cancelling a request stops its own wait. During a failed refresh the resolver permits eligible stale data and waits 20 minutes before another provider attempt. Without an eligible table it raises RateUnavailableException for future API mapping.
- A fresh SQL snapshot can serve a cold process immediately. A stale one can serve after refresh failure. A SQL read error allows an upstream attempt. A SQL write error is logged and a valid live table can serve from memory, with restart durability dependent on a successful write. A transaction holds an update/range lock while decoding the existing row and replacing it. Invalid rows, including duplicate currency keys, have no ordering priority. Valid rows use publication then retrieval time. Optional SQL reads and saves each receive a quarter of the overall refresh budget. During provider suppression, misses skip durable reads until the retry deadline.
- Verification covers refresh, expiration and retry boundaries under controlled time, overlapping callers, cancellation, shared deadline, invalid data, memory eviction and restart. Real SQL Server tests migrate an empty database, race competing replacements through independent contexts and exercise restarted hosts on both sides of the expiration boundary. Memory, retry suppression and request coalescing are per process.

## Recorded contract decisions

Phase 6 uses inclusive date boundaries. Reports cover the whole dataset. Phase 1 pins FluentAssertions 7.2.0 (Apache-2.0) in Directory.Packages.props.

### Phase 5 monetary and HTTP contract

The assignment requires persisted expense CRUD and automatic base-currency conversion. The following limits and snapshot rules are project implementation choices.

- Original amount is a JSON number from 0.01 through 99,999,999.99 in increments of 0.01, stored as decimal(10,2). Reject fractional cents.
- Trim the currency and uppercase three ASCII letters. A code absent from an eligible table is invalid (400). A missing eligible table or unusable rate gives 503. Identity uses rate 1, no provider metadata or fallback, and no resolver call.
- Define the applied rate as base units per original unit rounded half-to-even to 12 fractional places, stored as decimal(18,12). Reject rates rounding to zero or reaching 1,000,000. Multiply the original amount by this exact stored rate, then round once to cents, half-to-even, for decimal(16,2). The operand bounds require at most 28 significant digits. For example 10.00 USD at 0.9255 yields 9.26 EUR, and 0.01 at 0.5 yields 0.00.
- Date is a required ISO calendar date, including future dates. Trim optional description, treating omitted/null/blank as null, with a 500 UTF-16 code unit limit after trimming. Category must exist (400). Preserve provider, UTC publication, rate and fallback flag with each foreign conversion. Reprice only after amount or normalized currency changes, independently of expense date.
- Bind the configured base (default EUR) on the first successful expense write for the dataset. Keep that binding even after all expenses are removed. Reject creates and updates under a conflicting base (409). Before resolving a foreign quote for creation, check for an existing mismatch outside a transaction. At first insertion, serialize competing initializers and commit the binding and expense together. A failed first insert rolls both back.
- POST returns 201 with Location, GET returns 200, PUT and DELETE return 204. Missing expenses return 404. Concurrent conflicting edits and deletion of referenced categories return 409. Errors use ProblemDetails.

## Historical Phase 9 final verification — 21 November 2026

This section supersedes the earlier phase-time verification statuses above. The original specification was reread in full. Mandatory functionality and evaluation criteria are implemented; optional JWT authentication, isolated users and background refresh remain unselected. Provider names, example currencies and report intervals in the assignment are examples; SQL Server, EUR, stored snapshots, Monday-start weeks, publication-age limits and deterministic ordering are approved project decisions. Pagination is not required and is absent.

A fresh clone of approved Phase 8 merge `41d8a0fe1870c696e44e6814e2ab5ada518139ea` was verified on branch `feature/phase-9-final-verification`. Existing checkouts and stashes were preserved. The [approved main CI](https://github.com/mxezonakis/Smart-Expense-Tracker-with-Currency-Conversion/actions/runs/44826262945) passed; Phase 9 verification below was also executed locally, not inferred from that run.

| Area | Implementation and evidence | Final preparation status |
|---|---|---|
| API, CRUD and persistence | Five production/four test projects; scoped services and DbContext; CategoryApiTests and ExpenseApiTests; real HTTP create/read/update/delete walkthrough | Passed |
| Conversion and efficient external integration | ExchangeRateApiClient, ExpenseService and RateResolver; provider, application, cache and durable SQL tests; live 10 USD write stored 9.11 EUR at applied rate 0.910747641847 | Passed; provider publication 2026-11-20T00:02:32Z, daily rather than streaming |
| Caching, failure handling and concurrency | Controlled clock/provider tests cover refresh, retry, single-flight, cancellation, 48-hour publication cutoff and durable restart recovery | Passed; no deliberate live-provider outage |
| Filters and sorts | ExpenseStore SQL predicates/order; inclusive AND filters, normalized original currency, converted amounts, date, stable ID ties and 101-result regression | Passed; no pagination or read-time rate resolution |
| Reporting | ReportStore SQL GROUP BY/SUM; mixed currencies, zero-cent groups, day/week/month and year boundaries; real HTTP reports | Passed |
| Statistics | RequestStatistics and middleware; atomic updates, gated concurrency, positive duration, escaping exceptions, cancellation, handled errors and independent hosts | Passed |
| Fresh database | All four published migrations applied in order; exact index-key assertions, decimal persistence, restrictive FK, unique names, rollback and initializer races | Passed; EF reports no pending model changes |
| Reproducible setup and documentation | Clean clone, audited restore, pinned EF tool, disposable SQL Server, actual API/OpenAPI and restart walkthrough; README prerequisites, setup and response examples corrected | Passed |
| Complete suite | Application 37, Caching 22, Integrations 34, API 56 (including 29 real SQL Server tests) | 149 passed, 0 failed, 0 skipped; no Phase 9 test additions |
| Quality gates | Release warning-as-error build, full solution formatting, direct/transitive vulnerability audit | Passed; build 0 warnings/errors, no known vulnerable packages reported |
| Submission safety | Tracked source/configuration/docs reviewed; private assignment, credentials and generated outputs excluded; dependency license metadata inspected | Source-only delivery; retain dependency license terms |

The live walkthrough also verified snapshot-preserving metadata edits, stored data after restart, dataset base binding after deleting all expenses, conflict/validation/not-found responses and statistics reset/exclusion. Automated tests remain independent of the live provider. Testcontainers failures are not counted as successful SQL verification: all 29 SQL cases actually passed here.

Only README and this plan were corrected in Phase 9. No production implementation, tests, packages or published migrations changed. Final submission and any commit/push remain gated on independent final review. Optional features would add security/schema or scheduling/lifecycle complexity and delay delivery; none is necessary to satisfy mandatory requirements.

## Approved extension ? conditional expense writes

This section supersedes the historical expense PUT/DELETE response and concurrency descriptions above.

The assignment's concurrency evaluation criterion is addressed by the existing cache, statistics and SQL tests. ETag/If-Match is an explicitly selected additional consistency decision, not a newly inferred mandatory assignment feature. It extends the single-read expense update in the same worktree without schema changes.

- API owns bounded parsing of exactly one quoted strong ETag, response headers and 400/428/412 ProblemDetails. Swagger documents the required header. Expense JSON stays unchanged.
- Application compares the expected version before validation and rate resolution. Persistence retains the matched original rowversion for EF's guarded UPDATE of actual changes; GET supplies the current ETag after a successful PUT. After validation and normalization, equal Expense records (including conversion snapshots) return 200 with updated=false/reason=unchanged without calling persistence update. No-op requests do not guarantee detecting changes after the initial version check and do not advance rowversion.
- DELETE checks ID and expected rowversion atomically. Zero affected rows returns 412, including an absent resource; initial PUT/GET absence returns 404. Unrelated conflicts retain 409.
- Individual GET or POST supplies an ETag; list clients retrieve the individual resource first. This intentionally breaks unconditional expense PUT/DELETE clients.
- Verification: ExpensePreconditionTests covers rejected headers before I/O; ExpenseApiTests covers real SQL rowversions, stale clients, intervening writes/deletes, unchanged requests without writes, competing requests, preserved snapshots and command counts; OpenApiDocumentationTests checks generated header contracts. Final verification results are reported at review, separately from historical Phase 9 counts above.

No authentication, multi-user support, background jobs, migrations or unrelated product features are added. The no-op refinement remains uncommitted pending independent review.

## User-requested architecture revision — 23 November 2026

The user explicitly requested interfaces in the projects that implement them and dependencies from higher layers to lower layers. This replaces the earlier Application-owned infrastructure contracts. The five production projects and four test projects remain.

The original assignment was reread in full. This revision addresses its code structure, readability and maintainability evaluation criterion through the user's selected layer direction; it does not add functional requirements.

- Application references Caching and Persistence. Caching references Integrations and Persistence. Both leaf projects have no project references. Api may reference all four to compose DI.
- Store interfaces, records and failures belong to Persistence; the provider interface and rate table belong to Integrations; the resolver interface and result belong to Caching. Application keeps its workflows and application-only inputs.
- Caching maps the provider rate table to the durable snapshot model without changing rates, publication/retrieval times or next-update hints. This keeps Persistence and Integrations independent.
- Existing HTTP behavior, monetary rules, SQL schema/migrations, cache deadlines, cancellation and concurrency behavior remain the acceptance baseline. Existing tests keep explicit, separated Arrange / Act / Assert sections and controlled external dependencies.

Verification gate: inspect references for cycles and upward dependencies; build with warnings as errors; run the unit, host and real SQL Server suites; check formatting and coverage. Record actual results separately from the historical phase counts above.
