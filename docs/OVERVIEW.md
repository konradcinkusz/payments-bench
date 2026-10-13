# payments-bench: Comprehensive Project Overview

**An outbound bank-transfer service that shows the mechanisms a payment rail needs to move money
exactly once and to recover when the bank on the other end misbehaves. The bank is simulated; the
mechanisms are real code with tests. The service is the specimen; the evidence about it is the
deliverable.**

- **Status**: the core service, its tests and the partner simulator are on `main`; three streams
  of follow-up work are planned in [`plans/`](plans/) (core and correctness, platform,
  documentation). This document describes what has merged, not what is planned.
- **Companion documents**: [`START-HERE.md`](START-HERE.md) (which document to read) ·
  [`ARCHITECTURE.md`](ARCHITECTURE.md) (the shape and the seven rules) ·
  [`REGISTER.md`](REGISTER.md) (what is not proved) · [`FINDINGS.md`](FINDINGS.md) (what building
  it caught) · [`adr/`](adr/) (decisions) · [`../scenarios/`](../scenarios/) (the fault scenarios as
  data).
- **What this document is for**: every file above answers one question in depth. This one answers
  "what is this, why is it built this way, and does it work" in one sitting, for a reader who will
  not open ten files to find out. It introduces no fact the repository does not already contain;
  where a number appears, the file it was measured in is named next to it.

---

## Contents

1. [Executive summary](#1-executive-summary)
1. [Why a payment rail is hard](#2-why-a-payment-rail-is-hard)
1. [The money path](#3-the-money-path)
1. [Architecture](#4-architecture)
1. [The seven rules](#5-the-seven-rules)
1. [Idempotency](#6-idempotency)
1. [The unknown outcome and the resolver](#7-the-unknown-outcome-and-the-resolver)
1. [Outbox and inbox](#8-outbox-and-inbox)
1. [The ledger](#9-the-ledger)
1. [Reconciliation](#10-reconciliation)
1. [Schemes and calendars](#11-schemes-and-calendars)
1. [The partner simulator and the scenario classes](#12-the-partner-simulator-and-the-scenario-classes)
1. [CI and governance](#13-ci-and-governance)
1. [What the tests and the walk-through caught](#14-what-the-tests-and-the-walk-through-caught)
1. [Production readiness, honestly](#15-production-readiness-honestly)
1. [The register](#16-the-register)
1. [Architecture decision records](#17-architecture-decision-records)
1. [Non-goals](#18-non-goals)
1. [Relationship to the standards](#19-relationship-to-the-standards)
1. [Getting the PDF](#20-getting-the-pdf)
1. [Getting the site](#21-getting-the-site)

---

## 1. Executive summary

**This is not a payments product. It is a bench for the mechanisms one depends on.**

Moving money is easy to get almost right. A transfer request arrives, a row is written, a call goes
to the bank, the bank says yes. Every one of those steps can happen twice, or half-happen, or
happen with nobody hearing the answer, and in each case the naive code either loses track of a
customer's money or sends it twice. This repository builds the smallest service that handles those
cases on purpose and writes down, in a register, which of them it has proved and which it has not.

What is on `main`:

- an HTTP API (`src/PaymentsBench.Api/Program.cs`) to initiate, submit, resolve and inspect an
  outbound transfer, receive partner notifications, run reconciliation and read ledger balances;
- a six-state payment machine whose allowed moves are one table
  (`src/PaymentsBench.Domain/Payments/PaymentTransitions.cs`);
- idempotent initiation: an `Idempotency-Key` plus a SHA-256 hash of the request body, so a replay
  returns the original payment and a reused key with a different body is a `409`;
- a submit that commits `Submitted` before calling the partner, and maps every failure after the
  request may have left the process to an **Unknown** outcome that is never retried blind;
- a resolver that asks the partner by end-to-end id and resubmits only when the partner says it has
  no record;
- a transactional outbox for events and an inbox that deduplicates partner notifications;
- a double-entry, append-only ledger with three accounts, `customer-funds`, `pending-outbound` and
  `nostro` (`src/PaymentsBench.Domain/Ledger/LedgerAccounts.cs`);
- reconciliation of a day's statement against settled payments, returning named breaks as a work
  queue;
- scheme rules (cut-off, currency, settlement days) as data in `scenarios/schemes.yaml`;
- a **simulated** partner bank (`src/PaymentsBench.PartnerSimulator`) with five fault profiles
  switched at runtime;
- a degraded mode: with no database connection string every store is in memory and the log says so.

37 tests pass on `main` (15 in `tests/PaymentsBench.Domain.Tests`, 22 in
`tests/PaymentsBench.Application.Tests`, counted by `dotnet test` on 2026-10-13). They run against
in-memory stores and a scripted partner, which is exactly why the two most serious defects found so
far were found by walking a transfer through the running services over HTTP instead
([`FINDINGS.md`](FINDINGS.md), F-1 and F-2). Ten rows in [`REGISTER.md`](REGISTER.md) say what has
not been proved, the first of which is that nothing here has ever talked to a real bank.

## 2. Why a payment rail is hard

Four properties make outbound payments harder than ordinary request handling. Each one maps to a
mechanism in this repository.

**Requests are repeated.** A mobile client retries on a dropped connection; a queue redelivers; a
user double-clicks. Without a stable notion of "the same request" each repeat is a new transfer.
→ idempotent initiation (§6).

**The partner's answer can be lost after the partner acted.** The bank can receive the transfer,
record it, and fail to deliver the response. From the caller's side that looks identical to a
request that never arrived. A retry is safe in one case and a duplicate transfer in the other, and
the caller cannot tell which. → the Unknown outcome and the resolver (§7).

**Delivery between systems is at least once.** Events this service publishes and notifications the
partner sends can both arrive twice. → outbox and inbox (§8).

**The money must be explainable afterwards.** "Where is this customer's money right now" must have
one answer that can be derived from history, not one that was last written to a column, and what
the bank's statement says must be compared with what the service believes. → the ledger (§9) and
reconciliation (§10).

Underneath all four sits a fifth, duller property: the schemes that carry the money have rules
(cut-off times, settlement days, business calendars) that change independently of the code. →
schemes as data (§11).

## 3. The money path

Every movement of money is a balanced journal entry between three accounts. The postings for each
payment event are in one file, `src/PaymentsBench.Domain/Ledger/Postings.cs`, so the whole path can
be read top to bottom:

| Event | Posting | Debit | Credit |
|---|---|---|---|
| Initiated | `Reserve` | `customer-funds` | `pending-outbound` |
| Settled | `Settle` | `pending-outbound` | `nostro` |
| Rejected (before settlement) | `Release` | `pending-outbound` | `customer-funds` |
| Returned (after settlement) | `Reverse` | `nostro` | `customer-funds` |

Customer funds are reserved the moment a payment is initiated, sit in `pending-outbound` while the
transfer is in flight, and leave through `nostro` only when the partner says the transfer settled.
A rejection releases the reservation; a return reverses the settlement. The ledger test
`The_full_money_path_of_a_returned_payment_nets_to_zero_on_customer_funds_and_nostro`
(`tests/PaymentsBench.Domain.Tests/LedgerTests.cs`) is the property that matters: after
reserve, settle and reverse, the customer's money is back where it started and every account nets
to zero.

The life of one payment through the API, from [`ARCHITECTURE.md`](ARCHITECTURE.md):

```text
POST /payments                     Initiated   ledger: customer-funds -> pending-outbound
POST /payments/{id}/submit         Submitted   committed before the partner call
   partner accepted                Accepted
   partner rejected                Rejected    ledger: pending-outbound -> customer-funds
   call did not complete           Submitted   nothing; the resolver takes it from here
POST /payments/{id}/resolve        asks the partner by end-to-end id; resubmits only on NotFound
POST /partner/notifications        Settled     ledger: pending-outbound -> nostro
                                   Rejected    ledger: pending-outbound -> customer-funds
                                   Returned    ledger: nostro -> customer-funds
POST /reconciliation/runs/{date}   statement vs settled payments -> matched + breaks
```

## 4. Architecture

Four projects carry the logic, in strict dependency order (`docs/ARCHITECTURE.md`, "The shape"):

| Project | Knows | Holds |
|---|---|---|
| `PaymentsBench.Domain` | nothing | `Money`, `Payment` and `PaymentTransitions`, `JournalEntry` and `Postings`, `SchemeRules` and `ValueDateCalculator` |
| `PaymentsBench.Application` | the domain and its ports | the handlers (initiate, submit, resolve, apply notification), `ReconciliationService`, the ports (`IPaymentRepository`, `ILedger`, `IOutbox`, `IInbox`, `IUnitOfWork`, `IPartnerGateway`, `IStatementSource`, `IClock`) |
| `PaymentsBench.Infrastructure` | the ports | in-memory and EF Core/PostgreSQL stores, `OutboxDispatcher`, `HttpPartnerGateway`, `HttpStatementSource` |
| `PaymentsBench.Api` | all of the above | the HTTP surface; `Program.cs` is the manifest |

Around them: `PaymentsBench.ServiceDefaults` (OpenTelemetry, health, service discovery; by rule no
domain type), `PaymentsBench.AppHost` (the Aspire composition root: one PostgreSQL with a
`payments` database, the partner simulator, the API that references both) and
`PaymentsBench.PartnerSimulator` (the simulated bank).

**Storage modes.** `InfrastructureRegistration.AddPaymentsInfrastructure`
(`src/PaymentsBench.Infrastructure/InfrastructureRegistration.cs`) picks the stores once, at start-up.
With a connection string named `payments`, every store is EF Core on PostgreSQL, the outbox has a
dispatcher, and the schema is applied by a hosted service. Without one, every store is in memory,
the unit of work is a no-op, and the log says, at warning level, that the service is running
degraded and nothing survives a restart. A missing database degrades the service instead of
stopping it; the tests use this mode.

**Persistence.** `PaymentsDbContext` stores rows, not aggregates; the repository rebuilds a
`Payment` by replaying the moves its stored status implies, so the domain's invariants hold on the
way out of the database as well as on the way in. `Version` is a concurrency token
(`src/PaymentsBench.Infrastructure/Persistence/PaymentsDbContext.cs`). No test races two writers yet
(register R6).

**Observability.** One OpenTelemetry trace per request via `ServiceDefaults`; a
`payments.transitions` counter on the `PaymentsBench` meter, tagged by status
(`src/PaymentsBench.Api/Program.cs`); `/health` and `/alive`; OTLP export when
`OTEL_EXPORTER_OTLP_ENDPOINT` is set. The Aspire dashboard shows all of it with no configuration.

## 5. The seven rules

[`ARCHITECTURE.md`](ARCHITECTURE.md) states seven rules. They are quoted here in short form, each
with where it is enforced.

| # | Rule | Enforced in |
|---|---|---|
| 1 | Submitted is committed before the partner call. | `SubmitPaymentHandler.HandleAsync`: `MarkSubmitted`, update, outbox, commit, then `partner.SubmitAsync` |
| 2 | An unknown outcome is never retried. | `HttpPartnerGateway.SubmitAsync` maps failures to `SubmitOutcome.Unknown`; the resilience pipeline retries `GET` only (`InfrastructureRegistration.cs`) |
| 3 | Resubmission needs the partner's word. | `ResolveSubmittedPaymentHandler`: only `PartnerStatus.NotFound` leads to a second submit, with the same end-to-end id |
| 4 | The ledger is append-only and balanced. | `JournalEntry` cannot be constructed unbalanced per currency; balances are sums |
| 5 | Events go through the outbox, notifications through the inbox. | `IOutbox.EnqueueAsync` in every handler's unit of work; `IInbox.TryRecordAsync` first in `ApplyPartnerNotificationHandler` |
| 6 | Reconciliation produces breaks, not alerts. | `ReconciliationService` returns a `ReconciliationReport` with named `BreakKind`s |
| 7 | Scheme rules are data. | `scenarios/schemes.yaml`, read into `SchemeRules` |

Rules 1 to 3 are one argument and the core of the repository: if the state before the call is
durable, and an unknowable result is never guessed at, and the only thing allowed to trigger a
second attempt is the partner's own statement that the first one does not exist, then a transfer is
sent at most once and is never lost track of. F-2 in [`FINDINGS.md`](FINDINGS.md) is the evidence
that the argument holds even when the code around it is wrong: an exception escaped the gateway, and
rule 1 alone was enough for the resolver to recover without a duplicate.

## 6. Idempotency

`POST /payments` requires an `Idempotency-Key` header; without it the API answers `400`
(`src/PaymentsBench.Api/Program.cs`). `InitiatePaymentHandler`
(`src/PaymentsBench.Application/Payments/InitiatePayment.cs`) computes `RequestHash.Of(command)`, a
SHA-256 over a canonical string of the fields that make two requests "the same request": end-to-end
id, amount formatted to two decimals in the invariant culture, currency upper-cased, debtor and
creditor accounts, and scheme (`RequestHash.cs`). Then:

- no payment with that key: create one, reserve the funds in the ledger and enqueue
  `PaymentInitiated`, all in one unit of work;
- a payment with that key and the same hash: return the original payment;
- a payment with that key and a different hash: `IdempotencyConflictException`, which the API maps
  to `409`.

Three tests in `tests/PaymentsBench.Application.Tests/IdempotencyTests.cs` pin this: the same
request twice is one payment and one reservation; the same key with a different body is a conflict;
different keys are different payments. What is not yet tested is two identical requests racing each
other into the database at once; that is a concurrency question and sits with register row R6.

## 7. The unknown outcome and the resolver

ADR [0001](adr/0001-no-blind-resubmit.md) is the decision this repository is built around. A submit
can fail in three ways that look alike to the caller: the connection drops before the request
lands, the request lands and the response is lost, or the partner records the transfer and returns
a 5xx. A naive retry creates a second transfer in two of the three.

**Submit.** `SubmitPaymentHandler` (`src/PaymentsBench.Application/Payments/SubmitPayment.cs`) moves
the payment to `Submitted`, enqueues `PaymentSubmitted` and commits, and only then calls the
partner. `HttpPartnerGateway.SubmitAsync` turns the answer into one of three outcomes:

- `422` from the partner → `Rejected`, with the partner's reason code; the funds are released;
- any other non-success status, an empty body, or an `HttpRequestException`,
  `TaskCanceledException`, `TimeoutRejectedException` or `BrokenCircuitException` → `Unknown`;
- a success with a partner reference → `Accepted`.

On `Unknown` the handler does nothing at all: the payment stays at `Submitted`, which is the state
that means "ask before you act". The HTTP client's resilience handler is configured to retry only
`GET` requests (`InfrastructureRegistration.cs`), so the submit `POST` is sent once. The predicate is
stricter than intended: it retries a `GET` on a 5xx response but never on an exception, so a status
lookup that times out is not retried either ([`FINDINGS.md`](FINDINGS.md), F-5).

**Resolve.** `POST /payments/{id}/resolve` runs `ResolveSubmittedPaymentHandler`
(`ResolveSubmittedPayment.cs`). If the payment is not at `Submitted` it returns `AlreadyResolved`.
Otherwise it asks the partner for the transfer by end-to-end id and applies the answer:

| Partner says | Resolver does |
|---|---|
| `NotFound` | resubmits once with the same end-to-end id, and applies an accepted or rejected answer |
| `Accepted` | moves the payment to `Accepted` |
| `Settled` | moves it to `Settled` and posts `Settle` |
| `Rejected` | moves it to `Rejected` and posts `Release` |
| `Returned` | refuses: a return for a transfer never accepted on this side is an error |

The resubmit carries the same end-to-end id so that, if the first attempt arrives late, the partner
can recognise it as a duplicate. That is an assumption about the partner, stated in ADR 0001's
consequences; the simulated partner honours it (`transfers.GetOrAdd(request.EndToEndId, ...)` in
`src/PaymentsBench.PartnerSimulator/Program.cs`), and a real partner that did not would force a
different design.

Five tests in `tests/PaymentsBench.Application.Tests/SubmitAndResolveTests.cs` cover accepted,
rejected, timeout-never-resubmitted-blind, resubmit-only-on-not-found, and resolving a payment that
is not submitted. One branch is not covered and is named badly: when the resubmit's own outcome is
unknown, the resolver returns `SafeToResubmit` ([`FINDINGS.md`](FINDINGS.md), F-3). Nothing calls
the resolver on its own yet; it is an endpoint, not a schedule (register R8).

## 8. Outbox and inbox

**Outbox.** Every handler enqueues the payment's domain events (`PaymentInitiated`,
`PaymentSubmitted`, `PaymentAccepted`, `PaymentSettled`, `PaymentRejected`, `PaymentReturned`, in
`src/PaymentsBench.Domain/Payments/PaymentEvents.cs`) through `IOutbox` in the same unit of work as
the state change and the ledger entry. With PostgreSQL, `OutboxDispatcher`
(`src/PaymentsBench.Infrastructure/Outbox/OutboxDispatcher.cs`) polls every two seconds, takes up to
100 undispatched messages oldest first, hands each to `IEventPublisher`, and marks it dispatched
only after the publish returned. A crash between the publish and the mark publishes the message
again: delivery is at least once by design, and consumers must deduplicate on the message id.

What it publishes to today is a log line (`LoggingEventPublisher`). No broker is wired and no
consumer exists, so at-least-once delivery to a consumer is a design claim, not a tested property:
register row R5. In the in-memory mode there is no dispatcher at all; events accumulate in the
in-memory outbox.

**Inbox.** Partner notifications (`POST /partner/notifications`) are delivered at least once too.
`ApplyPartnerNotificationHandler` (`ApplyPartnerNotification.cs`) records the notification id in
`IInbox` before doing anything else; a second delivery of the same id returns `Duplicate` and
changes nothing. A notification for an end-to-end id the service does not know is recorded and
reported as `UnknownPayment`, not dropped, because it is exactly what reconciliation needs to see.
Tests in `NotificationTests.cs`: a settlement notification delivered twice settles once; a return
after settlement gives the customer their money back; a notification for an unknown payment is
reported.

## 9. The ledger

ADR [0002](adr/0002-double-entry-ledger.md): every money movement is a `JournalEntry` with at least
two `JournalLine`s that balance per currency; entries are appended, never updated or deleted; a
correction is another entry; balances are sums over lines. Direction is the side (debit or credit),
never a negative amount.

The five ledger tests (`tests/PaymentsBench.Domain.Tests/LedgerTests.cs`) prove that an unbalanced
entry cannot exist, that balance is checked per currency, that direction is the side, that a
rejected payment releases the reservation, and that a returned payment nets to zero on
`customer-funds` and `nostro`. `GET /payments/{id}/ledger` returns a payment's entries;
`GET /ledger/balances/{account}/{currency}` returns a derived balance.

`BalanceAsync` is a sum over rows. ADR 0002 records the consequence: at volume that needs a
materialised balance maintained by the same entries, with the entries still the source of truth.

## 10. Reconciliation

ADR [0003](adr/0003-reconciliation-produces-a-work-queue.md): a statement that disagrees with the
ledger is the normal case on some days, and an alert saying "reconciliation failed" tells the person
on call nothing. `POST /reconciliation/runs/{date}` runs `ReconciliationService`
(`src/PaymentsBench.Application/Reconciliation/ReconciliationService.cs`): it reads the day's
statement through `IStatementSource`, takes the payments whose status is `Settled` and whose value
date is that day, and matches debit lines to payments by end-to-end id and exact amount and
currency. The output is a `ReconciliationReport`: the number matched, and a list of breaks, each
with a kind, the end-to-end id, and both amounts where both exist.

| Break kind | Meaning |
|---|---|
| `OnStatementOnly` | the statement books a movement with no settled payment behind it |
| `InLedgerOnly` | a payment settled that the statement does not show |
| `AmountMismatch` | both sides have it, with different amounts |

A clean day is `IsClean == true`. Three tests in `ReconciliationTests.cs` cover a clean day, each
kind of break named, and a settled payment missing from the statement. Matching is exact: fees,
tolerances and multi-line bookings are not handled, and ADR 0003 says each would be a new break
kind, not a fuzzier match.

One defect is known. A payment that settled on a day and was returned later is no longer `Settled`,
so its settlement line on that day's statement is reported as `OnStatementOnly`
([`FINDINGS.md`](FINDINGS.md), F-4). The statement the simulated partner produces is a JSON
reduction of what an ISO 20022 `camt.053` carries, and it has no line for the return itself
(register R2).

## 11. Schemes and calendars

`scenarios/schemes.yaml` holds three schemes as data:

| Scheme | Currency | Cut-off | Settlement business days |
|---|---|---|---|
| `sepa` | EUR | 16:00 | 1 |
| `faster-payments` | GBP | 23:59 | 0 |
| `swift` | USD | 15:00 | 2 |

`ValueDateCalculator.ValueDateFor` (`src/PaymentsBench.Domain/Schemes/SchemeRules.cs`) computes the
value date: the same business day if the submission is before cut-off on a business day, else the
next business day, then plus the scheme's settlement days. Three tests in
`tests/PaymentsBench.Domain.Tests/ValueDateTests.cs`: before cut-off, after cut-off, weekends
skipped.

The only business calendar is `WeekendOnlyCalendar`. There are no public holidays, no per-scheme
calendar and no TARGET2 closing days (register R7), so a value date computed across a bank holiday
is wrong today.

## 12. The partner simulator and the scenario classes

**The simulator.** `src/PaymentsBench.PartnerSimulator/Program.cs` is a bank that misbehaves on
request. It is not a model of any real scheme's API: its shape is invented (register R1). It
accepts transfers (`POST /transfers`, deduplicated by end-to-end id), answers lookups
(`GET /transfers/{endToEndId}`), lets a test settle or return a transfer, and serves a statement per
date (`GET /statements/{date}`). `PUT /faults` switches the fault profile at runtime:

| Fault | Effect |
|---|---|
| `None` | behaves |
| `Latency` | delays the response (1,500 ms by default) |
| `TimeoutAfterAccept` | records the transfer, then holds the response (10,000 ms by default) |
| `ServerError` | returns `503` without recording anything |
| `RejectAll` | returns `422` with a reason code (`AC01` by default) |
| `StatementDrift` | adds a drift (0.01 by default) to every statement amount |

**The scenarios.** `scenarios/*.yaml` describe what the rail must survive as data: the partner's
fault, the calls made, and the state the system must end in. Seven files in four classes
(`scenarios/README.md`):

| Class | Files | What it proves |
|---|---|---|
| `happy` | `happy-sepa-transfer.yaml`, `return-after-settlement.yaml` | the plain path works and the ledger nets correctly |
| `partner-failure` | `timeout-after-accept.yaml`, `server-error-then-not-found.yaml` | a refusal, a 5xx or a timeout after acceptance leaves a recoverable state and never a duplicate |
| `duplicate` | `duplicate-request.yaml`, `duplicate-notification.yaml` | the same request or notification twice |
| `reconciliation` | `statement-drift.yaml` | a disagreeing statement produces named breaks |

**They are a specification, not an executed suite.** `ScenarioFilesTests` checks that every file
parses and that every class has at least one scenario; no runner drives them against the API and
the simulator yet. That is register row R3, and it is the closing condition that would also guard
F-1 and F-2 against regression.

## 13. CI and governance

`.github/workflows/ci.yml` runs on every pull request and on pushes to `main`: restore, build in
Release, test, and upload the test results. `AGENTS.md` sets the rules of the road: .NET 10,
`dotnet build -warnaserror` and `dotnet test` before a push with analyzers on, one pull request per
change against `main` with CI green before merge, scheme rules and scenarios as data, nothing that
talks to a real bank, no secrets in the tree.

Two documents carry the governance that code cannot:

- the **register** ([`REGISTER.md`](REGISTER.md)): a change that closes a row moves it to *Closed*
  with the date; a change that opens a gap adds a row; the register is never silently wrong;
- the **decision records** ([`adr/`](adr/)): a new decision is a new record, not an edit.

Further work is planned in three streams, each in its own plan file under [`plans/`](plans/): core
and correctness (migrations, a scenario runner, a resolver sweep, calendars, an ISO 20022 edge,
integration tests against PostgreSQL), platform (repository baseline, containers, deployment,
identity, load tests, a security review), and documentation (this document and its PDF and site
editions). Until a slice merges it is a plan, and this document does not describe it as done.

## 14. What the tests and the walk-through caught

**Five findings, none caught by a unit test** ([`FINDINGS.md`](FINDINGS.md)):

| # | Defect | Found by | Status |
|---|---|---|---|
| F-1 | Enums crossed the wire as integers; every partner notification and statement line failed to bind | HTTP walk-through | Fixed |
| F-2 | A timeout after the partner accepted surfaced as a 500, not as an Unknown outcome | HTTP walk-through | Fixed |
| F-3 | The resolver reports `SafeToResubmit` after a resubmit whose own outcome is unknown | Writing this document | Open |
| F-4 | A returned payment is an `OnStatementOnly` break for its settlement day | Writing this document | Open |
| F-5 | The status lookup is never retried on an exception, only on a 5xx | Writing this document | Open |

The pattern is the finding. The 37 tests are good at what they test: the state machine, the
postings, idempotency, the resolver's decisions, the inbox. They do it in one process, against
in-memory stores and a scripted partner, so a defect that lives between processes (F-1), in a
library's exception types (F-2), in an HTTP pipeline no test drives (F-5), or in an assumption the
code and its tests share (F-3, F-4) cannot
fail them. F-2 is also the best evidence for the design: the defect was real, and the rule that the
status is committed before the call meant it cost an error message, not a duplicate transfer.

## 15. Production readiness, honestly

What would have to change before this moved anyone's money, stated without softening:

- **The partner is simulated.** Nothing has talked to a real bank or a scheme sandbox, no ISO 20022
  message is produced or parsed, and the partner's support for lookup by end-to-end id is an
  assumption the simulator was written to satisfy (R1, R2, ADR 0001).
- **The scenarios do not run.** Seven files specify the behaviour that matters most; none is
  executed end to end (R3). The two findings that a walk-through caught have no automated guard.
- **PostgreSQL has not run in CI.** The EF Core stores compile and the schema is applied with
  `EnsureCreated`; no test has written a payment to a real database, and no migration exists (R4).
- **The degraded mode is degraded.** In memory, the unit of work is a no-op, so "in the same
  commit" holds only because nothing commits; there is no outbox dispatcher; nothing survives a
  restart. The log says so at start-up; a reader of this document should know it too.
- **Concurrency is a column.** `Version` is a concurrency token; nothing proves two writers on one
  payment lose cleanly (R6).
- **Nothing resolves on its own.** A payment can sit at `Submitted` until someone calls resolve
  (R8, ADR 0001).
- **No load, no chaos.** Throughput has never been measured and faults are switched by hand (R9).
- **No authentication on the API, no sanctions, AML or limits.** The service moves any amount to any
  account for any caller (R10 for the compliance controls).
- **Three open defects** (F-3, F-4, F-5) sit in the resolver's reporting, in reconciliation and in
  the lookup's retry policy.

## 16. The register

[`REGISTER.md`](REGISTER.md) has ten open rows and none closed, quoted here by their headlines:

| # | Since | Not proved |
|---|---|---|
| R1 | 2026-10-13 | Nothing here has ever talked to a real bank. |
| R2 | 2026-10-13 | No ISO 20022 message is parsed or produced. |
| R3 | 2026-10-13 | The scenario files are a specification, not an executed suite. |
| R4 | 2026-10-13 | PostgreSQL persistence has not run in CI. |
| R5 | 2026-10-13 | The outbox dispatcher publishes to a log line. |
| R6 | 2026-10-13 | Concurrency is a column, not a test. |
| R7 | 2026-10-13 | The business calendar is weekends only. |
| R8 | 2026-10-13 | The resolver is called, never scheduled. |
| R9 | 2026-10-13 | No load, no chaos. |
| R10 | 2026-10-13 | Sanctions, AML, limits: absent. |

Each row has a closing condition in the register itself. R10 is out of scope for the bench and stays
open as a statement of scope.

## 17. Architecture decision records

| ADR | Decision |
|---|---|
| [0001](adr/0001-no-blind-resubmit.md) | An unknown submit outcome is never retried blind; the resolver asks the partner and resubmits only on `NotFound`. |
| [0002](adr/0002-double-entry-ledger.md) | Money is a double-entry, append-only ledger; balances are derived. |
| [0003](adr/0003-reconciliation-produces-a-work-queue.md) | Reconciliation produces breaks, not alerts. |

## 18. Non-goals

- **Being a payments product.** It is a bench; its value is the mechanisms and the evidence about
  them.
- **Talking to a real bank.** The partner is simulated, and every document says so.
- **Compliance controls.** Sanctions screening, AML and limits are out of scope (R10).
- **Inbound payments, FX, fees, multi-currency netting.** The rail is outbound, one currency per
  scheme.
- **Fuzzy reconciliation.** Matching is exact; anything else is a new, named break kind (ADR 0003).

## 19. Relationship to the standards

This repository conforms to
[architecture-standards](https://github.com/konradcinkusz/architecture-standards) and does not
re-derive it: the reference architecture's principles, the repository baseline, ticket delivery and
the research-documentation conventions load with every session through `.claude/settings.json`
(`AGENTS.md`). This document's structure follows the documentation of
[agent-eval-bench](https://github.com/konradcinkusz/agent-eval-bench): a long-form overview as the
source of truth, a findings report written to be unflattering, a register of what is not proved,
and presentation editions that add nothing the overview does not contain.

## 20. Getting the PDF

A LaTeX edition of this document lives at
[`papers/payments-bench-overview.tex`](papers/payments-bench-overview.tex). It is a curated
presentation of this file, introduces no fact this file does not contain, and states in its own
header that it is a project overview, not a research paper. It uses the house preamble
([`papers/house-preamble.tex`](papers/house-preamble.tex), copied unmodified from
architecture-standards and checked by digest in `scripts/check-papers.mjs`) and includes the
diagrams from [`diagrams/`](diagrams/) rendered to vector PDF. The PDF is build output and never
committed.

The **Build Overview PDF** workflow (`.github/workflows/build-overview-pdf.yml`) builds it:

1. Open the repository's **Actions** tab and select **Build Overview PDF**.
1. Click **Run workflow**, or open any pull request that touches `docs/papers/` or
   `docs/diagrams/` (the workflow runs on those automatically).
1. Download `PaymentsBench_Overview_PDF` from the run's **Artifacts** section.

On a `v*` tag the same workflow attaches `PaymentsBench_Overview.pdf` to the GitHub Release.
Locally, with Node and a TeX distribution installed:

```bash
npm ci && npm run render:diagrams
cd docs/papers && latexmk -pdf payments-bench-overview.tex
```

## 21. Getting the site

A generated static one-pager served from `docs/` by GitHub Pages is planned as slices C5 and C6 in
[`plans/stream-c-docs.md`](plans/stream-c-docs.md). It does not exist yet; this section will say
where it is once it has merged.
