# Architecture

## The shape

Four projects carry the logic, in strict dependency order: `Domain` knows nothing, `Application`
knows the domain and a set of ports, `Infrastructure` implements the ports, `Api` composes them.
`ServiceDefaults` is the shared kernel (telemetry, health, discovery) and by rule contains no
domain type. `AppHost` declares the system: one PostgreSQL, the partner simulator, the API.

## A payment's life

```text
POST /payments            Initiated      ledger: customer-funds → pending-outbound     outbox: PaymentInitiated
POST /payments/{id}/submit
   status committed first  Submitted                                                    outbox: PaymentSubmitted
   partner says accepted   Accepted      (partner reference stored)                     outbox: PaymentAccepted
   partner says rejected   Rejected      ledger: pending-outbound → customer-funds      outbox: PaymentRejected
   call did not complete   Submitted     nothing - the resolver takes it from here
POST /payments/{id}/resolve
   partner: not found      resubmit once, same end-to-end id
   partner: accepted       Accepted
   partner: settled        Settled       ledger: pending-outbound → nostro
   partner: rejected       Rejected      ledger: pending-outbound → customer-funds
POST /partner/notifications (deduplicated by notification id)
   settled                 Settled       ledger: pending-outbound → nostro               outbox: PaymentSettled
   rejected                Rejected      ledger: pending-outbound → customer-funds
   returned                Returned      ledger: nostro → customer-funds                  outbox: PaymentReturned
POST /reconciliation/runs/{date}
   statement vs settled payments with that value date → matched + breaks
```

## The rules that matter

1. **Submitted is committed before the partner call.** A crash between the commit and the
   response leaves a payment at Submitted, which is the state that means "ask before you act".
1. **An unknown outcome is never retried.** Timeouts, dropped connections and 5xx after the body
   was sent all map to `SubmitOutcome.Unknown`. The HTTP resilience pipeline retries `GET` only.
1. **Resubmission needs the partner's word.** Only `PartnerStatus.NotFound` allows a second
   submit, and it carries the same end-to-end id so a late-arriving first attempt is a duplicate
   the partner can detect.
1. **The ledger is append-only and balanced.** A `JournalEntry` that does not balance per
   currency cannot be constructed. Balances are sums, never columns.
1. **Events go through the outbox, notifications through the inbox.** Publishing is at least
   once; consumers dedupe on the message id.
1. **Reconciliation produces breaks, not alerts.** Three kinds, each with the two amounts, so the
   person working the queue has what they need on the row.
1. **Scheme rules are data.** Cut-off, currency and settlement days per scheme live in
   `scenarios/schemes.yaml`; code reads them.

## Persistence

`PaymentsDbContext` stores rows, not aggregates. The repository rebuilds a `Payment` by replaying
the moves its stored status implies, so the domain's invariants hold on the way out of the
database as well as on the way in. `Version` is a concurrency token; two writers racing on one
payment lose one of them cleanly.

Without a `payments` connection string every store is in memory and the log says so at start-up.
That is the degraded mode, and it is the mode the tests use.

## Observability

OpenTelemetry via `ServiceDefaults`: ASP.NET Core and HttpClient instrumentation, the
`PaymentsBench` activity source and meter, the `payments.transitions` counter tagged by status.
OTLP export switches on with `OTEL_EXPORTER_OTLP_ENDPOINT`. The Aspire dashboard shows all of it
with no configuration.
