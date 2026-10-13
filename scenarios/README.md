# Scenarios

Each file describes one thing the payment rail must survive, as data: the fault the partner
injects, the calls made against the API, and the state the system must end in. The runner (`tests/PaymentsBench.Scenarios`, ADR 0011) executes every file here
end to end against the API and the simulated partner, both in process, on in-memory storage and, when
`PAYMENTSBENCH_TEST_POSTGRES` is set (CI does), on PostgreSQL as well. A new file is a new case;
`schemes.yaml` and `calendars/` are data, not scenarios, and are skipped. Run it with
`dotnet test tests/PaymentsBench.Scenarios`.

## File format

Top level: `id` (equals the file name), `class`, `description`, `partner` (`fault`, and optionally
`driftAmount`, `reasonCode`, `delayMilliseconds`, set on the simulator before the first step),
optional `clock` (the fixed time the API runs at; default `2026-10-13T10:00:00Z`, a Tuesday),
`steps`, `expect`. An unknown key anywhere fails the case, naming the file.

Fault names are the simulator's `FaultKind` in kebab-case: `none`, `latency`, `timeout-after-accept`,
`server-error`, `reject-all`, `statement-drift`. The runner shortens the API's partner timeouts to about
one second and the simulated timeout-after-accept delay to three.

| Step | What it does |
|---|---|
| `initiate` | `POST /payments` with the `idempotencyKey`, `endToEndId`, `amount`, `currency`, `scheme` (accounts optional). |
| `initiateExpectingConflict` | The same, asserting `409`. |
| `submit: <endToEndId>` | `POST /payments/{id}/submit`; the runner keeps the end-to-end id to id map. |
| `assertStatus: <status>` | The scenario's payment is in that status now. |
| `partnerFault: <fault>` or a map | Switches the simulator's fault mid-scenario (`PUT /faults`). |
| `resolve: <endToEndId>` | `POST /payments/{id}/resolve`. |
| `partnerSettles` | Settles the transfer at the simulator; `valueDate` is `next-business-day` (first business day after the clock on the payment's scheme calendar, from the service's catalog) or a date. |
| `partnerReturns` | Returns the transfer at the simulator (`endToEndId`, optional `reasonCode`). |
| `notify` | `POST /partner/notifications`. A `Settled` without `valueDate` uses the date the partner settled with, if the scenario settled it. |
| `reconcile: value-date` or a date | `POST /reconciliation/runs/{date}`; `value-date` is the payment's value date. |

`expect` keys, all asserted, about the scenario's payment (the first one initiated) unless noted:
`status`, `valueDate`, `payments` (total from `GET /payments`), `ledgerEntries`, `ledger` (balance per
account in the payment's currency), `partnerSubmits` and `partnerStatusQueries` (counted at the
simulated partner, `GET /diagnostics/calls`), `reconciliation.breaks` and `reconciliation.kinds`
(from the last `reconcile`).

Classes, in the order a reviewer should read them:

| Class | What it proves |
|---|---|
| `happy` | The plain path works and the ledger nets to the right balances. |
| `partner-failure` | A refusal, a 5xx, a timeout after acceptance: each leaves the payment in a recoverable state and never a duplicate transfer. |
| `duplicate` | The same request, the same notification, the same statement line, twice. |
| `reconciliation` | A statement that disagrees with the ledger produces named breaks, not an alert. |
