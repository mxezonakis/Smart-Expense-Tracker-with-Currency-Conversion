# SmartExpenseTracker - .NET Backend Engineering Instructions

## Purpose and authority

This document defines **C# and ASP.NET Core implementation conventions** for SmartExpenseTracker. It is a companion to the root [AGENTS.md](../AGENTS.md), [implementation plan](../implementation-plan.md), and the assignment specification in `docs/`; it is not a replacement for them.

Read those documents before implementing a phase. Follow the assignment and explicitly approved requirements first. If this document conflicts with a project-level decision, stop and identify the conflict rather than silently changing the design. These instructions describe **how** to implement approved features; they do not authorize implementing additional features.

Use only SmartExpenseTracker project identifiers. Do not copy unrelated source, configuration, comments, credentials, secrets, code history, or branding into this repository.

## 1. Approved solution boundaries

Use exactly the agreed five production projects and four test projects:

```text
src/
  SmartExpenseTracker.Api/
  SmartExpenseTracker.Application/
  SmartExpenseTracker.Persistence/
  SmartExpenseTracker.Integrations/
  SmartExpenseTracker.Caching/
tests/
  SmartExpenseTracker.Application.UnitTests/
  SmartExpenseTracker.Integrations.UnitTests/
  SmartExpenseTracker.Caching.UnitTests/
  SmartExpenseTracker.Api.IntegrationTests/
```

### Production dependency rules

| Project | Direct project references | Responsibility |
| --- | --- | --- |
| `SmartExpenseTracker.Api` | Application, Persistence, Integrations, Caching | ASP.NET Core host, MVC controllers, HTTP contracts, middleware, ProblemDetails, OpenAPI and DI composition |
| `SmartExpenseTracker.Application` | Caching, Persistence | Application inputs, business workflows, business rules and reporting orchestration |
| `SmartExpenseTracker.Persistence` | None | Store interfaces and records, EF Core, SQL Server, relational queries, migrations and durable stores |
| `SmartExpenseTracker.Integrations` | None | Provider interface and rate table, outbound HTTP adapters, typed clients and response mapping |
| `SmartExpenseTracker.Caching` | Integrations, Persistence | Resolver interface and result, memory cache, rate freshness, refresh coordination and fallback policy |

- References flow from higher layers to lower layers. Each interface belongs to the project containing its production implementation. `Api` may reference every production project to compose DI.
- `Application` calls the interfaces exposed by `Caching` and `Persistence`; `Caching` calls those exposed by `Integrations` and `Persistence`. Neither leaf project references a higher layer or the other leaf. Do not introduce circular references.
- Keep ASP.NET Core hosting in `Api`, database operations in `Persistence` and provider transport in `Integrations`. Application services use the lower layers' contracts without performing SQL or HTTP operations themselves.
- Keep the five production projects. Interfaces stay beside their implementations; no interface-only or shared-model assembly is needed for the current design.
- Do not add a generic repository, MediatR, an event bus, a new infrastructure project or a separate HTTP client project without an explicit requirement and approval.
- Organize folders by cohesive responsibility, not by creating one folder or interface for every class automatically.

## 2. C# language and formatting conventions

- Target **.NET 10**; enable nullable reference types and implicit usings.
- Use **block-scoped namespaces** and **Allman-style braces** (opening braces on new lines) consistently for handwritten C# source and tests.
- Prefer **explicit constructors** with injected dependencies assigned to `private readonly` fields. Do not mix primary and explicit constructors across the solution merely for brevity.
- Use `PascalCase` for types, methods and public members; `camelCase` for local variables and parameters; `_camelCase` for private fields; `I` prefixes for interfaces.
- Prefer `var` when the assigned type is obvious; use explicit types where they improve understanding.
- Prefer `sealed` for concrete classes not designed for inheritance. Prefer immutable request/response models and immutable data snapshots where appropriate.
- Write small, readable methods with early returns when they simplify branching. Avoid deeply nested conditional flows and unnecessary partial classes.
- Use `partial` only for a genuinely large cohesive type that benefits from grouping operations; do not use it to conceal unrelated responsibilities.
- Follow `.editorconfig` and run `dotnet format` verification. Do not suppress warnings or disable analyzers solely to make checks pass.
- Document non-obvious business decisions, concurrency assumptions or protocol contracts. Avoid comments that merely restate the code.

Illustrative formatting only (not a required business interface):

```csharp
using SmartExpenseTracker.Persistence.Expenses;

namespace SmartExpenseTracker.Application.Expenses
{
    public sealed class ExpenseService : IExpenseService
    {
        private readonly IExpenseStore _expenseStore;

        public ExpenseService(IExpenseStore expenseStore)
        {
            _expenseStore = expenseStore;
        }
    }
}
```

If `.editorconfig` already prescribes incompatible namespace or constructor conventions, update it deliberately and consistently; do not leave the repository with conflicting formatting instructions.

## 3. Controllers and HTTP contracts

- Use **ASP.NET Core MVC controllers** with `[ApiController]` and attribute routing for business endpoints. Do not replace the agreed controller architecture with minimal-API business routes.
- Keep controllers thin: accept/bind inputs, delegate to application services, map outputs and return HTTP responses. Do not query `DbContext` or call external providers directly from controllers.
- Keep publicly exposed request/response DTOs in `SmartExpenseTracker.Api` (e.g., `Contracts/Requests` and `Contracts/Responses`), separate from persistence entities and provider-specific DTOs.
- Use typed actions (`ActionResult<T>` where appropriate), predictable response shapes and correct HTTP status codes.
- Use `201 Created` for successful resource creation when the contract calls for it; use `404` for missing resources, `400` for invalid input, `409` for conflicts and a documented `503` for unavailable required rates. Apply these consistently rather than hard-coding error payloads throughout controllers.
- Centralize expected failures and unexpected exceptions as safe RFC 7807-style `ProblemDetails` responses. Do not disclose exception internals, connection strings or upstream secrets.
- Keep JSON serialization and OpenAPI behavior aligned with actual API contracts. Document decimal representation and date formats when chosen by the functional contract.
- Do not make the API call its own endpoints over HTTP to execute internal workflows.

## 4. Application workflows and service interfaces

- Define focused interfaces at genuine dependency boundaries and keep each in its implementation's project. Application services consume lower-layer contracts through constructor injection.
- Application services coordinate business operations and validation. Place deterministic business rules in focused models/helpers where they are reusable and testable.
- Keep provider-specific DTOs, HTTP statuses and EF Core-specific types out of Application contracts.
- Use explicit, predictable mapping methods or mapping extension classes when the mapping is nontrivial; avoid bringing in an object-mapping framework merely for simple DTO mappings.
- Preserve the approved behavior for conversion snapshots, monetary edits versus metadata-only edits, category constraints, and reporting. The implementation plan owns the detailed contracts.
- Use business-oriented names (`CreateExpenseAsync`, `GetExpensesAsync`) rather than vague methods such as `Process` or `Handle` without context.
- Do not introduce an interface for every pure helper or implementation class by default.

## 5. Async programming and cancellation

- Use `async`/`await` for asynchronous I/O. Return `Task` or `Task<T>`; do not use `async void` outside framework event handlers.
- Accept `CancellationToken cancellationToken` as the last parameter of async service/client/persistence methods and forward it to supported .NET/EF Core/HTTP operations.
- Do not block on tasks with `.Result`, `.Wait()` or sync-over-async patterns.
- Use `Task.WhenAll` only for independent work; **never** issue concurrent operations against a single EF Core `DbContext`.
- Do not use `Task.Run` to wrap ordinary database or HTTP I/O.
- Distinguish caller cancellation from provider timeouts and failures. Do not turn a cancelled request into a successful response or incorrectly treat it as provider downtime.
- For shared cache refreshes, a caller should be able to cancel its own wait without cancelling an in-flight refresh still needed by other callers. Bound the shared operation independently.

## 6. Dependency injection and configuration

- Use **constructor injection**. Avoid service-location patterns, mutable static dependencies and creating service providers inside normal registrations.
- Keep cohesive registration extension methods in the owning projects, e.g., `AddApplication`, `AddPersistence`, `AddIntegrations`, `AddCaching`. The `Api` host calls them and acts as the composition root.
- Use strongly typed options (`IOptions<T>` / related patterns as appropriate) with startup validation of required values, URI schemes, positive timeouts and acceptable cache durations.
- Use `appsettings.json` for non-sensitive defaults and `appsettings.Development.json` for safe development overrides. Keep API keys, passwords and connection secrets in environment variables or .NET User Secrets; never commit them.
- Do not introduce additional reusable configuration/logging/Swagger class-library projects solely to share a few extension methods.
- Register service lifetimes deliberately: EF Core contexts are scoped; stateless HTTP adapters follow the typed-client registration; shared cache coordinators must have a safe lifetime and thread-safe internals.

## 7. Outbound HTTP integrations

- Put external exchange-rate provider communication in `SmartExpenseTracker.Integrations`, **not** in controllers, Application services or Caching.
- Register the provider adapter using `IHttpClientFactory` / a **typed HttpClient** and provider-specific validated options.
- Keep `IExchangeRateProvider` and its `ExchangeRateTable` contract in Integrations. Use provider-specific transport request/response models internally and map into that contract. Validate required fields, rate direction, finite/positive rates where applicable, and publication metadata.
- Forward cancellation and handle bounded timeouts. Distinguish invalid upstream payloads, unsupported currencies, HTTP failure statuses, timeouts and client cancellation.
- Dispose owned request/response messages and streams appropriately. Let `IHttpClientFactory` manage HTTP-handler lifetimes; do not create a new long-lived-handler stack per operation.
- Retry only when explicitly justified and safe. Do not automatically retry non-idempotent writes or produce uncontrolled retry storms.
- Keep the provider replaceable without changing Application workflows or public API contracts.
- Never use live provider calls in automated unit or API integration tests; substitute a controlled `HttpMessageHandler` or the Integrations-owned `IExchangeRateProvider` interface.

## 8. Persistence and database design

- Put `DbContext`, EF Core configurations, SQL Server registration, migrations and database-backed implementations exclusively in `SmartExpenseTracker.Persistence`.
- Keep `ICategoryStore`, `IExpenseStore`, `IReportStore` and `IRateSnapshotStore` in Persistence with their contract records and failure types. Keep EF row types internal to Persistence.
- Prefer individual `IEntityTypeConfiguration<T>` classes for nontrivial mappings. Explicitly configure money precision, required columns, foreign keys, deletion behavior, unique constraints and relevant indexes.
- Use a relational model consistent with the approved contracts: expense date handling, original amount/currency, applied exchange rate, converted base amount and quote metadata. Do not invent additional expense fields or reporting semantics.
- Keep migrations in Persistence, and run them with an appropriate startup project. Do not rely on `EnsureCreated` as a replacement for migration verification.
- Push filtering, stable sorting and aggregations down to SQL when possible. Use projections for read endpoints rather than loading unnecessary graphs.
- Use `AsNoTracking()` for read-only entity queries when beneficial. Avoid accidental N+1 access patterns; inspect generated SQL and execution plans for important query paths.
- Preserve database integrity using unique constraints, foreign keys and explicit transactions for truly multi-step atomic writes. Obtain fresh exchange rates **before** starting a database transaction.
- A `DbContext` is not thread-safe. Do not share it across concurrent tasks or long-lived singleton services. Use a suitable fresh scope for background/shared refresh persistence operations.
- Do not introduce a generic repository abstraction or mock the relational provider to claim database correctness.

## 9. Exchange-rate caching and thread safety

- `SmartExpenseTracker.Caching` owns `IRateResolver`, `ResolvedRates`, `RateUnavailableException`, `IMemoryCache`, freshness evaluation, per-key refresh coordination, bounded stale fallback and rate-selection policy.
- Keep transport in Integrations and durable snapshot storage in Persistence. Caching references both projects and consumes their interfaces. Map the provider's `ExchangeRateTable` to Persistence's `StoredExchangeRateTable` when creating a durable `RateSnapshot`, preserving all values and timestamps. The two leaf projects remain independent.
- Use **separate fresh TTL and maximum stale age** as specified in the implementation plan. Expired-freshness data may still be eligible for fallback; simply evicting at the fresh TTL is insufficient.
- Coalesce concurrent refresh attempts per stable provider/base key. Recheck the cache once refresh coordination is acquired; avoid leaked locks, unlimited per-key state and races around completion.
- Never replace a valid last-known snapshot with a malformed or unsuccessful refresh. Do not reset the original quote age when using a fallback.
- If no acceptable rate exists, return a documented failure rather than silently using an invalid or excessively old rate.
- Keep the cache's multi-instance limitations explicit. Do not add Redis or other distributed dependencies unless requirements change.
- Use a thread-safe design for request statistics, with a consistent snapshot of counters and durations; keep middleware and endpoint behavior in Api.

## 10. Unit and integration testing

Use the approved four test projects; do not create separate tests for every C# namespace or class library:

| Project | Focus |
| --- | --- |
| `SmartExpenseTracker.Application.UnitTests` | Business rules, validation, conversion, rounding and snapshot update rules |
| `SmartExpenseTracker.Integrations.UnitTests` | HTTP mapping, malformed replies, failures, timeouts and cancellation |
| `SmartExpenseTracker.Caching.UnitTests` | Snapshot validation, provider-to-snapshot mapping, fresh/stale boundaries, fallback, deterministic concurrent refresh and cancellation |
| `SmartExpenseTracker.Api.IntegrationTests` | Real HTTP requests, SQL Server persistence, constraints, filtering, reporting, errors and statistics |

- Use NUnit with FakeItEasy and a **license-approved, pinned** FluentAssertions version; verify license suitability before adding or changing the package.
- Prefer descriptive test names and a clear **Arrange / Act / Assert** flow. Use `[SetUp]` only for common deterministic setup that genuinely improves clarity.
- Test observable behavior and meaningful edge cases, not framework implementation details.
- Control clocks and HTTP responses. Prefer barriers or coordinated tasks over arbitrary `Task.Delay` sleeps in concurrency tests.
- For relational behavior, use an isolated **real SQL Server** instance, preferably via Testcontainers, rather than the EF Core InMemory provider. Keep container-required tests separate from the host-only smoke test.
- Verify status codes, serialization, request binding, `ProblemDetails`, database constraints and cancellation via the composed API host.
- Do not claim integration tests passed when Docker or another prerequisite was unavailable. Do not depend on a live currency service in CI.

## 11. Development workflow and verification

- Implement only the current approved phase; do not proceed to the next phase automatically.
- Before changes: inspect `AGENTS.md`, the implementation plan and relevant code; explain substantial architecture or dependency changes.
- After changes: summarize affected files, design decisions, risks, and the **actual** verification performed.
- Run the following commands from the repository root when their prerequisites are available:

```powershell
dotnet restore SmartExpenseTracker.slnx
dotnet build SmartExpenseTracker.slnx --no-restore -warnaserror
dotnet test SmartExpenseTracker.slnx --no-restore -warnaserror
dotnet format whitespace SmartExpenseTracker.slnx --no-restore --verify-no-changes
dotnet format style SmartExpenseTracker.slnx --no-restore --verify-no-changes
```

- Use `--locked-mode` for restore only when the repository intentionally maintains valid NuGet lock files.
- Test command behavior and availability depend on the installed .NET SDK and environment. Report failures or skipped checks honestly.
- When Docker is unavailable, run the remaining build/unit/host tests and explicitly report that SQL Server integration verification was not performed.
- Keep the README, OpenAPI examples, migrations instructions and phase verification results consistent with implemented behavior.
- Ensure a clean checkout can build and run with documented prerequisites; do not publish environment-specific secrets or private configuration.

## 12. Scope discipline and review checklist

Before completing a phase, check:

1. Does the code respect all five production and four test project boundaries?
2. Do references follow the higher-to-lower direction, with interfaces in their implementations' projects and no circular references?
3. Are controllers thin, service contracts focused, and models kept at their proper boundaries?
4. Are C# namespaces, brace placement, constructors and field naming consistent with these instructions?
5. Are async calls cancellable, with no concurrent access to the same `DbContext`?
6. Are HTTP integration, cache policy and SQL persistence independently testable?
7. Are money, date, conversion-snapshot and reporting rules consistent with the approved plan?
8. Are error responses, logs and configuration safe and documented?
9. Were relevant checks actually run, and are any skipped checks explicitly identified?
10. Did the changes avoid unrelated features, extra projects, credentials and external code?

**Do not implement optional authentication, multiple users, or background refresh without explicit approval.**
