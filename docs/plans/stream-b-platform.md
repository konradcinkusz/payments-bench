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
1. **Deny by default.** The API sets a fallback authorization policy that requires an
   authenticated user; the anonymous list is short and explicit: `/health`, `/alive`, and the
   OpenAPI document in Development. The partner simulator stays open: it is a simulated bank,
   not a product surface. `Program.cs` changes stay registration lines (P9); the wiring lives in
   an extension method in the API project.
1. **Degraded identity is Development-only.** With `Auth:Authority` unset the API logs a warning,
   reports `auth: development-policy` in its startup banner, and allows anonymous calls, so
   `dotnet run` in memory still works with no identity provider (P8). Outside Development an
   unset `Auth:Authority` stops startup naming the setting: a deployed payment API without
   authentication is not a degraded mode, it is an open one. Recorded as an ADR and as a
   deviation from P8.
1. **Deploy never needs a paid account to pass CI.** Every `flyctl` step in `deploy.yml` is
   guarded: without `FLY_API_TOKEN` the job writes a step summary saying what it would have
   deployed and exits green. Secrets are listed by name only (`docs/DEPLOYMENT.md`).
1. **Images go to GHCR** (blueprint §2: GHCR wins over `registry.fly.io`), built once per tag and
   deployed by digest/tag with `flyctl deploy --image`.
1. **Secret scanning is gitleaks, twice**: a pre-commit hook installed by `scripts/setup.sh` and a
   CI workflow scanning full history (same `.gitleaks.toml`).
1. **Numbers are claimed only once measured.** R9 is narrowed with the numbers a real k6 run
   produced on the CI runner or the session container, with the machine named; it closes only
   if its closing condition is met as written.
1. **Shared files** (`Program.cs`, `ci.yml`, `README.md`, `docs/REGISTER.md`,
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

### B1 - Repo baseline (wave 1) - PR: #5 (merged)

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

### B2 - Containers (wave 1) - PR: #8 (merged)

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

### B3 - Identity: authservice and JwtBearer (wave 1) - PR: #14 (merged)

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

### B4 - Fly.io topology and tag-driven deploy (wave 2, after B2 and B3) - PR: #18 (merged)

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

### B5 - Observability for operations (wave 1) - PR: #9 (merged)

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

### B6 - Load and chaos: k6 (wave 2) - PR: #19 (merged)

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

### B7 - Security review (wave 3, after B3 and B4) - PR: #29 (merged)

Principle: security-review guide; P5.

Acceptance criteria:

- `docs/security/SECURITY-REVIEW.md`: OWASP category walk with justified N/As (grep pasted),
  positive findings, findings in the standard format with attack scenarios, endpoint x role
  matrix, time-windowed priorities, readiness ledger (blocks deployment / before deployment),
  residual risks, the pentest caveat.
- `SECURITY.md` at the root: how to report, supported versions, what is simulated.
- Fixes for any P1 finding in the same PR if small; otherwise register rows.

### B8 - README badges (wave 3, last) - PR: #25 (merged)

Principle: readme-badges guide. Header row plus one badge per merge-gating workflow that exists
on `main` at the time (CI, containers, secret scan, CodeQL, lint). No deployment badge until a
deployment exists.

## Outcome

| Slice | PR | What landed | Register |
|---|---|---|---|
| B1 baseline | #5 | gitleaks pre-commit hook and full-history CI job, `scripts/setup.sh`, CodeQL, actionlint, Dependabot, CODEOWNERS, PR template, `.gitattributes`, `secrets.env.example` | none |
| B2 containers | #8 | multi-stage non-root Dockerfiles for the API and the simulated partner, `.dockerignore`, `containers.yml` (build, run, `/health`, uid check) | none |
| B3 identity | #14 | JwtBearer against authservice's JWKS (RS256 only), deny by default, Development-only anonymous mode, AppHost wiring for the pinned authservice image, ADR 0015 | R13 opened |
| B4 deploy | #18 | four annotated `fly.toml`s, `deploy.yml` (change detection, missing app always selected, GHCR build, ordered deploy, JWKS and 401 checks, token-guarded), scale and destroy workflows, `docs/DEPLOYMENT.md` | R14 opened |
| B5 observability | #9 | probes filtered from traces, opt-in anonymous `/metrics` with bounded labels, provisioned Grafana dashboard and alert under `docs/ops/`, OTLP proved through a collector | none |
| B6 load | #19 | `k6/payments.js` with fault-profile cycling, `scripts/load-test.sh`, manual `load.yml` | R9 closed, R15 and R16 opened |
| B7 security | #29 | `docs/security/SECURITY-REVIEW.md` (18 findings, ledger, residual risks), `SECURITY.md`, grants for the partner webhook and back-office reads, Fly guard on the anonymous mode, 64 KiB body cap, ADR 0016 | R21-R26 opened, R13 reworded |
| B8 badges | #25 | standard header row and one badge per merge-gating workflow | none |
| (lead) | #28 | the telemetry test waits for the exported span instead of reading once (a flake seen on stream A's #23) | none |

Proof on `main`: every merge-gating workflow green (CI, Containers, CodeQL, Secret Scan, Lint).
`deploy.yml` dispatched without `FLY_API_TOKEN` (run 38440218587): every job green, deploy jobs
skipped with a summary. `load.yml` dispatched twice on a GitHub-hosted runner: run 38440765949
(before stream A's resolve fix, red on R16 exactly as recorded) and run 38442934326 (after it,
zero real failures; red on one latency threshold, see F-B6).

## Findings

What stream B's builds, scans, load runs and review caught, for `docs/FINDINGS.md`:

- **F-B1 - The floating SDK image ships newer analyzers than CI.** Building the images on
  `mcr.microsoft.com/dotnet/sdk:10.0` raised CA1848/CA1873 on code that is clean under
  `global.json`'s SDK, so the Dockerfiles publish with `TreatWarningsAsErrors=false` and CI's own
  build stays the gate (#8).
- **F-B2 - Resolve answered 500 while the partner circuit was open.** Found by the first k6 run:
  every real failure was `POST /payments/{id}/resolve` under `ServerError`/`RejectAll`. Fixed by
  stream A (A5, #21); confirmed under load by run 38442934326. Register R16 (closed).
- **F-B3 - The simulator cannot count submissions.** It keys transfers by end-to-end id and
  answers a repeat with the existing one, so duplicate transfers are checked indirectly. R15.
- **F-B4 - GHCR packages start private, and Fly cannot pull a private image.** A one-time manual
  visibility change no token can make; documented in `docs/DEPLOYMENT.md` and guarded by a
  pre-deploy manifest check (#18). R14.
- **F-B5 - An empty `Metrics:Prometheus:Enabled` crashed startup.** `GetValue<bool>` throws on
  an empty string; an empty or unparseable switch now means off (#9).
- **F-B6 - Status lookups retried while the circuit was open.** After the F-B2 fix the
  `ServerError` profile's p95 rose from 13 ms to 9601 ms (4.7 requests/s): the resilience pipeline
  retried `BrokenCircuitException` on the idempotent GET. Confirmed by stream A, fixed in their
  slice A9 (its own register row). R15 records the measurement.
- **F-B7 - The telemetry test read the exporter once.** The test client can see the response
  before the hosting layer stops the activity; fixed at the cause (#28).
- **F-B8 - The security review's open items.** No payment owner (BOLA) and a global idempotency
  key (R21), end-to-end id uniqueness (R22), no rate limiting (R23), operator separation and audit
  trail (R24), paging overflow and unbounded partner responses (R25), deployment settings that live
  outside the tree (R26). Details and attack scenarios in `docs/security/SECURITY-REVIEW.md`.
- **F-B9 - Three streams numbering in parallel collide.** Register rows and ADR numbers were
  taken twice during the day (two ADRs 0008 on `main` until #29 moved identity to 0015). Numbers
  were reserved by message between leads afterwards; a numbering check in `site:check` would make
  it mechanical.

## Open, and why

- **Nothing is deployed (R14).** Needs a Fly account, `FLY_API_TOKEN` in the `production`
  environment, the root secrets, and the GHCR packages made public once. Nothing in CI requires it.
- **The load numbers are in memory, one instance, anonymous (R15).** The authenticated, PostgreSQL,
  multi-instance run needs authservice running and is future work; `load.yml` is to be re-run after
  stream A's A9.
- **The partner webhook is bearer-authenticated, not partner-signed (R13).**
- **R21-R26** from the security review; four are stream A's code, two need repository and
  platform settings a person makes.
- **The AppHost's authservice wiring compiles but has not run** (no container runtime in the
  sessions that built it), and the Grafana compose stack is validated but not started.

## Done

Every slice above is ticked with its PR number, CI is green on `main` including the new
workflows, the register is updated with proof, and the sub-sessions are archived.
