# Stream A plan: core and correctness

Scope: `src/`, `tests/`, `scenarios/`, register rows R2-R8, the PostgreSQL service and the
scenario runner in `ci.yml`, and new ADRs for the decisions taken here. Deployment, identity,
the docs site and the PDF belong to streams B and C and are out of scope.

Read against `00-REFERENCE-ARCHITECTURE.md` (P1-P15 and the compliance checklist),
`SERVICE-API-PATTERNS.md`, `TESTING-STRATEGY.md`, `TICKET-ANALYSIS.md` and `PR-REVIEW.md` from
[architecture-standards](https://github.com/konradcinkusz/architecture-standards). Baseline on
`main` at the time of writing: `dotnet build -warnaserror` clean, 37 tests green.

## Decisions taken for the whole stream

1. **One slice, one branch, one PR.** Branches are `claude/stream-a/<slice>`. Every PR runs
   `dotnet build -warnaserror` and `dotnet test` before push, describes the decision and the
   principle it serves, and is reviewed against its acceptance criteria below before merge.
1. **PostgreSQL in tests is resolved once, by a fixture.** If `PAYMENTSBENCH_TEST_POSTGRES` holds
   a connection string, the fixture creates a fresh database per test class on that server;
   otherwise it starts a Testcontainers PostgreSQL. CI provides a PostgreSQL service container
   with `POSTGRES_HOST_AUTH_METHOD: trust`, so no credential is written into the tree. A missing
   database fails the test loudly; nothing is skipped (TESTING-STRATEGY.md §6).
1. **The schema is migrated, never ensured** (P4), and every other hosted service waits on a
   migration gate (SERVICE-API-PATTERNS.md §7).
1. **Metric names follow stream B's dashboard**: `payments.submitted.oldest_age_seconds`
   (observable gauge, seconds) and `payments.sweep.resolved` (counter, tag `outcome`), on the
   `PaymentsBench` meter.
1. **`tests/PaymentsBench.Api.Tests` is created by slice A2.** Stream B's identity tests are
   added to the same project when that slice lands; whichever PR lands second rebases.
1. **API tests and the scenario runner run the API in the `Development` environment** with no
   `Auth:Authority`, which stream B's plan defines as the anonymous development policy. If
   identity lands first, the runner keeps working without a token by that rule.
1. **Shared files are edited minimally and every slice is brought up to date with `main` before
   merge**: `Program.cs`, `ci.yml`, `PaymentsBench.sln`, `Directory.Packages.props`,
   `docs/REGISTER.md`, `README.md` (Run section only). ADRs take the next free number on `main`
   at merge time.
1. **Nothing here talks to a real bank.** Every partner in tests is the simulator, and the text
   says "simulated" wherever a reader could assume otherwise.

## Order and waves

| Wave | Slice | Register | Depends on | Starts when |
|---|---|---|---|---|
| 1 | A1 persistence | R4 | - | plan merged |
| 1 | A2 api-surface | - | - | plan merged |
| 1 | A3 calendars | R7 | - | plan merged |
| 2 | A6 scenario-runner | R3 | A1, A2 | A1 and A2 merged |
| 2 | A4 concurrency | R6 | A1, A2 | A1 and A2 merged |
| 2 | A5 sweeper | R8 | A1 | A1 merged |
| 2 | A7 outbox-contract | R5 (narrowed) | A1 | A1 merged |
| 3 | A8 iso20022 | R2 | A2 | A2 merged and a slot is free |

At most four sub-sessions run at once. When slots are short, the register's priority order
decides: R3, R6, R8, R5, R2.

## Slices

### A1 - persistence: migrations and PostgreSQL integration tests (R4) - PR: [#13](https://github.com/konradcinkusz/payments-bench/pull/13)

Principle: P4 (migrated, never ensured; migrations in a hosted service after Kestrel starts),
P13 (integration tier against a real container), SERVICE-API-PATTERNS.md §7 (migration gate).

| Acceptance criterion | Owning context and layer | Governed by | Guide to load | Files | Blocking question |
|---|---|---|---|---|---|
| An initial EF Core migration replaces `EnsureCreated`; `MigrationHostedService` calls `MigrateAsync` and runs after the server starts, so `/alive` answers while it works | payments, persistence | P4 | reference architecture P4 | `Persistence/Migrations/*`, `Persistence/MigrationHostedService.cs`, `Persistence/PaymentsDbContextFactory.cs` (design time, no credentials), `.config/dotnet-tools.json` (pinned `dotnet-ef`) | none |
| A `MigrationGate` singleton completes when migration finishes; `OutboxDispatcher` awaits it; `/health` reports unhealthy until it completes | payments, persistence + hosting | P4, P8 | SERVICE-API-PATTERNS.md §7 | `Persistence/MigrationGate.cs`, `Outbox/OutboxDispatcher.cs`, `InfrastructureRegistration.cs` | none |
| Integration tests against real PostgreSQL for `EfPaymentRepository` (round-trip of every status), `EfLedger` (entries and derived balances), `EfOutbox`, `EfInbox` (second record of an id is refused) | payments, persistence | P13 | TESTING-STRATEGY.md §4, §6 | `tests/PaymentsBench.Integration.Tests/*` | none |
| `EfUnitOfWork` is atomic: payment, ledger and outbox rows commit together, and a failing commit leaves none of them | payments, persistence | P13 | TESTING-STRATEGY.md §6 | `tests/PaymentsBench.Integration.Tests/UnitOfWorkTests.cs` | none |
| The migration applies to an empty database, leaves no pending migrations, and the model has no changes the migration does not carry (`HasPendingModelChanges`) | payments, persistence | P4 | reference architecture P4 | `tests/PaymentsBench.Integration.Tests/MigrationTests.cs` | none |
| CI runs the integration tests against a PostgreSQL service container | CI | P12, P13 | TESTING-STRATEGY.md §3 | `.github/workflows/ci.yml` | none |

Tests: the integration project above; existing 37 stay green. Register: R4 moves to _Closed_
citing the test classes and the CI job. ADR: schema migrated by a hosted service behind a gate.
README Run section: how `dotnet test` finds a database.

### A2 - api-surface: manifest, validation, problem details, pagination - PR: [#10](https://github.com/konradcinkusz/payments-bench/pull/10)

Principle: P9 (Program.cs is a manifest), SERVICE-API-PATTERNS.md §3 (validation) and §4
(pagination and list queries), P11 (no persistence or framework type in a response).

| Acceptance criterion | Owning context and layer | Governed by | Guide to load | Files | Blocking question |
|---|---|---|---|---|---|
| `Program.cs` reads as a list of capabilities; endpoints live in `Endpoints/*Endpoints.cs` extension methods and API wiring in one `AddPaymentsApi` extension | payments, HTTP surface | P9 | reference architecture P9 | `Program.cs`, `Endpoints/*.cs`, `ApiServiceCollectionExtensions.cs` | none |
| Every error is `application/problem+json`: an exception handler maps idempotency conflict and invalid transition to 409, not found to 404, other domain errors to 422 | payments, HTTP surface | SERVICE-API-PATTERNS.md (uniform error body) | SERVICE-API-PATTERNS.md §3 | `Errors/ProblemDetailsExceptionHandler.cs` | none |
| Request bodies are validated once, generically, and invalid input returns `ValidationProblem` grouped by member (end-to-end id max 35, amount > 0 with at most 2 decimals, ISO currency code, accounts max 34, scheme required, Idempotency-Key required and max 255) | payments, HTTP surface | SERVICE-API-PATTERNS.md §3 | SERVICE-API-PATTERNS.md §3 | request records, validation registration | none |
| `GET /payments` lists with filters (`status`, `scheme`, `endToEndId`, `createdFrom`, `createdTo`) and clamped pagination (`page = max(1, page)`, `limit = clamp(limit, 1, 100)`, default 20) and returns `items`, `page`, `limit`, `total` and per-status counts from one grouped query | payments, HTTP surface + persistence | SERVICE-API-PATTERNS.md §4 | SERVICE-API-PATTERNS.md §4 | `IPaymentRepository.ListAsync(PaymentQuery)`, in-memory and EF implementations | none |
| WebApplicationFactory tests prove each of the above | payments, tests | P13 | TESTING-STRATEGY.md §6 | `tests/PaymentsBench.Api.Tests/*` | none |

Out of scope: authentication (stream B). The EF `ListAsync` gets its integration test in A4 if
A1 has not landed when A2 merges.

### A3 - calendars: business calendars as data (R7) - PR: [#11](https://github.com/konradcinkusz/payments-bench/pull/11)

Principle: architecture rule 7 (scheme rules are data), P10 (a calendar is an interface plus
registration), P8 (scheme data is required; malformed data stops start-up with the file named).

| Acceptance criterion | Owning context and layer | Governed by | Guide to load | Files | Blocking question |
|---|---|---|---|---|---|
| Each scheme in `schemes.yaml` names a calendar; calendars live in `scenarios/calendars/*.yaml` with weekend rule, closing days and an explicit covered date range, each citing its public source | payments, data | architecture rule 7 | none - domain data | `scenarios/schemes.yaml`, `scenarios/calendars/*.yaml` | none |
| `HolidayCalendar : IBusinessCalendar` honours closing days and refuses a date outside its coverage instead of guessing | payments, domain | none - domain logic | none | `Domain/Schemes/*.cs` | none |
| Schemes and calendars are loaded at start-up into an `ISchemeCatalog`; the path comes from configuration; unknown scheme or a currency the scheme does not carry is refused at initiation with 422 | payments, infrastructure + application | P5, P8 | reference architecture P5, P8 | `Infrastructure/Schemes/*.cs`, `InitiatePayment.cs`, API project file (data copied to output) | none |
| Tests on known closing days: TARGET2 Christmas 2026 and Good Friday/Easter Monday 2026 move the SEPA value date; the real data files load and every scheme resolves its calendar | payments, tests | P13 | TESTING-STRATEGY.md §6 | `tests/PaymentsBench.Domain.Tests/*`, `tests/PaymentsBench.Application.Tests/*` | none |

Register: R7 moves to _Closed_. ADR: calendars as dated, covered data. A new row is opened if the
computed value date is still not stamped on the payment.

### A4 - concurrency: one of two writers loses (R6) - PR: [#22](https://github.com/konradcinkusz/payments-bench/pull/22)

Principle: P13; architecture "Persistence" (`Version` is a concurrency token); P11 (no EF type
crosses into Application or Api).

| Acceptance criterion | Owning context and layer | Governed by | Guide to load | Files | Blocking question |
|---|---|---|---|---|---|
| The version the aggregate was read at is the version the update checks (today `UpdateAsync` re-reads the row, so a stale aggregate is not detected) | payments, persistence | P4 | none | `Persistence/EfCoreStores.cs`, `InMemory/InMemoryStores.cs` | none |
| `DbUpdateConcurrencyException` (and a unique violation on the idempotency key) is translated to an Application `ConcurrencyConflictException`; the API maps it to 409 problem details | payments, persistence + HTTP surface | P11 | SERVICE-API-PATTERNS.md (uniform error body) | `Application/Ports/ConcurrencyConflictException.cs`, `EfUnitOfWork`, the API exception handler | none |
| An integration test races two writers on one payment: exactly one commits, the other gets the conflict, and the loser's ledger and outbox rows are absent | payments, tests | P13 | TESTING-STRATEGY.md §6 | `tests/PaymentsBench.Integration.Tests/ConcurrencyTests.cs` | none |
| An API test proves the 409 mapping | payments, tests | P13 | TESTING-STRATEGY.md §6 | `tests/PaymentsBench.Api.Tests/*` | none |

Register: R6 moves to _Closed_.

### A5 - sweeper: Submitted payments resolve themselves (R8) - PR: [#21](https://github.com/konradcinkusz/payments-bench/pull/21)

Principle: ADR 0001 consequence (a payment can sit at Submitted until something resolves it),
P5 (configuration through the environment), P15 (the new path has metrics, traces and logs),
SERVICE-API-PATTERNS.md §6-§7.

| Acceptance criterion | Owning context and layer | Governed by | Guide to load | Files | Blocking question |
|---|---|---|---|---|---|
| A sweep resolves payments at Submitted whose last update is older than a configurable age, oldest first, in batches, each through `ResolveSubmittedPaymentHandler` in its own scope; one failure does not stop the batch | payments, application | ADR 0001 | SERVICE-API-PATTERNS.md §6 | `Application/Payments/SweepSubmittedPayments.cs`, `IPaymentRepository` query | none |
| `SubmittedPaymentSweeper` is a background service that waits on the migration gate; `Sweeper__Enabled`, `Sweeper__Interval`, `Sweeper__MinimumAge`, `Sweeper__BatchSize` are bound and validated on start | payments, infrastructure | P5, P8 | SERVICE-API-PATTERNS.md §7 | `Infrastructure/Sweeping/*.cs`, `InfrastructureRegistration.cs` | none |
| Metrics `payments.submitted.oldest_age_seconds` and `payments.sweep.resolved{outcome}`; the alert threshold is documented in the ADR | payments, observability | P15 | reference architecture P15 | sweeper, ADR | none |
| Tests with a fixed clock: an old Submitted payment is resolved, a young one is not, NotFound resubmits once, a throwing resolve does not stop the batch, the metrics carry the expected values | payments, tests | P13 | TESTING-STRATEGY.md §6 | `tests/PaymentsBench.Application.Tests/*`, infrastructure tests | none |

Register: R8 moves to _Closed_.

### A6 - scenario-runner: every scenario file executes (R3) - PR: [#23](https://github.com/konradcinkusz/payments-bench/pull/23)

Principle: P13 (a suite is evidence only if it is wired to CI), architecture rule 7 (scenarios
are data; a new file is a new case with no code change).

| Acceptance criterion | Owning context and layer | Governed by | Guide to load | Files | Blocking question |
|---|---|---|---|---|---|
| `tests/PaymentsBench.Scenarios` discovers every `scenarios/*.yaml` (not `schemes.yaml`, not `calendars/`) as one test case and runs it end to end against the API and the simulator in process, a fresh pair per scenario | payments, tests | P13 | TESTING-STRATEGY.md §6, E2E-ACCEPTANCE-TESTING.md | `tests/PaymentsBench.Scenarios/*` | none |
| The partner fault (`partner.fault` and `partnerFault` steps) is applied on the simulator; partner timeouts are configuration (`Partner__*`), so a timeout scenario runs in seconds | payments, tests + infrastructure | P5 | reference architecture P5 | `InfrastructureRegistration.cs`, simulator | none |
| Every `expect` key is asserted: status, payment count, ledger entry count, ledger balances, partner submit and status-query counts (counted by the simulator), reconciliation breaks and kinds; an unknown step or expect key fails the case | payments, tests | P13 | TESTING-STRATEGY.md §6 | runner, simulator call counter | none |
| Runs on in-memory storage always and on PostgreSQL (fresh database per scenario) when `PAYMENTSBENCH_TEST_POSTGRES` is set; CI runs both and gates on them | payments, tests + CI | P13 | TESTING-STRATEGY.md §3 | runner, `ci.yml` | none |
| New scenario files for paths not yet specified: partner rejects, and a reconciliation break of a kind not yet covered | payments, data | architecture rule 7 | none | `scenarios/*.yaml`, `scenarios/README.md` | none |

Register: R3 moves to _Closed_. ADR: the scenario runner runs in process.

### A7 - outbox-contract: at-least-once, deduped by the consumer (R5, narrowed) - PR: [#20](https://github.com/konradcinkusz/payments-bench/pull/20)

Principle: architecture rule 5, P10 (a publisher is an interface plus one registration),
reference architecture §4 (no event bus until a use case needs one).

| Acceptance criterion | Owning context and layer | Governed by | Guide to load | Files | Blocking question |
|---|---|---|---|---|---|
| An in-process `ChannelEventPublisher` behind `IEventPublisher`, selected by `Outbox__Publisher` (`log` default, `channel`) | payments, infrastructure | P10, P5 | reference architecture P10 | `Outbox/*.cs` | none |
| A consumer that dedupes on the message id | payments, infrastructure | architecture rule 5 | none | `Outbox/DedupingConsumer.cs` | none |
| Integration tests: an event written with its state change is delivered; a publish whose dispatched mark is lost is delivered again with the same id and the consumer applies it once | payments, tests | P13 | TESTING-STRATEGY.md §6 | `tests/PaymentsBench.Integration.Tests/OutboxContractTests.cs` | none |

Register: R5 is narrowed, not closed: no broker has carried an event. ADR: a broker is a
one-class swap, and what that class must honour.

### A8 - iso20022: anti-corruption edge (R2) - PR: [#24](https://github.com/konradcinkusz/payments-bench/pull/24)

Principle: P11 (external dialects are normalized at the boundary, once).

| Acceptance criterion | Owning context and layer | Governed by | Guide to load | Files | Blocking question |
|---|---|---|---|---|---|
| A `pain.001`-shaped customer credit transfer initiation is read at the edge into `InitiatePayment` commands (one per transaction) through `POST /payments/pain001`; the internal model is unchanged | payments, anti-corruption edge | P11 | reference architecture P11 | `src/PaymentsBench.Iso20022/*`, `Endpoints/*` | none |
| A `camt.053`-shaped statement is read into `Statement`; `HttpStatementSource` accepts it when the simulated partner serves XML | payments, anti-corruption edge | P11 | reference architecture P11 | `src/PaymentsBench.Iso20022/*`, `Partner/HttpStatementSource.cs`, simulator | none |
| Writers for both shapes and round-trip tests on sample documents committed under `tests/PaymentsBench.Iso20022.Tests/samples/` | payments, tests | P13 | TESTING-STRATEGY.md §6 | test project and samples | none |

Register: R2 moves to _Closed_ if the closing condition is met as written; a new row records
that the samples are hand-written in the published shape and are not validated against the
official schemas.

## Out of scope

A real broker (R5 stays narrowed), a real bank or scheme API (R1), load and chaos (R9, stream
B), sanctions and limits (R10), authentication, Dockerfiles, deployment and the docs site.

## Progress

- [x] A1 persistence (R4) - #13: R4 closed; R12 opened (Testcontainers path not run, concurrent migration untested); ADR 0008
- [x] A2 api-surface - #10: manifest, problem details, validation, paged list; ADR 0009; site generator sums every `tests.*` key
- [x] A3 calendars (R7) - #11: R7 closed; R11 opened (value date not stamped on the payment); ADR 0007
- [x] A4 concurrency (R6) - #22: R6 closed; R19 opened (concurrent duplicate initiate returns 409); ADR 0013
- [x] A5 sweeper (R8) - #21: R8 closed, stream B's R16 (resolve 500 on open circuit) closed, fixes F-3; R17 opened (sweep proved in process only); ADR 0011
- [x] A6 scenario-runner (R3) - #23: R3 closed, fixes F-4 and F-5; R18 opened (a return has no statement line of its own); ADR 0012
- [x] A7 outbox-contract (R5 narrowed) - #20: R5 narrowed, not closed (no broker); ADR 0010
- [x] A8 iso20022 (R2) - #24: R2 closed; R20 opened (samples hand-written, not XSD-validated, gateway still JSON); ADR 0014

## Findings

What the slices caught on the way, for `docs/FINDINGS.md`. Each one is fixed unless it says otherwise.

| Finding | Caught by | Outcome |
|---|---|---|
| `EfPaymentRepository.UpdateAsync` re-read the row and used that version as the original value, so a writer that lost a race overwrote the winner instead of failing | A4, reading the code behind R6 | Fixed in #22: the version read is the version checked; `ConcurrencyTests` races two writers |
| The resolver answered `SafeToResubmit` after a resubmit whose own outcome was unknown (F-3) | Stream C, `OVERVIEW.md` | Fixed in #21: `StillUnknown` |
| `resolve` answered 500 while the partner circuit was open, the recovery path failing exactly when needed (stream B's R16) | Stream B, k6 run | Fixed in #21: `PartnerStatus.Unavailable`, never treated as not-found |
| A settled-then-returned payment showed as an `OnStatementOnly` break on its settlement day (F-4) | Stream C, `OVERVIEW.md` | Fixed in #23; the return day itself is R18 |
| The GET-only retry predicate never matched an exception, so a status lookup that threw was not retried (F-5) | Stream C, `OVERVIEW.md` | Fixed in #23: the method is read from the request |
| Container images failed to publish once scheme data became required: the Dockerfiles copied `src/` only | A3, Containers workflow | Fixed in #11: the Dockerfiles copy `scenarios/` |
| `WebApplicationFactory.ConfigureAppConfiguration` applies too late for settings read while services are built: the runner's partner timeouts silently stayed at their defaults and a timeout scenario passed for the wrong reason | A6, a scenario that should have failed | Fixed in #23: `UseSetting`; recorded in ADR 0012 |
| New register rows appended below the prose that follows the open table rendered outside it, twice | Stream lead review of #23 and #24 | Fixed in both; stream C added a site-build check (#27) |
| CodeQL test-hygiene notes on the integration tests (an undisposed `CancellationTokenSource`, an unused assignment) | CodeQL on #13 | Open; not blocking, left for a hygiene pass |

