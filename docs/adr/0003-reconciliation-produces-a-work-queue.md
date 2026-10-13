# ADR 0003: Reconciliation produces breaks, not alerts

**Status:** accepted, 2026-10-13

## Context

A statement that disagrees with the ledger is the normal case on some days: a late booking, a
partner's fee, a return that arrived after the cut. An alert that says "reconciliation failed"
tells the person on call nothing they can act on.

## Decision

`ReconciliationService` returns a report: the number of matched lines and a list of breaks, each
with a kind (`OnStatementOnly`, `InLedgerOnly`, `AmountMismatch`), the end-to-end id, and both
amounts where both exist. The report is the output; what to alert on (the count, the age of the
oldest unworked break) is a monitoring decision made outside the service.

## Consequences

- A clean day is `IsClean == true`, and a dirty day is a list someone can work top to bottom.
- Matching is by end-to-end id and exact amount. Tolerances, fees and multi-line bookings are
  not handled and would each be a new break kind, not a fuzzier match.
