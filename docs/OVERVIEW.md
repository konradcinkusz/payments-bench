# payments-bench: Comprehensive Project Overview

**An outbound bank-transfer service that shows the mechanisms a payment rail needs to move money
exactly once and to recover when the bank on the other end misbehaves. The bank is simulated; the
mechanisms are real code with tests. The service is the specimen; the evidence about it is the
deliverable.**

- **Status**: all three streams of work in [`plans/`](plans/) (core and correctness, platform,
  documentation) have merged; this document describes `main` at `579e135` plus this pass, measured
  on 2026-10-13. It describes what has merged, never what is planned.
- **Companion documents**: [`START-HERE.md`](START-HERE.md) (which document to read) ·
  [`ARCHITECTURE.md`](ARCHITECTURE.md) (the shape and the seven rules) ·
  [`REGISTER.md`](REGISTER.md) (what is not proved) · [`FINDINGS.md`](FINDINGS.md) (what building
  it caught) · [`adr/`](adr/) (decisions) · [`../scenarios/`](../scenarios/) (the fault scenarios as
  data) · [`DIAGRAMS.md`](DIAGRAMS.md) · [`security/SECURITY-REVIEW.md`](security/SECURITY-REVIEW.md) ·
  [`DEPLOYMENT.md`](DEPLOYMENT.md) (the service) · [`DEPLOYMENT-DOCS.md`](DEPLOYMENT-DOCS.md) (these
  documents).
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

- an HTTP API (`src/PaymentsBench.Api`) to initiate (JSON or an ISO 20022 `pain.001` file),
  list, submit, resolve and inspect an outbound transfer, receive partner notifications, run
  reconciliation and read ledger balances, behind bearer-token authentication;
- a six-state payment machine whose allowed moves are one table
  (`src/PaymentsBench.Domain/Payments/PaymentTransitions.cs`);
- idempotent initiation: an `Idempotency-Key` plus a SHA-256 hash of the request body, and
  optimistic concurrency that makes one of two racing writers lose cleanly;
- a submit that commits `Submitted` before calling the partner, and maps every failure after the
  request may have left the process to an **Unknown** outcome that is never retried blind;
- a resolver that asks the partner by end-to-end id and resubmits only when the partner says it has
  no record, and a background sweep that runs the resolver on its own;
- a transactional outbox with an at-least-once contract and a deduplicating consumer, and an inbox
  that deduplicates partner notifications;
- a double-entry, append-only ledger with three accounts, `customer-funds`, `pending-outbound` and
  `nostro` (`src/PaymentsBench.Domain/Ledger/LedgerAccounts.cs`);
- reconciliation of a day's statement (JSON or `camt.053`) against settled payments, returning
  named breaks as a work queue;
- scheme rules and dated holiday calendars as data under `scenarios/`;
- a **simulated** partner bank (`src/PaymentsBench.PartnerSimulator`) with five fault profiles,
  and ten scenario files that run end to end against the API and the simulator in CI;
- PostgreSQL through EF Core migrations, a degraded in-memory mode, container images, a Fly.io
  topology that has never been deployed, a k6 load run, and a security review.

365 tests pass on `main` (`dotnet test` against PostgreSQL on 2026-10-13 at `579e135`; per project
in §13). Eighteen defects are recorded in [`FINDINGS.md`](FINDINGS.md), thirteen of them fixed, and
none was caught by a unit test failing on its own. Nine register rows are closed with proof and
eighteen are open ([`REGISTER.md`](REGISTER.md)); the first open row is still that nothing here has
ever talked to a real bank.

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
| `PaymentsBench.Domain` | nothing | `Money`, `Payment` and `PaymentTransitions`, `JournalEntry` and `Postings`, `SchemeRules`, `HolidayCalendar` and `ValueDateCalculator` |
| `PaymentsBench.Application` | the domain and its ports | the handlers (initiate, submit, resolve, apply notification, sweep), `ReconciliationService`, the ports (`IPaymentRepository`, `ILedger`, `IOutbox`, `IInbox`, `IUnitOfWork`, `IPartnerGateway`, `IStatementSource`, `ISchemeCatalog`, `IClock`) |
| `PaymentsBench.Infrastructure` | the ports | in-memory and EF Core/PostgreSQL stores, migrations and `MigrationGate`, the outbox dispatcher and publishers, `SubmittedPaymentSweeper`, `HttpPartnerGateway` and `PartnerResilience`, `HttpStatementSource`, the scheme catalog loader |
| `PaymentsBench.Api` | all of the above | the HTTP surface, validation and problem details, identity and grants; `Program.cs` is the manifest |

Around them: `PaymentsBench.Iso20022` (the `pain.001` and `camt.053` edge, referencing Application
only; ADR 0014), `PaymentsBench.ServiceDefaults` (OpenTelemetry, health, an opt-in Prometheus
endpoint, the request-size cap; by rule no domain type), `PaymentsBench.AppHost` (the Aspire
composition root: one PostgreSQL with a `payments` database, the partner simulator, the API, and
authservice when a signing key is configured) and `PaymentsBench.PartnerSimulator` (the simulated
bank).

**Storage modes.** `InfrastructureRegistration.AddPaymentsInfrastructure` picks the stores once, at
start-up. With a connection string named `payments`, every store is EF Core on PostgreSQL, the
schema is the `InitialSchema` migration applied by `MigrationHostedService` after the server starts,
and every background service waits on `MigrationGate`; `/health` is unhealthy until the migration
finishes (ADR 0008). Without a connection string every store is in memory and the log says, at
warning level, that the service is running degraded and nothing survives a restart. The in-memory
repository follows the same concurrency rule as PostgreSQL (ADR 0013), so the degraded mode behaves
the same way under a race; it has no outbox dispatcher.

**Identity.** The API holds no signing key. It validates RS256 bearer tokens against authservice's
JWKS (`Auth:Authority`, `Auth:Issuer`, `Auth:Audience`; ADR 0015). Every endpoint needs a token
except `/health` and `/alive`; the partner webhook needs the `payments.partner.notify` grant, and the
ledger and reconciliation endpoints need `payments.operate` (ADR 0016). Without `Auth:Authority` the
API allows anonymous calls in Development only, never on Fly, and refuses to start anywhere else.

**Observability.** One OpenTelemetry trace per request with health probes filtered out; the
`PaymentsBench` meter carries `payments.transitions` (tagged by status),
`payments.submitted.oldest_age_seconds` and `payments.sweep.resolved` (tagged by outcome); OTLP
export when `OTEL_EXPORTER_OTLP_ENDPOINT` is set; `/metrics` when `Metrics:Prometheus:Enabled` is
true; a provisioned Grafana dashboard under `docs/ops/grafana/`.

## 5. The seven rules

[`ARCHITECTURE.md`](ARCHITECTURE.md) states seven rules. They are quoted here in short form, each
with where it is enforced.

| # | Rule | Enforced in |
|---|---|---|
| 1 | Submitted is committed before the partner call. | `SubmitPaymentHandler.HandleAsync`: `MarkSubmitted`, update, outbox, commit, then `partner.SubmitAsync` |
| 2 | An unknown outcome is never retried. | `HttpPartnerGateway.SubmitAsync` maps failures to `SubmitOutcome.Unknown`; `PartnerResilience.ShouldRetry` never retries a `POST` |
| 3 | Resubmission needs the partner's word. | `ResolveSubmittedPaymentHandler`: only `PartnerStatus.NotFound` leads to a second submit, with the same end-to-end id |
| 4 | The ledger is append-only and balanced. | `JournalEntry` cannot be constructed unbalanced per currency; balances are sums |
| 5 | Events go through the outbox, notifications through the inbox. | `IOutbox.EnqueueAsync` in every handler's unit of work; `IInbox.TryRecordAsync` first in `ApplyPartnerNotificationHandler` |
| 6 | Reconciliation produces breaks, not alerts. | `ReconciliationService` returns a `ReconciliationReport` with named `BreakKind`s |
| 7 | Scheme rules are data. | `scenarios/schemes.yaml` and `scenarios/calendars/*.yaml`, loaded into `ISchemeCatalog` at start-up |

Rules 1 to 3 are one argument and the core of the repository: if the state before the call is
durable, and an unknowable result is never guessed at, and the only thing allowed to trigger a
second attempt is the partner's own statement that the first one does not exist, then a transfer is
sent at most once and is never lost track of. F-2 in [`FINDINGS.md`](FINDINGS.md) is the evidence
that the argument holds even when the code around it is wrong: an exception escaped the gateway, and
rule 1 alone was enough for the resolver to recover without a duplicate.

## 6. Idempotency

`POST /payments` requires an `Idempotency-Key` header; without it the API answers a `400` problem
(ADR 0009). `InitiatePaymentHandler` (`src/PaymentsBench.Application/Payments/InitiatePayment.cs`)
computes `RequestHash.Of(command)`, a SHA-256 over a canonical string of the fields that make two
requests "the same request": end-to-end id, amount formatted to two decimals in the invariant
culture, currency upper-cased, debtor and creditor accounts, and scheme. Then:

- no payment with that key: create one, reserve the funds in the ledger and enqueue
  `PaymentInitiated`, all in one unit of work;
- a payment with that key and the same hash: return the original payment;
- a payment with that key and a different hash: `IdempotencyConflictException`, a `409`.

`POST /payments/pain001` reads an ISO 20022 `pain.001.001.09` file into one `InitiatePayment` per
transaction, with the key `MsgId/PmtInfId/EndToEndId`, so posting the same file twice replays
instead of duplicating (ADR 0014).

Two identical requests racing each other are the case a sequential check cannot see. The unique
index on the key rejects the second insert, and the loser gets a `409`, not the winner's `201` body
(ADR 0013). That is register row R19. The key is also global, not per caller: one caller can replay
another's key and learn that the payment exists (R21, security finding SEC-05). Tests:
`tests/PaymentsBench.Application.Tests/IdempotencyTests.cs`, `ConcurrencyTests` in
`tests/PaymentsBench.Integration.Tests`, and `Pain001EndpointTests`.

## 7. The unknown outcome and the resolver

ADR [0001](adr/0001-no-blind-resubmit.md) is the decision this repository is built around. A submit
can fail in three ways that look alike to the caller: the connection drops before the request
lands, the request lands and the response is lost, or the partner records the transfer and returns
a 5xx. A naive retry creates a second transfer in two of the three.

**Submit.** `SubmitPaymentHandler` (`src/PaymentsBench.Application/Payments/SubmitPayment.cs`) moves
the payment to `Submitted`, enqueues `PaymentSubmitted` and commits, and only then calls the
partner. `HttpPartnerGateway.SubmitAsync` turns the answer into one of three outcomes:

- `422` from the partner → `Rejected`, with the partner's reason code; the funds are released;
- any other non-success status, an empty body, or a transport error, timeout or open circuit →
  `Unknown`;
- a success with a partner reference → `Accepted`.

On `Unknown` the handler does nothing at all: the payment stays at `Submitted`, which is the state
that means "ask before you act". `PartnerResilience.ShouldRetry`
(`src/PaymentsBench.Infrastructure/Partner/PartnerResilience.cs`) never retries a `POST`, so the
submit is sent once. It retries a `GET` that threw a transport error, timed out per attempt or
answered 5xx, and never one that hit an open circuit or ran out of its total budget. That rule took
two findings to reach: F-5 (a lookup that threw was never retried) and F-9 (the fix then retried an
open circuit and slowed every recovery during an outage).

**Resolve.** `POST /payments/{id}/resolve` runs `ResolveSubmittedPaymentHandler`. If the payment is
not at `Submitted` it returns `AlreadyResolved`. Otherwise it asks the partner for the transfer by
end-to-end id:

| Partner says | Resolver does | Outcome |
|---|---|---|
| `NotFound` | resubmits once with the same end-to-end id; applies an accepted or rejected answer | `Accepted`, `Rejected`, or `StillUnknown` if the resubmit's own outcome is unknown |
| `Accepted` | moves the payment to `Accepted` | `Accepted` |
| `Settled` | moves it to `Settled` and posts `Settle` | `Settled` |
| `Rejected` | moves it to `Rejected` and posts `Release` | `Rejected` |
| `Unavailable` (open circuit, timeout, transport failure, 5xx) | writes nothing; the payment stays `Submitted` | `PartnerUnavailable` |
| `Returned` | refuses: a return for a transfer never accepted on this side is an error | |

`Unavailable` is never read as `NotFound`; that distinction is what keeps an outage from becoming a
resubmit. Before it existed the resolver answered 500 during an outage (F-7). `StillUnknown`
replaced a `SafeToResubmit` answer that said the opposite of the truth (F-3).

The resubmit carries the same end-to-end id so that, if the first attempt arrives late, the partner
can recognise it as a duplicate. That is an assumption about the partner, stated in ADR 0001; the
simulated partner honours it, and a real partner that did not would force a different design.

**The sweep.** `SubmittedPaymentSweeper`, a background service behind `MigrationGate`, runs
`SweepSubmittedPaymentsHandler` every `Sweeper:Interval` (30 s by default): Submitted payments older
than `Sweeper:MinimumAge` (60 s), oldest first, at most `Sweeper:BatchSize` (50), each resolved
through the same handler in its own scope (ADR 0011). The sweep never calls the partner itself, so
ADR 0001 holds by construction. ADR 0011 documents the alert, `payments.submitted.oldest_age_seconds`
above 360 s for 5 minutes with the defaults; no alert rule is deployed, and the sweep has not run on
the EF stores with two instances (R17).

## 8. Outbox and inbox

**Outbox.** Every handler enqueues the payment's domain events (`PaymentInitiated`,
`PaymentSubmitted`, `PaymentAccepted`, `PaymentSettled`, `PaymentRejected`, `PaymentReturned`) through
`IOutbox` in the same unit of work as the state change and the ledger entry. With PostgreSQL,
`OutboxDispatcher` waits on `MigrationGate` and then runs `OutboxBatchDispatcher` on a timer: it
publishes undispatched rows in `OccurredAt` order and marks a row dispatched only after its publish
returned. A crash between the two publishes the message again, with the same id. Delivery is at
least once by design (ADR 0010).

`Outbox:Publisher` selects the publisher: `log` (the default, a log line) or `channel` (a bounded
in-process channel with a `DedupingConsumer` that applies each message id once and counts the
duplicates it drops). The contract is proved in process against PostgreSQL (`OutboxContractTests`,
`DedupingConsumerTests`): an event committed with its change is delivered, a publish whose
dispatched mark is lost is redelivered and applied once, nothing is published for a failed commit.
**No broker has ever carried an event**: register row R5 stays open, narrowed. In the in-memory
mode there is no dispatcher at all.

**Inbox.** Partner notifications (`POST /partner/notifications`, which needs the
`payments.partner.notify` grant) are delivered at least once too. `ApplyPartnerNotificationHandler`
records the notification id in `IInbox` before anything else; a second delivery returns
`Duplicate` and changes nothing. A notification for an unknown end-to-end id is recorded and
reported as `UnknownPayment`, not dropped. The sender is authenticated with a bearer token and a
grant, not a partner signature (R13).

## 9. The ledger

ADR [0002](adr/0002-double-entry-ledger.md): every money movement is a `JournalEntry` with at least
two `JournalLine`s that balance per currency; entries are appended, never updated or deleted; a
correction is another entry; balances are sums over lines. Direction is the side (debit or credit),
never a negative amount.

The ledger tests (`tests/PaymentsBench.Domain.Tests/LedgerTests.cs`, and `LedgerTests` against PostgreSQL in `tests/PaymentsBench.Integration.Tests`) prove that an unbalanced
entry cannot exist, that balance is checked per currency, that direction is the side, that a
rejected payment releases the reservation, and that a returned payment nets to zero on
`customer-funds` and `nostro`. `GET /payments/{id}/ledger` returns a payment's entries;
`GET /ledger/balances/{account}/{currency}` returns a derived balance; both need the
`payments.operate` grant.

`BalanceAsync` is a sum over rows. ADR 0002 records the consequence: at volume that needs a
materialised balance maintained by the same entries, with the entries still the source of truth.

## 10. Reconciliation

ADR [0003](adr/0003-reconciliation-produces-a-work-queue.md): a statement that disagrees with the
ledger is the normal case on some days, and an alert saying "reconciliation failed" tells the person
on call nothing. `POST /reconciliation/runs/{date}` (grant `payments.operate`) runs
`ReconciliationService`: it reads the day's statement through `IStatementSource`, as JSON or, when
`Partner:StatementFormat` is `camt053`, as an ISO 20022 `camt.053.001.08` document read at the edge
(ADR 0014). It takes the payments that settled with that value date, whatever happened to them
since, and matches debit lines to them by end-to-end id and exact amount and currency. The output
is a `ReconciliationReport`: the number matched, and a list of breaks, each with a kind, the
end-to-end id, and both amounts where both exist.

| Break kind | Meaning |
|---|---|
| `OnStatementOnly` | the statement books a movement with no settled payment behind it |
| `InLedgerOnly` | a payment settled that the statement does not show |
| `AmountMismatch` | both sides have it, with different amounts |

A clean day is `IsClean == true`. Matching is exact: fees, tolerances and multi-line bookings are
not handled, and ADR 0003 says each would be a new break kind, not a fuzzier match.

"Whatever happened since" is the F-4 fix: a payment settled on a day and returned later used to be
reported as `OnStatementOnly` for its settlement day. The return day still cannot reconcile, because
the simulated statement carries no credit line for the return itself (R18). The `camt.053` samples
are hand-written in the published shape, not validated against the official schemas, and no bank
issued them (R20).

## 11. Schemes and calendars

`scenarios/schemes.yaml` holds three schemes as data, each naming its calendar:

| Scheme | Currency | Cut-off | Settlement business days | Calendar |
|---|---|---|---|---|
| `sepa` | EUR | 16:00 | 1 | `target2` |
| `faster-payments` | GBP | 23:59 | 0 | `uk-faster-payments` |
| `swift` | USD | 15:00 | 2 | `us-fedwire` |

Calendars are dated data in `scenarios/calendars/*.yaml`: whether weekends are closed, the closing
days as explicit dates, and the covered range, each citing its public source (ADR 0007). There is no
Easter algorithm in code. `HolidayCalendar` answers only inside its covered range and throws
`CalendarCoverageException` outside it: a calendar that does not know a date must not call it a
business day. Schemes and calendars load at start-up into `ISchemeCatalog`; a missing or malformed
file stops start-up naming the file, and an unknown scheme or a currency the scheme does not carry
is refused at initiation with a `422`.

`ValueDateCalculator.ValueDateFor` computes the value date: the same business day before cut-off,
else the next business day, then plus the scheme's settlement days. `HolidayCalendarTests` prove that
Christmas 2026 and Good Friday and Easter Monday 2026 move the SEPA value date. **Nothing calls the
calculator at submit or acceptance yet**: the payment's value date is still set only when the
partner reports settlement. The calendars are proved; the value date a customer would be shown is
not (R11).

## 12. The partner simulator and the scenario classes

**The simulator.** `src/PaymentsBench.PartnerSimulator/Program.cs` is a bank that misbehaves on
request. It is not a model of any real scheme's API: its shape is invented (R1). It accepts
transfers (`POST /transfers`, deduplicated by end-to-end id), answers lookups, lets a test settle or
return a transfer, serves a statement per date as JSON or `camt.053`, and counts the calls it
received (`GET /diagnostics/calls`, for the scenario runner). `PUT /faults` switches the fault
profile at runtime:

| Fault | Effect |
|---|---|
| `None` | behaves |
| `Latency` | delays the response (1,500 ms by default) |
| `TimeoutAfterAccept` | records the transfer, then holds the response (10,000 ms by default) |
| `ServerError` | returns `503` without recording anything |
| `RejectAll` | returns `422` with a reason code (`AC01` by default) |
| `StatementDrift` | adds a drift (0.01 by default) to every statement amount |

Because it deduplicates by end-to-end id, it cannot say how many times a transfer was submitted, so
"no duplicate transfer" under load is checked indirectly (F-15, R15).

**The scenarios.** `scenarios/*.yaml` describe what the rail must survive as data. Ten files in
four classes (`scenarios/README.md`):

| Class | Files |
|---|---|
| `happy` | `happy-sepa-transfer.yaml`, `happy-across-closing-days.yaml`, `return-after-settlement.yaml` |
| `partner-failure` | `timeout-after-accept.yaml`, `server-error-then-not-found.yaml`, `partner-rejects.yaml` |
| `duplicate` | `duplicate-request.yaml`, `duplicate-notification.yaml` |
| `reconciliation` | `statement-drift.yaml`, `ledger-only-break.yaml` |

**They run.** `tests/PaymentsBench.Scenarios` discovers every file and runs it end to end, in
process, against a fresh API and simulator pair, on in-memory storage and, when
`PAYMENTSBENCH_TEST_POSTGRES` is set as in CI, on PostgreSQL; every `expect` key is asserted, and an
unknown step or key fails the case (ADR 0012). CI gates on it as a named step. Register row R3 is
closed. The runner exercises real HTTP serialisation and the real resilience pipeline without a
socket; it does not exercise a process boundary or a real clock. One of its own defects, a timeout
scenario that passed without its timeout (F-8), is the reason the runner checks itself
(`RunnerSelfTests`).

## 13. CI and governance

The tests on `main`, counted by `dotnet test` against PostgreSQL on 2026-10-13 at `579e135`:

| Project | Tests |
|---|---|
| `tests/PaymentsBench.Domain.Tests` | 30 |
| `tests/PaymentsBench.Application.Tests` | 104 |
| `tests/PaymentsBench.Api.Tests` | 89 |
| `tests/PaymentsBench.Iso20022.Tests` | 62 |
| `tests/PaymentsBench.Integration.Tests` | 41 |
| `tests/PaymentsBench.Scenarios` | 27 |
| `tests/PaymentsBench.ServiceDefaults.Tests` | 12 |
| **Total** | **365** |

`tests/PaymentsBench.E2E` adds a Playwright suite, run by CI's end-to-end job against the API and
the simulator in the degraded mode and against the documentation site from `file://`.

`.github/workflows/ci.yml` runs on every pull request and on pushes to `main`: build with warnings
as errors, all tests against a `postgres:16-alpine` service container, the scenarios as their own
gate, the documentation lint (markdownlint, relative links, diagrams byte-identical with
`DIAGRAMS.md`, the house style of the papers, the generated site current), and the end-to-end
suite. Beside it: `codeql.yml`, `secret-scan.yml` (gitleaks over the full history), `lint.yml`,
`containers.yml` (images built and smoke-tested), `deploy.yml` (tag-driven Fly.io deploy, which has
never deployed), `load.yml` (the k6 run, on demand), `build-overview-pdf.yml` and
`deploy-pages.yml`.

`AGENTS.md` sets the rules of the road, and two documents carry the governance that code cannot:
the **register** ([`REGISTER.md`](REGISTER.md)), where a change that closes a row moves it to
*Closed* with its proof and a change that opens a gap adds a row, so the register is never silently
wrong; and the **decision records** ([`adr/`](adr/)), where a new decision is a new record. The work
arrived in three streams, each with its plan in [`plans/`](plans/), one pull request per slice; the
plans list every slice with its pull request.

## 14. What the tests and the walk-through caught

**Eighteen findings, none caught by a unit test failing on its own** ([`FINDINGS.md`](FINDINGS.md)):

| # | Defect | Found by | Status |
|---|---|---|---|
| F-1 | Enums crossed the wire as integers | HTTP walk-through | Fixed |
| F-2 | A timeout after the partner accepted surfaced as a 500 | HTTP walk-through | Fixed |
| F-3 | The resolver said `SafeToResubmit` after a resubmit whose outcome was unknown | Writing this document | Fixed (#21) |
| F-4 | A returned payment was a break on its settlement day | Writing this document | Fixed (#23) |
| F-5 | A status lookup that threw was never retried | Writing this document | Fixed (#23) |
| F-6 | A writer that lost a race overwrote the winner | Reading the code behind R6 | Fixed (#22) |
| F-7 | `resolve` answered 500 while the partner circuit was open | k6 load run | Fixed (#21) |
| F-8 | A timeout scenario passed without its timeout | A scenario that should have failed | Fixed (#23) |
| F-9 | The F-5 fix retried an open circuit | k6 re-run | Fixed (#30) |
| F-10 | Images stopped publishing when scheme data became required | Containers workflow | Fixed (#11) |
| F-11 | An empty metrics switch crashed start-up | Starting the service | Fixed (#9) |
| F-12 | A telemetry test read its exporter too early | A flaky CI run | Fixed (#28) |
| F-13 | Register rows fell out of the register table | Review | Fixed (#27) |
| F-14 | A payment has no owner; the idempotency key is global | Security review | Open (R21) |
| F-15 | The simulator cannot count submissions | k6 load run | Open (R15) |
| F-16 | The image build's analyzers differ from CI's | Containers workflow | Open |
| F-17 | GHCR packages start private | Deploy workflow | Open (R14) |
| F-18 | Parallel streams took the same numbers | Merging the streams | Open |

The pattern is the finding. The unit tests are good at what they test: the state machine, the
postings, idempotency, the resolver's decisions, the inbox. They run in one process against
in-memory stores and a scripted partner. The defects lived elsewhere: between processes (F-1),
in a library's exception types and an HTTP pipeline (F-2, F-5, F-7, F-9), in persistence (F-6),
in the measuring instrument itself (F-8, F-12), in the build and delivery path (F-10, F-11, F-16,
F-17), and in assumptions the code and its tests shared (F-3, F-4, F-14). Two of them, F-7 and F-9,
only appeared under load.

F-2 remains the best evidence for the design: the defect was real, and the rule that the status is
committed before the call meant it cost an error message, not a duplicate transfer.

## 15. Production readiness, honestly

What would have to change before this moved anyone's money, from the open rows of the register:

- **The partner is simulated.** Nothing has talked to a real bank or a scheme sandbox (R1). The
  ISO 20022 edge reads and writes `pain.001` and `camt.053`, but against hand-written samples never
  validated against the official schemas (R20), and the gateway still submits JSON.
- **Nothing has been deployed.** The Fly.io topology, the tag-driven workflow and
  [`DEPLOYMENT.md`](DEPLOYMENT.md) exist and validate offline; no tag has deployed anything (R14),
  and the deployment's security settings live outside the tree and are unverified (R26).
- **Payments have no owner.** Any token holder can read, list and submit any payment, and the
  idempotency key and end-to-end id are global (R21, R22). The security review says to deploy
  single-operator only until R21 closes. There is no rate limiting (R23), no separation of operator
  actions and no record of who did what (R24), and hostile input is only partly bounded (R25).
- **The partner webhook trusts a bearer token**, not a partner signature (R13).
- **No broker has carried an event** (R5); the outbox contract is proved in process only.
- **The load numbers are in memory, on one instance, anonymous** (R15). The repeat after the F-9
  fix passed every threshold (`load.yml` run 38446330396), but says nothing about PostgreSQL, the
  authenticated path or more than one instance.
- **The sweep is proved in process, and its alert is prose** (R17). **The computed value date is not
  stamped on the payment** (R11). **A return day cannot reconcile** (R18). **A concurrent duplicate
  initiate answers 409, not the original 201** (R19). **The integration fixture's Docker fallback and
  concurrent migration are untested** (R12).
- **Sanctions, AML and limits are absent** (R10), by scope.

## 16. The register

[`REGISTER.md`](REGISTER.md) on `main` has eighteen open rows and nine closed, each with a date and,
for the closed ones, the tests or runs that prove it. Open rows, by headline:

| # | Not proved |
|---|---|
| R1 | Nothing here has ever talked to a real bank. |
| R5 | No broker has ever carried an event. |
| R10 | Sanctions, AML, limits: absent. |
| R11 | The computed value date is not stamped on the payment. |
| R12 | The Testcontainers path of the integration fixture has not run, and concurrent migration is untested. |
| R13 | The partner webhook is authenticated with a bearer token and a grant, not a partner signature. |
| R14 | Nothing has been deployed. |
| R15 | The load numbers are in memory, on one machine, anonymous. |
| R17 | The sweep is proved in process, and its alert is prose. |
| R18 | A return has no statement line of its own. |
| R19 | A concurrent duplicate initiate returns 409, not the original 201 body. |
| R20 | The ISO 20022 samples are ours, not a bank's. |
| R21 | A payment has no owner, and the idempotency key is global. |
| R22 | The end-to-end id is global: another caller's id answers 409, and the message is wrong for it. |
| R23 | There is no rate limiting. |
| R24 | Operator-only actions are not separated, and nothing records who did what. |
| R25 | Hostile input and a hostile partner are only partly handled. |
| R26 | The deployment's security settings are unverified, and most live outside the tree. |

Closed, with proof in the register: R2 (ISO 20022), R3 (scenarios execute), R4 (PostgreSQL in CI),
R6 (concurrency), R7 (calendars), R8 (the resolver is scheduled), R9 (load and chaos measured), R16
(`resolve` during an open circuit) and R27 (an open circuit is not retried). R10 is out of scope for
the bench and stays open as a statement of scope.

## 17. Architecture decision records

| ADR | Decision |
|---|---|
| [0001](adr/0001-no-blind-resubmit.md) | An unknown submit outcome is never retried blind; the resolver asks the partner and resubmits only on `NotFound`. |
| [0002](adr/0002-double-entry-ledger.md) | Money is a double-entry, append-only ledger; balances are derived. |
| [0003](adr/0003-reconciliation-produces-a-work-queue.md) | Reconciliation produces breaks, not alerts. |
| [0004](adr/0004-the-overview-pdf-is-built-on-demand.md) | The overview PDF is built on demand, on pull requests touching its sources, and on tags; never committed. |
| [0005](adr/0005-the-documentation-site-is-generated.md) | The documentation site is generated from one source and one facts file, and checked for staleness in CI. |
| [0006](adr/0006-the-site-loads-no-external-resource.md) | The site loads no external resource and works from `file://`. |
| [0007](adr/0007-business-calendars-are-dated-covered-data.md) | Business calendars are dated, covered data; a date outside coverage is an error. |
| [0008](adr/0008-schema-migrated-by-a-hosted-service-behind-a-gate.md) | The schema is migrated by a hosted service behind a gate. |
| [0009](adr/0009-api-validation-and-errors.md) | Built-in validation, one exception handler, one problem body. |
| [0010](adr/0010-the-outbox-contract-and-why-a-broker-is-a-one-class-swap.md) | The outbox contract, and why a broker is a one-class swap. |
| [0011](adr/0011-submitted-payments-are-swept-never-blindly-resubmitted.md) | Submitted payments are swept, never blindly resubmitted. |
| [0012](adr/0012-scenarios-run-in-process-against-the-api-and-the-simulator.md) | Scenarios run in process against the API and the simulator. |
| [0013](adr/0013-optimistic-concurrency-by-remembered-version.md) | Optimistic concurrency by remembered version; a concurrent duplicate initiate is a 409. |
| [0014](adr/0014-iso-20022-at-the-edge-one-internal-model.md) | ISO 20022 at the edge, one internal model. |
| [0015](adr/0015-identity-via-authservice-jwks.md) | Identity via authservice JWKS; anonymous only in Development. |
| [0016](adr/0016-scoped-grants-for-the-webhook-and-back-office-reads.md) | Scoped grants for the partner webhook and the back-office reads; no anonymous mode on Fly; a request-size cap. |

## 18. Non-goals

- **Being a payments product.** It is a bench; its value is the mechanisms and the evidence about
  them.
- **Talking to a real bank.** The partner is simulated, and every document says so.
- **Compliance controls.** Sanctions screening, AML and limits are out of scope (R10).
- **A real broker or a real bank connection.** The seams exist (`IEventPublisher`,
  `IPartnerGateway`); what plugs into them is outside the bench (R1, R5).
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

The site is a single generated page, `docs/index.html`, built by `scripts/site-build.mjs` from
`docs/site/source.html`, `docs/site/style.css` and `docs/site/status.json`, plus facts read from the
register, the scenarios, the ADRs and the findings. It loads no external resource and opens from
`file://`. Regenerate it with `npm run site:build`; `npm run site:check` fails CI when the committed
page is stale (ADR 0005, ADR 0006).

It is published by `.github/workflows/deploy-pages.yml`, which uploads `docs/` to Pages on a
push to `main` that touches `docs/`, and on demand. It needs one step by hand that no API can
perform: **Settings > Pages > Build and deployment > Source = GitHub Actions**. Until that is set,
the workflow fails (`configure-pages` with "Get Pages site failed", then the deploy job with a
"Pages not enabled" error). [`DEPLOYMENT-DOCS.md`](DEPLOYMENT-DOCS.md)
has the detail.
