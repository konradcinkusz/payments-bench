# Findings

What building and testing this bench actually caught. Written to be unflattering: a finding is
recorded with what went wrong, how it was found, the evidence a reader can check in this
repository, and whether it is fixed. A finding that was fixed stays here; the fix is part of the
record, not a reason to delete it.

**Eighteen findings. None was caught by a unit test failing on its own.** They were caught by
walking a transfer through the running API and the simulated partner over HTTP (F-1, F-2), by
reading the code closely enough to describe it in [`OVERVIEW.md`](OVERVIEW.md) (F-3 to F-5) or to
test it (F-6), by k6 load runs (F-7, F-9, F-15), by a scenario that should have failed and did not
(F-8), by CI jobs doing something other than testing (F-10, F-11, F-16), by a flaky run (F-12), by
review and a security review (F-13, F-14), and by building the delivery path and merging the three
streams of [`plans/`](plans/) (F-17, F-18). Thirteen are fixed, each with the pull request that fixed
it; five are open, and each open one names its register row in [`REGISTER.md`](REGISTER.md) or says
why it has none.

The pattern from the first five held for the rest. The unit tests, now 134 of the 365 on `main`
(30 in `tests/PaymentsBench.Domain.Tests`, 104 in `tests/PaymentsBench.Application.Tests`, counted by
`dotnet test` against PostgreSQL on 2026-10-13 at `579e135`), run the handlers against in-memory
stores and a scripted partner. A defect in serialisation, in the HTTP resilience pipeline, in an
assumption the code and its tests share, or in the test harness itself is invisible to them by
construction. The integration, API and scenario suites that closed register rows R3, R4 and R6
exist because of that.

| # | Defect | Found by | Severity | Status |
|---|---|---|---|---|
| F-1 | Enums crossed the wire as integers; every partner notification and statement line failed to bind | HTTP walk-through | High | Fixed |
| F-2 | A timeout after the partner accepted surfaced as a 500, not as an Unknown outcome | HTTP walk-through | High | Fixed |
| F-3 | The resolver reported `SafeToResubmit` after a resubmit whose own outcome was unknown | Writing `OVERVIEW.md` | Low | Fixed (#21) |
| F-4 | A payment settled and later returned showed up as an `OnStatementOnly` break for its settlement day | Writing `OVERVIEW.md` | Medium | Fixed (#23) |
| F-5 | The status lookup's retry predicate never matched an exception, so a `GET` that timed out was not retried | Writing `OVERVIEW.md` | Low | Fixed (#23) |
| F-6 | A writer that lost a race overwrote the winner: `UpdateAsync` checked the version it re-read, not the one it had read | Reading the code behind R6 | High | Fixed (#22) |
| F-7 | `resolve` answered 500 while the partner circuit was open: the recovery path failed exactly when recovery was needed | k6 load run | High | Fixed (#21) |
| F-8 | A timeout scenario passed for the wrong reason: the runner's partner timeouts were set too late to take effect | A scenario that should have failed | High | Fixed (#23) |
| F-9 | The F-5 fix retried an open circuit, so every `resolve` and sweep waited out the backoff during an outage | k6 re-run | Medium | Fixed (#30) |
| F-10 | Container images stopped publishing once scheme data became required: the Dockerfiles copied `src/` only | Containers workflow | Medium | Fixed (#11) |
| F-11 | An empty `Metrics:Prometheus:Enabled` crashed start-up | Starting the service | Medium | Fixed (#9) |
| F-12 | The telemetry test read the exporter once, before the hosting layer had stopped the activity | A flaky CI run | Low | Fixed (#28) |
| F-13 | New register rows were appended below the prose under the table, twice, and fell out of it | Review of #23 and #24 | Low | Fixed, and checked by `site:check` (#27) |
| F-14 | A payment has no owner and the idempotency key is global: any token holder reads, lists and submits any payment | Security review | High | Open (R21; also R22 to R26) |
| F-15 | The simulator cannot count submissions per end-to-end id, so "no duplicate transfer" under load is checked indirectly | k6 load run | Medium | Open (R15) |
| F-16 | The floating SDK image ships newer analyzers than CI's pinned SDK; the images build with warnings allowed | Containers workflow | Low | Open, worked around |
| F-17 | GHCR packages start private and Fly cannot pull a private image; no token can change that | Writing the deploy workflow | Low | Open (R14), documented |
| F-18 | Three streams numbering register rows and ADRs in parallel took the same numbers twice | Merging the streams | Low | Open, no mechanical check |

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

**Status.** Fixed in #21 (stream A, slice A5). A resubmit whose own outcome is unknown now returns
`ResolutionOutcome.StillUnknown` and leaves the payment `Submitted`; `SubmitAndResolveTests` covers
the branch. The sweep counts it under that tag (ADR 0011).

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

**Status.** Fixed in #23 (stream A, slice A6) for the settlement day: a payment settled on a day
and returned since still matches that day's debit. The return day itself does not reconcile,
because the simulated statement carries no credit line for the return; that is register row R18.

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

**Status.** Fixed in #23: the predicate reads the method from the request, not from a response that
does not exist when the attempt threw. The fix then over-reached, which is F-9.

## F-6. A writer that lost a race overwrote the winner

**What happened.** `EfPaymentRepository.UpdateAsync` re-read the row at update time and used that
version as the concurrency token's original value. Two handlers that had both read version 3 would
each find version 3 (or 4) on the re-read, so the second writer passed the check and overwrote the
first. `Version` was a concurrency token that guarded nothing; register row R6 said only that no
test raced two writers.

**How it was found.** Stream A's slice A4, reading the code behind R6 before writing the test that
would close it.

**Status.** Fixed in #22. The repository remembers the version each payment was read at and checks
that one (ADR 0013); `ConcurrencyTests` races two scopes on PostgreSQL and exactly one commits, and
the loser leaves no ledger or outbox rows. R6 is closed.

## F-7. `resolve` answered 500 while the partner circuit was open

**What happened.** Under the simulator's `ServerError` profile the first submits get 503, the
resilience pipeline's circuit breaker opens, and from then on the status lookup that `resolve`
makes threw `BrokenCircuitException`, which nothing handled. The payment was not harmed (it stayed
`Submitted`), but the recovery path failed exactly when recovery was needed.

**How it was found.** Stream B's first k6 run (`k6/payments.js`, the "Load numbers" section of
[`REGISTER.md`](REGISTER.md)): every real failure in the run was `resolve`. Reproduced by hand with 14
payments: the first three resolves answered 200, every later one 500.

**Status.** Fixed in #21. The gateway maps an open circuit, timeouts, transport failures and 5xx on
a lookup to `PartnerStatus.Unavailable`, never `NotFound`; the resolver answers
`PartnerUnavailable` and writes nothing. Register row R16 is closed, with a confirming load run.

## F-8. A timeout scenario passed for the wrong reason

**What happened.** The scenario runner set the API's partner timeouts through
`WebApplicationFactory.ConfigureAppConfiguration`, which applies after `Program.cs` has already read
them while building services. The timeouts silently stayed at their defaults, and a timeout
scenario passed without the timeout it was written to exercise.

**How it was found.** Stream A's slice A6: a scenario that should have failed did not.

**Status.** Fixed in #23: the runner uses `UseSetting`, with the reason in a comment in
`tests/PaymentsBench.Scenarios/ScenarioHost.cs` and in ADR 0012. This is a defect in the measuring
instrument, not in the service: a green suite that tests something other than what its files say.

## F-9. The F-5 fix retried an open circuit

**What happened.** After F-5 the retry predicate retried any `GET` that threw, and
`BrokenCircuitException` is an exception. During a partner outage every `resolve` and every sweep
waited out the retry backoff before answering `PartnerUnavailable`.

**How it was found.** Stream B re-ran `load.yml` after the F-7 fix (run 38442934326 on `main` at
`431b244`): under `ServerError` the API's p95 went from 13 ms to 9601 ms and throughput fell to 4.7
requests/s.

**Status.** Fixed in #30 (stream A, slice A9): `PartnerResilience.ShouldRetry` never retries an open
or isolated circuit or a spent total budget, still retries a transport error, an attempt timeout or
a 5xx on a `GET`, and never a `POST`. Register row R27 is closed. The k6 numbers have not been
re-run since the fix; R15 says so.

**Lesson.** A fix to a retry policy is a change to behaviour under load, and only a load run showed it.

## F-10. Container images stopped publishing when scheme data became required

**What happened.** Slice A3 made the scheme and calendar files under `scenarios/` required at
start-up. The Dockerfiles copied `src/` only, so the published images had no scheme data.

**How it was found.** The containers workflow failed on #11.

**Status.** Fixed in #11: both Dockerfiles copy `scenarios/` (`src/PaymentsBench.Api/Dockerfile`,
`src/PaymentsBench.PartnerSimulator/Dockerfile`).

## F-11. An empty metrics switch crashed start-up

**What happened.** `Metrics:Prometheus:Enabled` was read with `GetValue<bool>`, which throws on an
empty string, so an environment that set the variable to nothing could not start the service.

**Status.** Fixed in #9: `src/PaymentsBench.ServiceDefaults/Extensions.cs` reads it with
`bool.TryParse`, and an empty or unparseable value means off.

## F-12. The telemetry test read the exporter once

**What happened.** A test asserting that health probes are not traced read the in-memory exporter
once, and the test client can see the response before the hosting layer stops the activity.

**Status.** Fixed in #28: the test waits for the span it expects before asserting what is absent.

## F-13. Register rows fell out of the register table

**What happened.** The register's open table is followed by prose (the load numbers). Two slices of
stream A appended a new open row after that prose. GitHub rendered the row as a stray line, and the
site generator dropped it from the page without saying so: the register was silently wrong.

**Status.** Fixed in both pull requests, and made mechanical in #27: `scripts/site-build.mjs` fails
with the file and line when a register row is not inside a table that starts with the register's
header.

## F-14. A payment has no owner

**What happened.** Any authenticated caller can read, list and submit any other caller's payment, and
replay another caller's idempotency key to receive that payment or a 409 that confirms the key
exists. Reproduced against the API with two tokens.

**How it was found.** Stream B's security review, [`security/SECURITY-REVIEW.md`](security/SECURITY-REVIEW.md),
findings SEC-01 and SEC-05. The review's other open items are register rows R22 (global end-to-end
id), R23 (no rate limiting), R24 (no operator separation or audit trail), R25 (paging overflow,
unbounded partner responses) and R26 (deployment settings outside the tree).

**Status.** Open: register row R21. Until it closes, the review says to deploy single-operator only.

## F-15. The simulator cannot count submissions

**What happened.** The simulated partner keys transfers by end-to-end id and answers a repeated
submit with the existing transfer. That is the behaviour ADR 0001 relies on, and it also means the
simulator cannot say how many times a transfer was submitted. Under load, "no duplicate transfer" is
checked through the k6 run's own counters (duplicate payments, simulator mismatches), not where a
duplicate would occur.

**Status.** Open: register row R15, which also records that the load numbers are in memory, on one
instance, anonymous.

## F-16. The floating SDK image ships newer analyzers than CI

**What happened.** Building the images on `mcr.microsoft.com/dotnet/sdk:10.0` raised analyzer
warnings (CA1848, CA1873) on code that is clean under the SDK `global.json` pins.

**Status.** Worked around in #8: the Dockerfiles publish with warnings not treated as errors, and
CI's own build stays the gate. The two builds can disagree; nothing checks that they do not.

## F-17. GHCR packages start private

**What happened.** A package pushed to GHCR is private until someone changes its visibility, and
Fly cannot pull a private image. No workflow token can make that change.

**Status.** Documented in `docs/DEPLOYMENT.md` as a one-time manual step, and guarded by a
pre-deploy manifest check in `deploy.yml` (#18). Nothing has been deployed: register row R14.

## F-18. Parallel streams took the same numbers

**What happened.** Three streams each took "the next free number" for register rows and ADRs on the
same day. Numbers were taken twice; `main` briefly held two ADRs 0008 until #29 moved identity to
0015.

**Status.** Open. Numbers were afterwards reserved by message between the stream leads. A check in
`site:check` for duplicate register and ADR numbers would make it mechanical; it does not exist yet.

The CodeQL notes on the integration tests (an undisposed `CancellationTokenSource`, an unused
assignment), recorded by stream A, are hygiene, not defects, and are left out of the count.
