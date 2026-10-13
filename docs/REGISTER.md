# Register

What this repository has not proved, with the date the row was written and what would close it.
Rows are deleted only when the closing condition is met; a closed row moves to the bottom.

| # | Since | Not proved | Closes when |
|---|---|---|---|
| R1 | 2026-10-13 | **Nothing here has ever talked to a real bank.** The partner is `PaymentsBench.PartnerSimulator`; its API shape is invented, not SEPA, Faster Payments or SWIFT. | A gateway implementation against a real or sandbox scheme API exists and one scenario runs against it. |
| R2 | 2026-10-13 | **No ISO 20022 message is parsed or produced.** The statement is a JSON reduction of what camt.053 carries; initiation is a JSON body, not pain.001. | `pain.001` out and `camt.053` in, with a round-trip test on real sample files. |
| R3 | 2026-10-13 | **The scenario files are a specification, not an executed suite.** `ScenarioFilesTests` checks they parse; no runner drives them against the API and the simulator. | `tests/PaymentsBench.Scenarios` executes every file end to end and CI gates on it. |
| R5 | 2026-10-13 | **The outbox dispatcher publishes to a log line.** No broker is wired; at-least-once delivery to a consumer is a design claim. | An `IEventPublisher` against a real broker, and a consumer-side dedupe test. |
| R6 | 2026-10-13 | **Concurrency is a column, not a test.** `Version` is a concurrency token; no test races two writers on one payment. | A test that proves one of two concurrent updates fails with a concurrency exception. |
| R7 | 2026-10-13 | **The business calendar is weekends only.** No public holidays, no per-scheme calendar, no TARGET2 closing days. | Holiday calendars as data per scheme, with a test on a known closing day. |
| R8 | 2026-10-13 | **The resolver is called, never scheduled.** `POST /payments/{id}/resolve` exists; nothing sweeps Submitted payments older than N seconds on its own. | A background sweep with a metric on the age of the oldest Submitted payment, and an alert threshold documented. |
| R9 | 2026-10-13 | **No load, no chaos.** Nothing has measured throughput, and the simulator's faults are set by hand, not injected randomly under load. | A k6 or equivalent run with the simulator cycling fault profiles, and numbers in this file. |
| R10 | 2026-10-13 | **Sanctions, AML, limits: absent.** The service moves any amount to any account. | Out of scope for the bench; stays open as a statement of scope. |

## Closed

| # | Closed | Proved by |
|---|---|---|
| R4 | 2026-10-13 | **PostgreSQL persistence runs in CI.** The EF Core stores are exercised against a real PostgreSQL 16 by `tests/PaymentsBench.Integration.Tests`: `PaymentRepositoryTests` (every status round-trips), `LedgerTests`, `OutboxAndInboxTests`, `UnitOfWorkTests` (atomic commit and rollback), `HandlerTests` (initiate and submit on EF stores) and `MigrationTests` (migrations apply, nothing pending, no unmigrated model change). `EnsureCreated` is gone; the schema is the `InitialSchema` migration, applied by `MigrationHostedService` behind `MigrationGate` (`MigrationGateTests`, ADR 0004). CI job `build-and-test` runs them against a `postgres:16-alpine` service container. |
