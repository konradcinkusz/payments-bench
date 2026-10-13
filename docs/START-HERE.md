# Start here

The front door to this repository's documentation. It tells you which kind of document you need
and sends you there.

## What is this repository?

**An outbound bank-transfer service, built to show the mechanisms a payment rail needs to move
money exactly once and to recover when the partner bank misbehaves.** It is a bench, not a
product. The bank on the other end is simulated (`src/PaymentsBench.PartnerSimulator`), the
payment schemes are data (`scenarios/schemes.yaml`), and nothing here has ever talked to a real
bank: that is row R1 of the [register](REGISTER.md), and it is the first thing to know.

## Four kinds of document, and which one you want

The documentation is sorted by the [Diátaxis](https://diataxis.fr/) split: are you doing
something or learning something, and do you need steps or understanding?

| | Steps | Understanding |
|---|---|---|
| **Learning** | Tutorial: walk one transfer through its life | Explanation: why a payment rail is built this way |
| **Doing** | How-to: run it, change a scheme, add a scenario | Reference: states, postings, endpoints, decisions |

### Tutorial: "I have never run this before"

1. [**Run it, and walk a transfer through its life**](../README.md#run-it): start the API and the
   partner simulator, initiate a payment, make the simulated partner time out after accepting,
   and watch the resolver recover it without a duplicate. No database, no credentials.

### How-to: "I know what I want to do"

- **Run the tests**: `dotnet test` from the repository root (in-memory stores, no database).
- **Run the whole system** with PostgreSQL and the dashboard: `dotnet run --project src/PaymentsBench.AppHost`.
- **Change a scheme's cut-off or settlement days**: edit [`scenarios/schemes.yaml`](../scenarios/schemes.yaml);
  scheme rules are data, never branches in code.
- **Add a fault scenario**: add a file under [`scenarios/`](../scenarios/), following
  [`scenarios/README.md`](../scenarios/README.md). Today the files are checked for shape, not
  executed (register row R3).
- **Make the simulated partner misbehave**: `PUT /faults` on the simulator with one of `Latency`,
  `TimeoutAfterAccept`, `ServerError`, `RejectAll`, `StatementDrift` (`src/PaymentsBench.PartnerSimulator/Program.cs`).

### Explanation: "I want to understand why"

- [**OVERVIEW.md**](OVERVIEW.md): the whole shape in one sitting. Why a payment rail is hard, the
  money path, the seven rules, every mechanism, what the tests and the walk-through caught, and
  what is honestly not ready. The source of truth the PDF edition and the site are built from.
- [**FINDINGS.md**](FINDINGS.md): what building and testing this bench caught, unflattering on
  purpose.
- [**ARCHITECTURE.md**](ARCHITECTURE.md): the shape and the seven rules that matter, short.

### Reference: "I need the exact facts"

- [**REGISTER.md**](REGISTER.md): what is not proved, with dates and closing conditions.
- [**adr/**](adr/): decisions already taken, one record each.
- [**scenarios/README.md**](../scenarios/README.md): the scenario classes and what each proves.
- The state machine is one table, `PaymentTransitions` in
  `src/PaymentsBench.Domain/Payments/PaymentTransitions.cs`; the ledger postings are one file,
  `src/PaymentsBench.Domain/Ledger/Postings.cs`; the HTTP surface is
  `src/PaymentsBench.Api/Program.cs`.

## Reading order for a reviewer

1. [README](../README.md), for what it does and how to run it.
1. [OVERVIEW](OVERVIEW.md) sections 1 to 5, for the argument.
1. [REGISTER](REGISTER.md), for what the argument does not yet cover.
1. [FINDINGS](FINDINGS.md), for what went wrong while building it.
1. The code, which is small: start at `src/PaymentsBench.Api/Program.cs`, the manifest.
