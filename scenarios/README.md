# Scenarios

Each file describes one thing the payment rail must survive, as data: the fault the partner
injects, the calls made against the API, and the state the system must end in. The runner
(`tests/PaymentsBench.Scenarios`, not yet written - see `docs/REGISTER.md`) will execute them
against the API and the simulator; until then they are the specification the handlers were
written against, and `ScenarioFilesTests` checks that every file parses and names an expected
terminal state.

Classes, in the order a reviewer should read them:

| Class | What it proves |
|---|---|
| `happy` | The plain path works and the ledger nets to the right balances. |
| `partner-failure` | A refusal, a 5xx, a timeout after acceptance: each leaves the payment in a recoverable state and never a duplicate transfer. |
| `duplicate` | The same request, the same notification, the same statement line, twice. |
| `reconciliation` | A statement that disagrees with the ledger produces named breaks, not an alert. |
