# ADR 0007: Identity via authservice JWKS; anonymous only in Development

**Status:** accepted, 2026-10-13

## Context

The API moves money. Until now every endpoint was open. It needs callers to be authenticated, and
the reference architecture (P5) asks for JWT / OIDC with asymmetric signing: exactly one service
holds a signing key, every other service validates against that service's JWKS endpoint, so being
able to verify a token is not the same capability as being able to mint one.

`authservice` (`ghcr.io/konradcinkusz/authservice`, pinned at `v0.3.4`) is the identity service for
this estate. It signs RS256 and publishes `/.well-known/openid-configuration` and a JWKS.

## Decision

1. **The API holds no key.** `AddPaymentsIdentity` configures JwtBearer with
   `MetadataAddress = {Auth:Authority}/.well-known/openid-configuration`, `ValidIssuer` from
   `Auth:Issuer`, `ValidAudience` from `Auth:Audience`, `ValidateIssuerSigningKey = true` and
   `ValidAlgorithms = [RS256]`. `RequireHttpsMetadata` is true outside Development. Issuer and
   audience are required whenever an authority is set; they are product-specific so a token minted
   for another product on the same authservice is refused.
1. **Fallback policy = authenticated user.** Every endpoint needs a bearer token unless it says
   otherwise. `/health` and `/alive` are anonymous; OpenAPI is anonymous in Development only.
   `/partner/notifications` falls under the fallback policy (see R11).
1. **No `Auth:Authority`:**
   - Development logs a warning (`auth: development-policy (anonymous allowed)`) and allows
     anonymous requests, so `dotnet run` and the CI container smoke test need no identity service.
   - Any other environment fails startup with a message naming `Auth__Authority`.
1. **The AppHost** declares authservice from the pinned image with its own PostgreSQL database and
   a secret `auth-signing-key` parameter, and hands the API `Auth__Authority`, only when that
   parameter is configured. Otherwise it starts as before and says so.
1. The simulated partner stays open; authentication is not added to it.

## Why this deviates from P8 outside Development

P8 says optional dependencies degrade instead of failing startup, and the storage layer follows it:
no connection string, the service runs in memory and says so. Authentication is not that kind of
dependency. A deployed payment API that quietly runs without authentication is not degraded, it is
open: anyone who can reach it can initiate and submit transfers. A degraded mode that is
indistinguishable from "working" to every caller is the failure P8's "degradation must be visible"
exists to prevent, and a log line does not protect the money. So outside Development a missing
authority is a startup failure. Development keeps the P8 behaviour because there the open mode is
the point, and it is made visible at the warning level on every start.

## Consequences

- Every client, including the notification sender, needs a token from authservice (R11).
- The deploy slice sets `ASPNETCORE_ENVIRONMENT=Production` on Fly, so a missing authority there fails the deploy loudly.
- The Development policy is a property of the environment name. A deployment that sets
  `ASPNETCORE_ENVIRONMENT=Development` is open; the deployment slice must not do that.
- Key rotation is authservice's concern; the API picks up a new JWKS through its metadata refresh.
- Tests supply the JWKS by replacing the configuration manager in the test host, with an RSA key
  generated at test run time; production code carries no test hook.
