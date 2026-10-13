# ADR 0012: Scenarios run in process against the API and the simulator

**Status:** accepted, 2026-10-13

## Context

The files in `scenarios/*.yaml` were a specification nobody executed (register row R3).
`ScenarioFilesTests` proved they parse; a bug that lives between two processes, such as the enum
serialisation of F-1 or the resilience pipeline of F-2, was invisible to every test. A scenario
suite is evidence only when it runs and CI gates on it (P5, P13).

## Decision

- **`tests/PaymentsBench.Scenarios`** discovers every `scenarios/*.yaml` at run time, except
  `schemes.yaml` and `calendars/`, and runs each as one xUnit theory case per storage mode. A new
  file is a new case with no code change.
- **In process, not containers.** Each case starts a fresh pair, the API and the simulated partner,
  through `WebApplicationFactory`. The API's partner `HttpClient`s (the gateway's named client and
  the statement source's typed client) get the simulator's `TestServer` handler, so every call is
  real HTTP serialisation through the real resilience pipeline, without a socket. The API runs in
  `Development` with no `Auth:Authority`, with a fixed clock (2026-10-13T10:00:00Z unless the file
  sets `clock:`) and with `Sweeper:Enabled=false` so no background sweep races the explicit `resolve` steps.
- **The partner is told, not wrapped.** The fault is set on the simulator through `PUT /faults`;
  partner call counts are read from the simulator's own `GET /diagnostics/calls` (simulated), not
  from the API's account of itself.
- **Timeouts are configuration** (`Partner:Timeout`, `Partner:AttemptTimeout`, `Partner:TotalTimeout`,
  defaults unchanged). The runner sets them to about one second and the simulated
  timeout-after-accept delay to three, so a timeout scenario finishes in seconds.
- **Two storage modes.** In-memory always; PostgreSQL as well when `PAYMENTSBENCH_TEST_POSTGRES` is
  set (CI sets it), each scenario on its own fresh database, migrated by the service's own hosted
  migrator, with the runner waiting for `/health`. When the variable is absent the PostgreSQL cases
  are not generated and a test says so in its output. The fixture is the integration project's,
  compiled in by link, not copied.
- **Strict vocabulary.** An unknown step, step argument, `expect` key or top-level key fails the
  case, naming the file; every `expect` key is asserted; failures name scenario, step, expected and actual.
- CI gates on it with a named `Scenarios` step in `ci.yml`. The solution-wide test step also runs the
  project; the duplication is accepted so the gate has its own name and trx.

## Consequences

- R3 is closed. A change that breaks a scenario fails CI, and a scenario file is the first thing to
  write for a new failure mode.
- What this does **not** prove: no process boundary, container, network, TLS, DNS, real clock or
  real concurrency is involved; the simulator is still a simulator (R1), so passing scenarios say
  the service copes with the partner shapes the simulator can produce, not with a real bank's. The
  fixed clock hides time-dependent behaviour other than the value dates the scenarios name. Startup
  of the published container images is covered by the container build, not here.
- Scenarios run one at a time inside the project, so each PostgreSQL case pays for creating and
  dropping a database; the suite stays small enough for that to be cheap.
