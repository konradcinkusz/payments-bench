# ADR 0014: Scoped grants for the partner webhook and the back-office reads; no anonymous mode on Fly; a request-size cap

**Status:** accepted, 2026-10-13; amends ADR 0008 (decision 2: `/partner/notifications` no longer falls under the fallback policy)

## Context

ADR 0008 made every endpoint require an authenticated user and left the rest to ownership checks
that do not exist yet. The security review ([`docs/security/SECURITY-REVIEW.md`](../security/SECURITY-REVIEW.md))
found that "authenticated" is not enough for three groups of endpoints:

- `POST /partner/notifications` moves money between ledger accounts on the sender's word. With
  only a token required, any caller could post `Rejected` for a payment the partner had accepted
  and release the customer's reserved funds (SEC-02).
- `GET /ledger/balances/{account}/{currency}`, `GET /payments/{id}/ledger` and
  `POST /reconciliation/runs/{date}` are back-office functions: aggregate balances, any payment's
  journal, and a partner round trip plus a read of every settled payment (SEC-04).
- The Development anonymous policy is separated from a deployment only by an environment name
  (SEC-06), and Kestrel accepts a 30 MB body where the largest legitimate one is a few hundred
  bytes (SEC-07).

## Decision

1. **Two named policies, each needing a grant on the token.** `partner-notifications` requires
   `payments.partner.notify`; `operations` requires `payments.operate`. Both also require an
   authenticated user. A grant is a `role` or `roles` claim value, or one value of the
   space-separated `scope` or `scp` claim: the token's shape is the identity provider's choice and
   cannot be read from this repository, so both shapes are accepted. Claim names are as issued
   (inbound claim mapping is off, ADR 0008).
1. **The names are configuration.** `Auth:Grants:PartnerNotifications` and `Auth:Grants:Operations`
   rename the grants without a code change; unset or blank means the defaults above.
1. **Applied as groups in `UsePaymentsApi`**, around the endpoint maps, not as attributes on the
   routes. The endpoint files stay free of identity concerns, and the table of what needs what is
   three lines in one place.
1. **Fail closed.** A token without the grant is answered 403 (a missing token is still 401). If the
   identity provider never mints the grants, those endpoints answer 403 to everyone, which is the
   safe way to be wrong.
1. **Development anonymous mode** is unchanged except that both policies are satisfied by anyone
   there, because with no authority there is no authentication scheme to challenge. It additionally
   refuses to start when `FLY_APP_NAME` is set: Fly sets it on every machine, so a machine on Fly is
   never a developer's laptop, whatever `ASPNETCORE_ENVIRONMENT` says.
1. **Request bodies are capped at 64 KiB** for every service that uses ServiceDefaults
   (`Limits:MaxRequestBodyBytes` changes it; an unusable value falls back to the default rather than
   lifting the cap), and the `Server` response header is dropped.

## Alternatives considered

- **`[Authorize(Roles = ...)]` or `.RequireAuthorization(...)` on each route.** Reads best in
  isolation, but edits stream A's endpoint files for an identity concern and spreads the table
  across four files.
- **A separate listener or port for the back-office endpoints.** A network control rather than an
  identity one, heavier to operate on Fly, and it would not help the webhook, which the partner must
  reach.
- **A partner signature or mTLS for the webhook.** The right end state, and still open as R13; it
  needs a partner-held key and a design for rotation. The grant is what can be done today.

## Consequences

- authservice must mint the two grants for the identities that need them (the partner's integration,
  the operator). Until it does, the four endpoints answer 403; the simulated partner never calls the
  webhook itself, and the load script runs under the Development policy, so nothing in this
  repository breaks.
- Ownership (SEC-01, R21) is still absent: ordinary payment routes remain open to any authenticated
  caller. When it lands, the `operations` grant is the natural bypass for the owner check.
- R13 stays open: a bearer token with the grant proves the sender is a client of authservice with
  that grant, not that the bank sent the body.
- The Fly guard protects Fly only. Another host has no marker; the review lists it as residual and
  proposes an explicit `Auth__AllowAnonymous` opt-in as the structural fix.
