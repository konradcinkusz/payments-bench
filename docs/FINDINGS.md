# Findings

What building and testing this bench actually caught. Written to be unflattering: a finding is
recorded with what went wrong, how it was found, the evidence a reader can check in this
repository, and whether it is fixed. A finding that was fixed stays here; the fix is part of the
record, not a reason to delete it.

**Five findings so far. None of them was caught by a unit test.** Two were caught by walking a
transfer through the running API and the partner simulator over HTTP, the README's walk-through;
three were caught by reading the code closely enough to describe it in
[`OVERVIEW.md`](OVERVIEW.md). The 37 tests on `main` (15 in `tests/PaymentsBench.Domain.Tests`,
22 in `tests/PaymentsBench.Application.Tests`, counted by `dotnet test` on 2026-10-13) run the
handlers against in-memory stores and a scripted partner (`tests/PaymentsBench.Application.Tests/Fakes.cs`),
so a defect that lives in serialisation, in the HTTP resilience pipeline, or in an assumption
both the code and its tests share is invisible to them by construction.

| # | Defect | Found by | Severity | Status |
|---|---|---|---|---|
| F-1 | Enums crossed the wire as integers; every partner notification and statement line failed to bind | HTTP walk-through | High | Fixed |
| F-2 | A timeout after the partner accepted surfaced as a 500, not as an Unknown outcome | HTTP walk-through | High | Fixed |
| F-3 | The resolver reports `SafeToResubmit` after a resubmit whose own outcome is unknown | Writing `OVERVIEW.md` | Low | Open |
| F-4 | A payment settled and later returned shows up as an `OnStatementOnly` break for its settlement day | Writing `OVERVIEW.md` | Medium | Open |
| F-5 | The status lookup's retry predicate never matches an exception, so a `GET` that times out is not retried | Writing `OVERVIEW.md` | Low | Open |

Streams that add work to this repository add their findings here, through the "Findings" section
of their plan file under [`plans/`](plans/).

## F-1. Enums crossed the wire as integers

**What happened.** `System.Text.Json` treats an enum as its integer unless told otherwise: it
writes numbers and refuses to read names. The wire format in this repository uses names: a
notification's `kind` is `"Settled"`, a statement line's `direction` is `"Debit"`, a fault profile's
`kind` is `"TimeoutAfterAccept"`. Without a converter, every partner notification posted to the API
and every statement line the gateway read from the simulated partner failed to bind to
`NotificationKind` and `StatementDirection`. The handlers were correct; nothing reached them.

**How it was found.** The README's walk-through: initiate, submit, settle at the simulator, post a
notification, run reconciliation, all over HTTP against the two running services. The first
notification failed. The unit tests construct `PartnerNotification` and `StatementLine` as C#
objects and never serialise them, so they could not have seen it.

**Evidence.** The fix is one converter in three places, and all three are needed because the
three serialisers are configured independently:

- `src/PaymentsBench.Api/Program.cs`: `ConfigureHttpJsonOptions(... new JsonStringEnumConverter())`
  for what the API reads and writes.
- `src/PaymentsBench.PartnerSimulator/Program.cs`: the same, for what the simulated partner
  reads and writes.
- `src/PaymentsBench.Infrastructure/Partner/PartnerJson.cs`: `options.Converters.Add(new JsonStringEnumConverter())`
  in the `JsonSerializerOptions` the gateway uses for its own calls to the partner.

**What still guards it.** Nothing automated. No test serialises a notification or a statement
across a process boundary. The guard this repository is waiting for is the scenario runner
(register row R3 in [`REGISTER.md`](REGISTER.md)), which would drive the files in
[`../scenarios/`](../scenarios/) against the running API and simulator.

**Lesson.** A contract between two processes is not proved by tests that never leave one
process.

## F-2. A timeout after acceptance surfaced as a 500

**What happened.** With the simulator's `TimeoutAfterAccept` fault, the partner records the
transfer and holds the response. The gateway's HTTP client carries the standard resilience
handler, whose per-attempt timeout (`options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(4)` in
`src/PaymentsBench.Infrastructure/InfrastructureRegistration.cs`) fires before the client's own
5-second timeout and throws Polly's `TimeoutRejectedException`, not the `TaskCanceledException`
the gateway's catch was written for. The exception escaped `HttpPartnerGateway.SubmitAsync`, and
`POST /payments/{id}/submit` returned a 500 instead of a payment at `Submitted` with an unknown
outcome.

**How it was found.** The README's walk-through, the timeout-after-accept step. The unit tests use
a scripted partner (`ScriptedPartner` in `tests/PaymentsBench.Application.Tests/Fakes.cs`) that
returns `SubmitOutcome.Unknown` directly, so the exception that real HTTP produced never existed in
a test.

**Why no money was lost.** Rule 1 in [`ARCHITECTURE.md`](ARCHITECTURE.md): `SubmitPaymentHandler`
commits `Submitted` before it calls the partner. The 500 happened after that commit, so the payment
was at `Submitted` in the store, which is exactly the state the resolver is written for.
`POST /payments/{id}/resolve` asked the simulated partner by end-to-end id, was told `Accepted`, and
moved the payment to `Accepted` with no second transfer. The defect was in the error reporting; the
design held. That is the property ADR [0001](adr/0001-no-blind-resubmit.md) exists to buy.

**Evidence.** The catch in `src/PaymentsBench.Infrastructure/Partner/HttpPartnerGateway.cs` now
names `HttpRequestException or TaskCanceledException or TimeoutRejectedException or BrokenCircuitException`
and maps every one of them to `SubmitOutcome.Unknown`.

**What still guards it.** Nothing automated, for the same reason as F-1: no test sends a submit
through the real `HttpClient` and its resilience pipeline. Register row R3 is the closing condition
here too; the scenario `scenarios/timeout-after-accept.yaml` already specifies the expected end
state (`status: Accepted`, `partnerSubmits: 1`).

**Lesson.** A resilience library adds its own exception types. A catch written against the
framework's exceptions is a catch written against half the failures.

## F-3. `SafeToResubmit` is returned after a resubmit whose outcome is unknown

**What happened.** In `ResolveSubmittedPaymentHandler` (`src/PaymentsBench.Application/Payments/ResolveSubmittedPayment.cs`),
when the partner answers `NotFound` the handler resubmits once with the same end-to-end id. If that
resubmit is accepted or rejected, the outcome is applied. If the resubmit's own outcome is unknown,
the handler returns `ResolutionOutcome.SafeToResubmit` and changes nothing.

**Why it is a defect, and why it is a small one.** The behaviour is safe: the payment stays at
`Submitted`, and the next resolve asks the partner again before doing anything, so no blind retry
can follow from it. The name is the problem. The enum's own comment says the outcome means "the
partner has no record of the transfer, so the submit never landed", and at the moment it is returned
that is no longer known: a resubmit has just gone out and may have landed. An operator, or a future
sweep (register row R8), that takes the outcome at its word would be told the opposite of the
truth.

**How it was found.** Reading the handler line by line to describe the resolver in
[`OVERVIEW.md`](OVERVIEW.md). No test covers the branch: `SubmitAndResolveTests` resolve a
`NotFound` whose resubmit is accepted, not one whose resubmit is unknown.

**Status.** Open. The fix is in the core, outside the documentation stream: a distinct outcome
(for example "resubmitted, outcome unknown") and a test for the branch.

## F-4. A returned payment is a break on its settlement day

**What happened.** `ReconciliationService.ReconcileAsync` (`src/PaymentsBench.Application/Reconciliation/ReconciliationService.cs`)
matches a day's statement against payments whose status is `Settled` and whose value date is that
day. A payment that settled on that day and was returned later has status `Returned`, so it is no
longer in that set. The settlement debit is still on that day's statement; the simulated partner
keeps returned transfers on the statement for their value date
(`src/PaymentsBench.PartnerSimulator/Program.cs`, the `/statements/{date}` endpoint filters
`t.Status is "Settled" or "Returned"`). Reconciliation then reports the line as `OnStatementOnly`:
money the ledger supposedly does not know about, for a payment the ledger knows very well.

**Evidence.** A throwaway probe on 2026-10-13, built from the same `TestHost` the reconciliation
tests use: initiate, submit (accepted), notify `Settled` with value date 2026-10-14, notify
`Returned`, reconcile 2026-10-14 against a statement carrying the one debit. Result:
`matched=0 breaks=OnStatementOnly:E2E-1`. The probe was not committed; the steps above reproduce it.

**Why it matters.** A reconciliation that raises false breaks teaches the person working the queue
to ignore it, which is the failure ADR [0003](adr/0003-reconciliation-produces-a-work-queue.md)
was written to prevent. The statement also carries no credit line for the return itself, so the
return day cannot reconcile either; register row R2 (no camt.053) is related but does not cover
this.

**How it was found.** Reading the service and the simulator side by side while writing the
reconciliation section of [`OVERVIEW.md`](OVERVIEW.md). The three reconciliation tests settle
payments and never return one.

**Status.** Open. The fix is in the core: match on "settled on that value date", whatever happened
afterwards, and model the return as its own statement line.

## F-5. The status lookup is not retried on an exception

**What happened.** The partner client's resilience handler is configured to retry idempotent
lookups and nothing else (`src/PaymentsBench.Infrastructure/InfrastructureRegistration.cs`):

```csharp
options.Retry.ShouldHandle = args => ValueTask.FromResult(
    args.Outcome.Result?.RequestMessage?.Method == HttpMethod.Get
    && (args.Outcome.Exception is not null || (int?)args.Outcome.Result?.StatusCode >= 500));
```

When the attempt ends in an exception (a timeout, a refused connection), `args.Outcome.Result` is
null, so the first clause compares null with `HttpMethod.Get` and is false. The
`Exception is not null` branch can never make the predicate true. A `GET` is retried on a 5xx
response, and never on the failures a retry exists for.

**Why it is low severity.** The predicate errs on the safe side: it can never retry a submit `POST`,
which is the property rule 2 needs. The cost falls on the resolver: a lookup that times out once
fails the resolve call instead of being retried, and someone has to call resolve again (or, once it
exists, the sweep of register row R8 will).

**How it was found.** Checking the sentence "the resilience pipeline retries `GET` only" against the
code while writing section 7 of [`OVERVIEW.md`](OVERVIEW.md). No test exercises the HTTP client's
pipeline.

**Status.** Open. The fix is in the core: decide on the request's method from the request itself,
not from a response that does not exist when the attempt threw, and test both branches.
