# ADR 0004: Built-in validation, one exception handler, one problem body

**Status:** accepted, 2026-10-13

## Context

Endpoints mapped exceptions by hand, request bodies were not validated, and a missing id answered
with an empty 404. Callers could not rely on one error shape.

## Decision

- **Validation** uses .NET 10's built-in minimal-API validation (`AddValidation()`) with
  DataAnnotations on request records in `Contracts/`, plus two small custom attributes
  (`KnownValue<TEnum>`, `MaxDecimalPlaces`). It fits, so no hand-written endpoint filter exists.
  Two shapes follow from how the built-in validator works: attributes on a handler parameter are
  not validated, so the `Idempotency-Key` header is bound as a one-property type with
  `[AsParameters]`; and the Application's `PartnerNotification` stays attribute-free, with an
  API-side `PartnerNotificationRequest` that carries the rules and maps to it.
- **Errors**: one `IExceptionHandler` maps `PaymentNotFoundException` to 404, `IdempotencyConflictException`
  and `InvalidTransitionException` to 409, other `DomainException`s to 422, and anything else to a
  500 with a fixed text. `AddProblemDetails()` and `UseStatusCodePages()` make routing 404s and
  validation 400s `application/problem+json` too. The endpoints contain no try/catch.
- **Test entry point**: `PaymentsBench.Api.ApiEntryPoint`, a public marker type, rather than
  `public partial class Program`. The partner simulator also has a top-level `Program`, and a later
  slice hosts both in one test project.
- **List aggregates**: `IPaymentRepository.ListAsync` returns the page plus total and per-status counts
  over the filtered set (the status filter included). The EF version computes counts with one
  grouped query, then fetches the page; the total is the sum of the groups.

## Consequences

- A new rule is an attribute on a record, not a branch in an endpoint.
- A new exception type that should not be a 500 is one more line in the handler's switch.
- `countsByStatus` under a status filter shows zeros for the other statuses; a facet that ignores
  the filter would be a second query and is not offered.
