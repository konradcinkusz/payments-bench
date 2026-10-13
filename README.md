# payments-bench

[![License](https://flat.badgen.net/github/license/konradcinkusz/payments-bench?icon=github&color=black&scale=1.01)](https://github.com/konradcinkusz/payments-bench/blob/main/LICENSE "License")
[![CI](https://github.com/konradcinkusz/payments-bench/actions/workflows/ci.yml/badge.svg)](https://github.com/konradcinkusz/payments-bench/actions/workflows/ci.yml "CI")
[![Build Overview PDF](https://github.com/konradcinkusz/payments-bench/actions/workflows/build-overview-pdf.yml/badge.svg)](https://github.com/konradcinkusz/payments-bench/actions/workflows/build-overview-pdf.yml "Build Overview PDF")

An outbound bank-transfer service, built to show the mechanisms a payment rail needs in order to
move money exactly once and to recover when the partner on the other end misbehaves. It is a
bench, not a product: the bank is simulated, the schemes are data, and a register at
[`docs/REGISTER.md`](docs/REGISTER.md) says what has and has not been proved.

## How it works

| Mechanism | Where | Why it exists |
|---|---|---|
| Idempotent initiation | `InitiatePaymentHandler` | The same request twice is one payment; the same key with a different body is a `409`, not a second transfer. |
| Payment state machine | `PaymentTransitions`, `Payment` | Six states, every allowed move in one table, a reason code on every failure, terminal states that accept nothing. |
| Submit without blind retry | `SubmitPaymentHandler`, `HttpPartnerGateway` | A timeout after the body went out is an **unknown** outcome. Nothing retries it; the resolver asks the partner by end-to-end id and resubmits only when the partner has no record. |
| Transactional outbox / inbox | `IOutbox`, `IInbox`, `OutboxDispatcher` | Events leave in the same commit as the state change; partner notifications are deduplicated by id because delivery is at least once. |
| Double-entry ledger | `JournalEntry`, `Postings` | Append-only, balanced per currency, balances derived. Customer funds move `customer-funds → pending-outbound → nostro`, and a return brings them back. |
| Reconciliation | `ReconciliationService` | One day's statement against the settled payments: matches, and three named kinds of break as a work queue, not an alert. |
| Scheme rules as data | `scenarios/schemes.yaml`, `ValueDateCalculator` | Cut-off time and settlement days per scheme; the value date is computed, not guessed. |
| Partner simulator with faults | `PaymentsBench.PartnerSimulator` | Latency, timeout after accept, 503, reject-all, statement drift: switched at runtime with `PUT /faults`. |
| Degraded storage | `InfrastructureRegistration` | No connection string: the service runs in memory and says so in the log, instead of refusing to start. |

Observability: OpenTelemetry traces, metrics and logs over OTLP, an opt-in `/metrics`, and a
provisioned Grafana dashboard; see [`docs/ops/`](docs/ops/README.md).

## The numbers

Each figure names the file it was measured in; the measurements were taken on `main` on 2026-10-13.

| | | |
|---|---|---|
| **The tests** | 110 · 30 + 42 + 26 + 12 | `dotnet test`: 30 in `tests/PaymentsBench.Domain.Tests`, 42 in `tests/PaymentsBench.Application.Tests` (in-memory stores), 26 in `tests/PaymentsBench.Integration.Tests` (real PostgreSQL), 12 in `tests/PaymentsBench.Api.Tests` (in-memory stores, no database). |
| **The scenarios** | 7 files · 4 classes | [`scenarios/`](scenarios/) holds seven scenario files in the classes `happy`, `partner-failure`, `duplicate` and `reconciliation`. They are a specification; none is executed yet (R3). |
| **The register** | 10 open rows | [`docs/REGISTER.md`](docs/REGISTER.md): R1 to R10, none closed. |
| **The findings** | 5 | [`docs/FINDINGS.md`](docs/FINDINGS.md): F-1 and F-2 fixed, F-3 to F-5 open. |
| **The load run** | 1210 requests/s, 0 duplicates | `k6/payments.js` with the simulator cycling five fault profiles, in memory, on one 4-CPU machine; per-profile numbers and what they do not prove are in [`docs/REGISTER.md`](docs/REGISTER.md) (R9, R15, R16). |
| **The schemes** | 3 | [`scenarios/schemes.yaml`](scenarios/schemes.yaml): `sepa`, `faster-payments`, `swift`. |

## Run it

```bash
dotnet test                                   # 272 tests; the 36 integration tests need PostgreSQL (see below)
dotnet run --project src/PaymentsBench.AppHost   # Aspire: PostgreSQL + simulator + API + dashboard
```

The integration tests (`tests/PaymentsBench.Integration.Tests`) find a database in one of two ways:
with Docker running they start a `postgres:16-alpine` Testcontainers container; otherwise set
`PAYMENTSBENCH_TEST_POSTGRES` to a server that trusts you, for example
`PAYMENTSBENCH_TEST_POSTGRES="Host=localhost;Port=5432;Username=postgres" dotnet test`. Each test
class gets its own throwaway database. With neither, they fail with a message saying so; they are never skipped.

Or without Aspire, in memory, two terminals:

```bash
dotnet run --project src/PaymentsBench.PartnerSimulator   # http://localhost:5101
dotnet run --project src/PaymentsBench.Api                # http://localhost:5100
```

Every endpoint except `/health` and `/alive` needs a bearer token from authservice (`Auth__Authority`, `Auth__Issuer`, `Auth__Audience`); add `-H "Authorization: Bearer $TOKEN"` to the calls below.
In Development with no `Auth__Authority` the API logs a warning and allows anonymous calls, so the calls below work as written; any other environment refuses to start without it.

Then walk a transfer through its life:

```bash
# initiate (the Idempotency-Key header is required)
curl -X POST localhost:5100/payments -H 'Content-Type: application/json' -H 'Idempotency-Key: demo-1' \
  -d '{"endToEndId":"E2E-1","amount":100.00,"currency":"EUR","debtorAccount":"DE89...","creditorAccount":"GB33...","scheme":"sepa"}'
curl -X POST localhost:5100/payments/<id>/submit
# make the partner time out after accepting, then watch the resolver recover without a duplicate
curl -X PUT localhost:5101/faults -H 'Content-Type: application/json' -d '{"kind":"TimeoutAfterAccept","delayMilliseconds":8000}'
curl -X POST localhost:5100/payments/<id>/submit      # -> Submitted (outcome unknown)
curl -X PUT localhost:5101/faults -H 'Content-Type: application/json' -d '{"kind":"None"}'
curl -X POST localhost:5100/payments/<id>/resolve     # -> {"outcome":"Accepted"}, one transfer at the partner
# settle, notify (twice: the second is a Duplicate), reconcile, read the ledger
curl -X POST 'localhost:5101/transfers/E2E-1/settle?valueDate=2026-10-14'
curl -X POST localhost:5100/partner/notifications -H 'Content-Type: application/json' \
  -d '{"notificationId":"N-1","endToEndId":"E2E-1","kind":"Settled","valueDate":"2026-10-14"}'
curl -X POST localhost:5100/reconciliation/runs/2026-10-14
curl localhost:5100/payments/<id>/ledger
```

## Where to read next

| If you want | Go to |
|---|---|
| **To be pointed at the right document** | [`docs/START-HERE.md`](docs/START-HERE.md): which of the four kinds of document you need, and where it is. |
| **The whole repository in words, one linear read** | [`docs/OVERVIEW.md`](docs/OVERVIEW.md): the source of truth for the argument, the mechanisms, the register and the honest readiness assessment. |
| **Diagrams that render on GitHub** | [`docs/DIAGRAMS.md`](docs/DIAGRAMS.md): six Mermaid diagrams (money path, state machine, submit and resolve, outbox and inbox, reconciliation, delivery topology), each also a file in [`docs/diagrams/`](docs/diagrams/). |
| **What building it caught** | [`docs/FINDINGS.md`](docs/FINDINGS.md): five findings with evidence, unflattering on purpose. |
| **The shape and the seven rules** | [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md). |
| **What is not proved** | [`docs/REGISTER.md`](docs/REGISTER.md): dated rows with closing conditions. |
| **Decisions already taken** | [`docs/adr/`](docs/adr/): one record each. |
| **How the documentation is published** | [`docs/DEPLOYMENT-DOCS.md`](docs/DEPLOYMENT-DOCS.md), including the one manual GitHub Pages step. |
| **A PDF edition of the overview** | [`docs/papers/payments-bench-overview.tex`](docs/papers/payments-bench-overview.tex), built on demand by the *Build Overview PDF* workflow and never committed: [OVERVIEW section 20](docs/OVERVIEW.md#20-getting-the-pdf) says how to get it. |
| **The site** | [`docs/index.html`](docs/index.html): one generated page with the argument, the numbers and the register, no external resource, opens from `file://`. It is published to GitHub Pages once the setting in [`docs/DEPLOYMENT-DOCS.md`](docs/DEPLOYMENT-DOCS.md) is made; until then, open the file locally. |

## What is built, and what has never run

Built, tested and on `main`: the domain, the application handlers, the in-memory and EF Core
stores, the HTTP surface, the partner simulator with switchable faults, and the 110 tests above.

Never run, recorded in [`docs/REGISTER.md`](docs/REGISTER.md) rather than in a footnote:

- **No real bank.** The partner is `PaymentsBench.PartnerSimulator`; its API shape is invented
  (R1). No ISO 20022 message is parsed or produced (R2).
- **The scenarios do not run.** `ScenarioFilesTests` checks the seven files parse; no runner
  executes them against the API and the simulator (R3).
- **PostgreSQL has not run in CI** (R4), the outbox publishes to a log line (R5), concurrency
  is a column and not a test (R6), the calendar is weekends only (R7), nothing resolves a
  `Submitted` payment on its own (R8), the first load run is in memory only and found a defect in `resolve` (R15, R16), and there is no
  sanctions, AML or limits control (R10).
- **Three open defects**, F-3 to F-5 in [`docs/FINDINGS.md`](docs/FINDINGS.md).

### How it was built

Specification first: the state machine, the ledger postings, the unknown-outcome rule and the
scenario files in `scenarios/` were written before the handlers. The implementation was then
generated with an AI coding agent against that specification and accepted only where the tests
in `tests/` and the HTTP walk-through above proved it did what the specification said. The
[register](docs/REGISTER.md) lists what that process has not yet proved, with dates, so that a
reader does not have to guess.

## Repository layout

```text
src/PaymentsBench.Domain            Money, Payment + transitions, JournalEntry + Postings, SchemeRules
src/PaymentsBench.Application       ports and handlers: initiate, submit, resolve, notify, reconcile
src/PaymentsBench.Infrastructure    in-memory and EF Core/PostgreSQL stores, outbox dispatcher, HTTP partner gateway
src/PaymentsBench.Api               the HTTP surface; Program.cs is the manifest
src/PaymentsBench.PartnerSimulator  a bank that misbehaves on request
src/PaymentsBench.ServiceDefaults   OpenTelemetry, health, service discovery (no domain types)
src/PaymentsBench.AppHost           Aspire composition root
tests/                              domain and application tests on the in-memory stores
scenarios/                          the fault scenarios as data, and the scheme rules
docs/                               architecture, the register, decision records
  START-HERE.md  OVERVIEW.md  FINDINGS.md  ARCHITECTURE.md  REGISTER.md
  DIAGRAMS.md                       six Mermaid diagrams, each also a file in diagrams/
  DEPLOYMENT-DOCS.md                how the documentation is published
  index.html                        the generated site (npm run site:build)
  site/                             its source, stylesheet and facts file
  papers/                           LaTeX edition of OVERVIEW.md, built to PDF on demand
  adr/                              decision records
  plans/                            the three streams of planned work
scripts/                            documentation tooling (link check, diagram render and check, LaTeX source check, site generator)
package.json                        documentation tooling only: markdownlint, link check, diagram check, papers check and the site generator, nothing .NET depends on it
.github/workflows/                  CI, the PDF build, the Pages deploy
```

## How this repository relates to the standards

It follows [architecture-standards](https://github.com/konradcinkusz/architecture-standards),
declared as a plugin in `.claude/settings.json`; `AGENTS.md` says what that means day to day.
The structure and approach of this documentation are modelled on
[agent-eval-bench](https://github.com/konradcinkusz/agent-eval-bench), a sibling bench that
documents what is built and what has never run. The approach is reproduced; the prose is not.

## Non-goals

- **No real bank, scheme or sandbox connection.** The partner is simulated, and the bench stops
  at the gateway interface (R1).
- **No sanctions, AML or limits.** Out of scope; stays open as a statement of scope (R10).
- **No frontend, no customer or account management.**
- **No claim of production readiness.** [`docs/OVERVIEW.md`](docs/OVERVIEW.md) section 15 says
  what would have to change.

## License

MIT. See [`LICENSE`](LICENSE).
