# ADR 0001: An unknown submit outcome is never retried blind

**Status:** accepted, 2026-10-13

## Context

A submit to the partner can fail in three ways that look alike from the caller's side: the
connection drops before the request lands, the request lands and the response is lost, or the
partner returns a 5xx after recording the transfer. A naive retry creates a second transfer in
two of the three.

## Decision

`HttpPartnerGateway` maps every failure after the request may have left the process to
`SubmitOutcome.Unknown`. `SubmitPaymentHandler` leaves the payment at `Submitted` on that
outcome and returns. `ResolveSubmittedPaymentHandler` asks the partner for the transfer by
end-to-end id and resubmits only on `PartnerStatus.NotFound`, with the same end-to-end id. The
HTTP resilience pipeline is configured to retry `GET` only.

## Consequences

- A payment can sit at `Submitted` until something calls resolve. That is deliberate and is
  register row R8 until a sweep exists.
- The partner must support lookup by end-to-end id. A partner that does not forces a different
  design (a pre-registered reference, or a reconciliation-only recovery) and that would be a new
  ADR.
- The status before the call is committed, so a crash mid-call is visible as a Submitted
  payment, not as a missing one.
