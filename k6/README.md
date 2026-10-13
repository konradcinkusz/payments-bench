# Load and chaos (k6)

`payments.js` drives the API while the simulated partner bank cycles through its fault profiles.
It closes the measurement half of register row R9; the numbers are in
[`docs/REGISTER.md`](../docs/REGISTER.md). Nothing here talks to a real bank.

## Run it

```bash
scripts/load-test.sh                      # 10 VUs, 30 s per profile, one cycle (150 s)
VUS=20 PHASE_SECONDS=60 scripts/load-test.sh
```

The script builds the solution, starts the simulator on `:5101` and the API on `:5100`, runs k6,
stops both, and prints the API's warning-level log lines. If `k6` is not on `PATH` it downloads
the pinned release (v1.3.0, checksum verified). The k6 summary is written to `k6-summary.json`
(ignored by git). The same script is run by the manual workflow
[`load.yml`](../.github/workflows/load.yml), which uploads the summary.

| Variable | Default | Meaning |
|---|---|---|
| `VUS` | 10 | Virtual users running `initiate`, `submit` and `resolve` |
| `PHASE_SECONDS` | 30 | How long each fault profile stays active |
| `DURATION_SECONDS` | 5 x `PHASE_SECONDS` | Whole run; one full cycle by default |
| `SUMMARY_FILE` | `k6-summary.json` | Where the JSON summary goes |

## What runs

- **`payments` scenario.** Each iteration initiates a payment with a unique idempotency key and
  end-to-end id, submits it, and, if the outcome is unknown (still `Submitted`), resolves it.
  One iteration in ten replays the initiate with the same key (must return the same payment);
  one in twenty reuses the key with a different amount (must answer 409).
- **`faults` scenario.** One VU sets the simulator's profile with `PUT /faults` on a timer, in
  this order: `None`, `Latency` (1.5 s), `TimeoutAfterAccept` (10 s, longer than the API's 4 s
  attempt timeout), `ServerError` (503), `RejectAll` (`AC01`). `StatementDrift` is not cycled: it
  only affects statements, which this run does not read.
- Both scenarios derive the active profile from the same start time, so every API request is tagged
  `profile:<kind>`. A request that straddles a phase boundary keeps the tag it started with.

## Errors, expected outcomes and thresholds

Expected under faults, **not** counted as errors: `Submitted` with an unknown outcome, `Rejected`,
`Submitted->SafeToResubmit` after a resolve, and a 409 on a replayed key with a different body.
Counted as **real errors**: a 5xx from the API, any status outside the expected set for that step,
and transport failures. `real_failures{profile,step}` says where.

Thresholds (the run fails if any is crossed): real error rate below 1 % overall and per profile,
`http_req_duration` p95 per profile (500 ms `None`, 3 s `Latency`, 6 s `TimeoutAfterAccept`, 1 s
`ServerError` and `RejectAll`), no duplicate payments, no simulator mismatches.

## Duplicates: what is and is not measured

The simulator keeps transfers in a dictionary keyed by end-to-end id and answers a repeated
`POST /transfers` with the existing transfer. It exposes no list and no count of submissions, so
it **cannot report** a second submit of the same end-to-end id; a duplicate attempt is invisible
there. No such number is invented. What the run does check:

- `duplicate_payments`: replaying an initiate with the same key returns the same payment id.
- `simulator_mismatches`: for each payment that ends `Accepted`, `GET /transfers/{endToEndId}` on
  the simulator finds exactly that transfer; for each `Rejected` one it finds none.

Counting submissions at the simulator needs a counter there; that is a simulator change and is
left to the owner of the simulator.

## Authentication

Once identity lands the API requires a bearer token outside Development. The load script runs the
API with `ASPNETCORE_ENVIRONMENT=Development` and no `Auth__Authority`, because the Development
policy allows anonymous calls. An authenticated run against authservice is future work.

## Limits of these numbers

In-memory stores, one API instance, one simulator, one machine, no PostgreSQL, no authentication,
no network between the services beyond loopback. They show how the resilience pipeline and the
handlers behave under fault profiles, not what production would sustain.
