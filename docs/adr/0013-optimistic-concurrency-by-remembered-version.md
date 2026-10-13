# ADR 0013: Optimistic concurrency by remembered version; a concurrent duplicate initiate is a 409

**Status:** accepted, 2026-10-13

## Context

`PaymentRow.Version` was a concurrency token, but `EfPaymentRepository.UpdateAsync` re-read the
row at update time and used *that* version as the original value. A handler that read a payment
and then lost a race therefore overwrote the winner instead of failing. Separately, two requests
initiating with one idempotency key can both pass the "does it exist" check; the unique index
catches the second at commit, as a raw `DbUpdateException`.

## Decision

- The repository remembers, per scope, the version of every payment it hands out. `UpdateAsync`
  checks that version and writes version + 1; the remembered version advances after a successful
  commit, so a handler may update one payment twice in a scope. Updating a payment never read in
  the scope is an `InvalidOperationException` (a programming error). The in-memory repository
  hands out copies and follows the same rule, so the degraded mode behaves like PostgreSQL (P4).
- `ConcurrencyConflictException` (Application/Ports, a `DomainException`) is the one type callers
  see. `EfUnitOfWork.CommitAsync` translates `DbUpdateConcurrencyException`, and a unique violation
  (`23505`) on the idempotency-key or end-to-end-id index, into it and clears the change tracker.
  No EF or Npgsql type crosses into Application or Api (P11). The API maps it to 409 ahead of the
  generic `DomainException` arm, which would answer 422.
- **A concurrent duplicate initiate answers 409, not the stored 201 body.** The loser of the race
  cannot reliably tell, inside a failed scope, whether the winner stored the same request body, and
  replaying a response from a half-failed scope would need a second read path in the unit of work.
  The client re-reads or retries the same request, and the retry takes the normal replay path. This
  trades one extra round trip on a rare race for a simple, uniform rule. Recorded as register row R19.

## Consequences

Callers of a write must be ready for 409 and re-read. A blind retry of a stale write is refused,
which is the point. A true replay (the key already stored) is unchanged: it still returns the
original payment.
