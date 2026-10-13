# Stream B plan: platform, delivery, identity

Owner: stream B. Scope: `.github/` (except `build-overview-pdf.yml` and `deploy-pages.yml`),
`Dockerfile`s, `flyio/`, `scripts/`, `.claude/`, `.gitleaks*`, `k6/`, the operations rows of
`docs/REGISTER.md` (R9, R10 and any new operations rows), and the identity wiring of
`PaymentsBench.Api`. Domain, persistence and scenario work belong to stream A; the overview
docs, PDF and site belong to stream C.

Standards: [architecture-standards](https://github.com/konradcinkusz/architecture-standards),
reference architecture P1-P15 and its compliance checklist, the repo-baseline, Fly.io,
shared-service-reuse, identity-and-accounts, metrics-exposition, readme-badges and
security-review guides.

Baseline on `main` when this plan was written (2026-10-13): `dotnet build -warnaserror` clean,
37 tests green, one workflow (`ci.yml`, build + test).

## Decisions taken in this plan

1. **Identity is authservice, run from its image.** `ghcr.io/konradcinkusz/authservice:v0.3.4`,
   pinned, never `:latest`. It is the only service holding a signing key (RS256); the API
   validates against its JWKS through `MetadataAddress` =
   `<authority>/.well-known/openid-configuration`. Its source is never vendored or modified.
2. **Deny by default.** The API sets a fallback authorization policy that requires an
   authenticated user; the anonymous list is short and explicit: `/health`, `/alive`, and the
   OpenAPI document in Development. The partner simulator stays open: it is a simulated bank,
   not a product surface. `Program.cs` changes stay registration lines (P9); the wiring lives in
   an extension method in the API project.
3. **Degraded identity is Development-only.** With `Auth:Authority` unset the API logs a warning,
   reports `auth: development-policy` in its startup banner, and allows anonymous calls, so
   `dotnet run` in memory still works with no identity provider (P8). Outside Development an
   unset `Auth:Authority` stops startup naming the setting: a deployed payment API without
   authentication is not a degraded mode, it is an open one. Recorded as an ADR and as a
   deviation from P8.
4. **Deploy never needs a paid account to pass CI.** Every `flyctl` step in `deploy.yml` is
   guarded: without `FLY_API_TOKEN` the job writes a step summary saying what it would have
   deployed and exits green. Secrets are listed by name only (`docs/DEPLOYMENT.md`).
5. **Images go to GHCR** (blueprint §2: GHCR wins over `registry.fly.io`), built once per tag and
   deployed by digest/tag with `flyctl deploy --image`.
6. **Secret scanning is gitleaks, twice**: a pre-commit hook installed by `scripts/setup.sh` and a
   CI workflow scanning full history (same `.gitleaks.toml`).
7. **Numbers are claimed only once measured.** R9 is narrowed with the numbers a real k6 run
   produced on the CI runner or the session container, with the machine named; it closes only
   if its closing condition is met as written.
8. **Shared files** (`Program.cs`, `ci.yml`, `README.md`, `docs/REGISTER.md`,
   `PaymentsBench.sln`) are edited minimally and every slice rebases on `main` before merge.
   New workflows go in their own files rather than growing `ci.yml`. New register rows and ADRs
   take the next free number on `main` at merge time.

## Note to stream A: metric names

Stream A's plan (`docs/plans/stream-a-core.md`) had not landed on `main` when this plan was
written. Stream B's Grafana dashboard and alert text therefore assume these instruments on the
`PaymentsBench` meter, and stream A is asked to use exactly these names (or to change the
dashboard in the same PR if it renames them):

| Instrument | Kind | Unit | Tags (bounded) | Meaning |
|---|---|---|---|---|
| `payments.transitions` | counter | `{transition}` | `status` (six states + notification kinds) | already on `main` |
| `payments.submitted.oldest_age_seconds` | observable gauge | `s` | none | age of the oldest payment still at Submitted |
| `payments.sweep.resolved` | counter | `{payment}` | `outcome` (the `ResolveOutcome` enum) | payments the background sweep resolved |

OpenTelemetry's Prometheus naming turns these into `payments_transitions_total`,
`payments_submitted_oldest_age_seconds` and `payments_sweep_resolved_total`.

## Slices

Each slice is one branch `claude/stream-b/<slice>`, one PR against `main`, CI green, reviewed
with the pr-review skill before merge. Waves respect the limit of four concurrent sessions and
the dependency between slices.

### B1 - Repo baseline (wave 1) - PR: _pending_

Principle: P5 (secret scanner in CI), repo-baseline §1-§4 and §9.

Acceptance criteria:
- `.gitleaks.toml` (extends the default rules; any allowlist entry is narrow and commented).
- `scripts/hooks/pre-commit` runs gitleaks on staged changes; `scripts/install-hooks.sh`
  installs it; `scripts/scan-secrets.sh` mirrors the CI job locally.
- `.github/workflows/secret-scan.yml`: full-history gitleaks on PR, push to `main` and weekly,
  with a step summary that says "rotate first" on failure.
- `scripts/setup.sh`: one command; checks prerequisites, installs `dotnet-sdk-10.0` from the
  Ubuntu archive if `dotnet` is missing (sudo, Ubuntu only, otherwise prints install pointer),
  installs the hook, restores, builds with `-warnaserror`, runs tests; numbered steps;
  optional integrations (Docker, k6, flyctl) labelled `(optional - needed for <feature>)`.
- `scripts/README.md`: every script, every environment variable by tier, error-text-keyed
  troubleshooting table. `secrets.env.example` at the root; `.env*` ignored except the example.
- `.github/workflows/codeql.yml` (C#, `build-mode: manual` or `autobuild` that succeeds).
- `.github/workflows/lint.yml`: actionlint over `.github/workflows/`; markdownlint only if
  stream C has not added a config on `main` (check first; if C owns it, do not add one).
- `.github/dependabot.yml` (nuget, github-actions, docker), `.github/CODEOWNERS`,
  `.github/pull_request_template.md`, a real `.gitattributes`.
Verification: `scripts/setup.sh` runs green in a fresh container; `actionlint` clean locally;
gitleaks clean on full history locally; the new workflows green on the PR.
Register: none closed; opens none.

### B2 - Containers (wave 1) - PR: _pending_

Principle: P6.

Acceptance criteria:
- `src/PaymentsBench.Api/Dockerfile` and `src/PaymentsBench.PartnerSimulator/Dockerfile`:
  multi-stage (`sdk:10.0` build, `aspnet:10.0` final), project files and
  `Directory.*.props`/`global.json` copied and restored before the source, `ASPNETCORE_URLS=http://+:8080`,
  `EXPOSE 8080`, `USER $APP_UID` (non-root), `HEALTHCHECK` hitting `/health`,
  `LABEL org.opencontainers.image.source=https://github.com/konradcinkusz/payments-bench`.
- Root `.dockerignore`, exclusion-based (`tests/`, `docs/`, `k6/`, `bin/`, `obj/`, `.git`, ...).
- `.github/workflows/containers.yml`: on PR and push to `main`, a matrix (`fail-fast: false`)
  builds both images with the GHA layer cache, runs each, and polls `/health` until healthy; no
  push from this workflow.
Verification: both images build and answer `/health` locally (or in CI if Docker is not
available in the session); `id` inside the container is not root.
Register: none.

### B3 - Identity: authservice and JwtBearer (wave 1) - PR: _pending_

Principle: P5 (JWT / OIDC, asymmetric + JWKS), P8, P9; compliance checklist "exactly one
service holds a signing key; all others validate against its JWKS endpoint".

Acceptance criteria:
- `Microsoft.AspNetCore.Authentication.JwtBearer` (central version) in the API.
- `src/PaymentsBench.Api/Identity/IdentityRegistration.cs` (or equivalent extension):
  `Auth:Authority`, `Auth:Issuer`, `Auth:Audience` from configuration; `MetadataAddress` =
  `{Authority}/.well-known/openid-configuration`; `ValidIssuer`, `ValidAudience`,
  `ValidateIssuerSigningKey = true`, RS256 only (`ValidAlgorithms`); `RequireHttpsMetadata`
  true outside Development.
- Fallback policy = authenticated user; `/health`, `/alive` anonymous (in ServiceDefaults'
  `MapDefaultEndpoints` or by `.AllowAnonymous()` at the API's registration), OpenAPI anonymous
  in Development only. `Program.cs` gains registration lines only.
- `Auth:Authority` unset: Development logs a warning and allows anonymous; any other
  environment fails startup naming `Auth__Authority` (decision 3).
- `tests/PaymentsBench.Api.Tests` (WebApplicationFactory): unauthenticated `POST /payments` is
  401 when `Auth:Authority` is configured; a token signed by a test RSA key the test serves as
  the JWKS is accepted (201 or the handler's normal status); `/health` is 200 anonymous; with
  no authority in Development `POST /payments` is not 401.
- AppHost: authservice declared from the pinned image with its own PostgreSQL database and
  signing key parameter, the API given `Auth__Authority`, when the signing-key parameter is
  configured; otherwise the AppHost runs as today and says so.
- ADR (next free number): identity via authservice JWKS, Development-only anonymous policy.
- README "Run it" mentions the token requirement and the Development policy in two lines.
Verification: `dotnet build -warnaserror`, `dotnet test` (new tests included).
Register: opens a row if the partner webhook (`/partner/notifications`) remains authenticated
by bearer token only, i.e. without partner signature verification.

### B4 - Fly.io topology and tag-driven deploy (wave 2, after B2 and B3) - PR: _pending_

Principle: P7, P12, shared-service-reuse §4-§5.

Acceptance criteria:
- `flyio/api.fly.toml` (`min_machines_running = 1`, synchronous path), `flyio/partner.fly.toml`
  (`min_machines_running = 1`: the API calls it in-request), `flyio/authservice.fly.toml`
  (pinned image, `min_machines_running = 1`, its own PostgreSQL), `flyio/postgres.fly.toml`
  (no public listener, volume with `PGDATA` subdirectory) if PostgreSQL runs as a Fly app;
  every field annotated; health check on `/health`.
- `.github/workflows/deploy.yml` on `v*` tags (+ `workflow_dispatch`): test, change detection
  against the previous tag (shared kernel / props invalidate all), "missing Fly app is always
  selected", build matrix to GHCR with `fail-fast: false`, ordered deploy
  postgres -> authservice -> partner -> api with `success || skipped` gates, a JWKS check after
  authservice deploys. Every `flyctl` step guarded: no `FLY_API_TOKEN` -> step summary, green.
- `docs/DEPLOYMENT.md`: topology, cost reasoning, secrets **by name only**, bootstrapping,
  first-tag procedure, teardown.
- Register: new operations row "Nothing has been deployed" (closes when a tag deploys the
  estate and one authenticated payment is walked against the public URL).
Verification: actionlint clean; `flyctl config validate` where available; a dry run of the
workflow logic (no token) green on the PR via `workflow_dispatch` or a test tag on a fork.

### B5 - Observability for operations (wave 1) - PR: _pending_

Principle: P15, metrics-exposition guide.

Acceptance criteria:
- OTLP end to end proved: the Aspire dashboard (or an OTLP collector in the session) receives
  traces, metrics and logs from the API and the simulator; the proof is recorded in the PR.
- Health-check requests filtered out of traces (P15).
- `/metrics` (OpenTelemetry Prometheus exporter) only behind a configuration switch
  (`Metrics:Prometheus:Enabled`, default off), with every label's bound written down; if the
  guide's cardinality rules cannot be met, no endpoint and the reason in the PR.
- `docs/ops/grafana/payments-bench.json` (provisioned dashboard: transitions by status, oldest
  Submitted age with the alert threshold line, sweep resolved by outcome), plus datasource and
  scrape config under `docs/ops/grafana/`.
- Metric names as in the table above; if stream A's plan has landed and differs, follow it.
Verification: build and tests green; exporter test or a curl transcript of `/metrics` in the PR.
Register: none closed.

### B6 - Load and chaos: k6 (wave 2) - PR: _pending_

Principle: P13 (test at the layer that has the logic), register R9.

Acceptance criteria:
- `k6/payments.js`: initiate + submit (+ resolve for Submitted) against the API; a separate
  scenario cycles the simulator's fault profiles (`None`, latency, `TimeoutAfterAccept`, 503,
  reject-all) through `PUT /faults` on a timer; thresholds on error rate and p95; it counts
  duplicate transfers at the simulator (must be zero).
- `scripts/load-test.sh` starts the simulator and the API in memory, runs k6, stops them.
- `.github/workflows/load.yml`: `workflow_dispatch` only, uploads the k6 summary.
- First numbers in `docs/REGISTER.md` R9: throughput, p95, error rate per fault profile,
  duplicates, machine. Row narrowed, closed only if its closing condition is met.
Verification: one real run, its summary attached to the PR.

### B7 - Security review (wave 3, after B3 and B4) - PR: _pending_

Principle: security-review guide; P5.

Acceptance criteria:
- `docs/security/SECURITY-REVIEW.md`: OWASP category walk with justified N/As (grep pasted),
  positive findings, findings in the standard format with attack scenarios, endpoint x role
  matrix, time-windowed priorities, readiness ledger (blocks deployment / before deployment),
  residual risks, the pentest caveat.
- `SECURITY.md` at the root: how to report, supported versions, what is simulated.
- Fixes for any P1 finding in the same PR if small; otherwise register rows.

### B8 - README badges (wave 3, last) - PR: _pending_

Principle: readme-badges guide. Header row plus one badge per merge-gating workflow that exists
on `main` at the time (CI, containers, secret scan, CodeQL, lint). No deployment badge until a
deployment exists.

## Done

Every slice above ticked with its PR number, CI green on `main` including the new workflows,
the register updated with proof, sub-sessions archived.
