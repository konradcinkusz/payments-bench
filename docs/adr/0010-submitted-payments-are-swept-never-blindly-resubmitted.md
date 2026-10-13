# ADR 0010: Submitted payments are swept, never blindly resubmitted

**Status:** accepted, 2026-10-13

## Context

[ADR 0001](0001-no-blind-resubmit.md) leaves a payment at `Submitted` after an unknown submit
outcome and recovers it with `ResolveSubmittedPaymentHandler`: ask the simulated partner by
end-to-end id, resubmit only when it says `NotFound`. Nothing called that handler on its own, so a
crash or a timeout left the amount in `pending-outbound` until a person noticed (register row R8).
The recovery has to run by itself, and running it by itself must not weaken the rule that made it safe.

## Decision

1. **The sweep reuses the resolver and adds no second path to the partner.**
   `SweepSubmittedPaymentsHandler` (Application) selects Submitted payments whose `UpdatedAt` is
   older than `MinimumAge`, oldest first, at most `BatchSize`, and passes each id to
   `ISubmittedPaymentResolver`. The infrastructure implementation opens a DI scope per payment and
   runs `ResolveSubmittedPaymentHandler` in it. The sweep never calls the partner and never
   resubmits itself, so ADR 0001's rule holds by construction: a transfer is resubmitted only when
   the partner reports no record, with the same end-to-end id.
1. **One payment's failure does not stop the batch.** A partner exception, a concurrency conflict
   because someone resolved the payment by hand, anything else: logged with the payment id, counted
   under `outcome="failed"`, and the payment stays Submitted for the next sweep. Cancellation is not
   a failure; it stops the sweep.
1. **One batch per sweep, not a drain loop.** A payment the partner cannot answer for stays
   Submitted and would be selected again immediately; draining until empty could spin on it. The
   next sweep is one `Interval` away.
1. **`StillUnknown` is its own outcome (fixes F-3).** When the partner has no record, the resolver
   resubmits, and that resubmit's own outcome is unknown, the resolver now returns
   `ResolutionOutcome.StillUnknown`, not `SafeToResubmit`. Nothing is written; the next sweep asks
   again. The sweep counts it under that tag instead of mistaking it for a resolution.
1. **`PartnerUnavailable` is its own outcome (the resolve-during-open-circuit defect, stream B R16).**
   Failed submits trip the circuit breaker that `HttpPartnerGateway.GetStatusAsync` shares, and the
   lookup did not catch `BrokenCircuitException` (or a timeout or other transport failure), so
   `POST /payments/{id}/resolve` returned 500 and the sweeper would have hit the same exception.
   The gateway now maps an open circuit, timeouts, transport failures and 5xx answers to
   `PartnerStatus.Unavailable`, never to `NotFound`, because "no record" is the one answer that
   permits a resubmit. The resolver returns `ResolutionOutcome.PartnerUnavailable`, writes nothing and
   leaves the payment Submitted. The API answers **200 with that outcome**, like `StillUnknown`, not a 503:
   the request itself succeeded and the payment is in a defined, unchanged state; a 5xx would read
   as "this service is broken" and invite blind client retries against an open breaker, whereas
   the outcome name tells the caller to ask again later. The sweep counts it under that tag.
1. **`SubmittedPaymentSweeper`** is a `BackgroundService` in `Infrastructure/Sweeping/`. It awaits
   `MigrationGate` ([ADR 0008](0008-schema-migrated-by-a-hosted-service-behind-a-gate.md)), sweeps
   once at start and then every `Interval`, and is registered for both storage modes. Options are
   bound from the `Sweeper` section, so `Sweeper__MinimumAge=00:02:00` works from the environment,
   and validated on start:

   | Key | Default | Rule |
   |---|---|---|
   | `Sweeper:Enabled` | `true` | |
   | `Sweeper:Interval` | 30 s | positive |
   | `Sweeper:MinimumAge` | 60 s | positive |
   | `Sweeper:BatchSize` | 50 | 1 to 500 |

   The minimum age keeps the sweep off a submit that is still in flight; the sweep reads time from
   `IClock`.
1. **Telemetry** on the `PaymentsBench` meter (taken from `IMeterFactory`, so there is one meter
   per name) and the `PaymentsBench` activity source (an `Activity` per sweep, a log line per
   non-empty sweep). These names are used by the stream B dashboard:
   - `payments.submitted.oldest_age_seconds`, an observable gauge in seconds: the age of the oldest
     payment at Submitted as found by the last sweep, 0 when there is none. The sweep finds the
     payment; the age is read live, so a sweeper that stops makes the gauge climb instead of
     freezing.
   - `payments.sweep.resolved`, a counter with tag `outcome`: the `ResolutionOutcome` name
     (`Accepted`, `Settled`, `Rejected`, `StillUnknown`, `PartnerUnavailable`, `AlreadyResolved`) or `failed`.
1. **The alert.** Page when `payments.submitted.oldest_age_seconds` is above
   `10 x Sweeper:Interval + Sweeper:MinimumAge` for 5 minutes. With the defaults that is
   10 x 30 s + 60 s = 360 s. Why these numbers:
   - A healthy sweeper clears a payment within `MinimumAge + Interval` of its last update, so the
     healthy ceiling of the gauge is about 90 s. The threshold is `MinimumAge` plus ten intervals:
     it tolerates nine failed or empty-handed sweeps (a partner blip, a slow database, a backlog
     larger than one batch draining at `BatchSize` per interval) before anyone is woken.
   - The 5-minute hold keeps a single slow partner answer, or a deploy that restarts the sweeper,
     from paging. A payment that really is stuck stays above the line and pages within minutes.
   - `payments.sweep.resolved{outcome="failed"}` or `{outcome="StillUnknown"}` or `{outcome="PartnerUnavailable"}` rising while the gauge
     is high means the sweeper runs but the partner does not answer; a flat counter and a high
     gauge means the sweeper is not running (check `Sweeper:Enabled` and the migration gate).
   - If an operator changes `Interval` or `MinimumAge`, the threshold changes with them; it is
     derived, not a constant.

## Consequences

- R8 closes: payments left at Submitted resolve without a person, and the age of the oldest one is
  observable. The rule in ADR 0001 is unchanged and now covered by a test that drives the sweep.
- Payments are taken oldest first. A payment that stays `StillUnknown`, `PartnerUnavailable` or failing keeps its
  `UpdatedAt`, stays at the front, and is retried every sweep. If more than `BatchSize` payments are
  stuck this way they starve younger ones. That is visible on the alert, and it is open as a
  register row.
- Several API instances each run a sweeper. Two sweepers may pick the same payment; the second to
  commit hits the `Version` concurrency token and is counted as `failed`. A lease or advisory lock
  would remove the noise; it is not built.
- The alert rule is documented here and not deployed: no alerting stack exists in this repository.
- Resolving needs the simulated partner to answer status lookups. Against a partner that cannot,
  every sweep counts failures and the alert fires; that is the intended signal.
