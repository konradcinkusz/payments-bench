# ADR 0012: ISO 20022 at the edge, one internal model

**Status:** accepted, 2026-10-13

## Context

Register row R2: no ISO 20022 message was parsed or produced. Initiation was a JSON body and the
statement a JSON reduction of a camt.053. Banks speak pain.001 and camt.053; the application should not.
Reference architecture P11 says an external dialect is normalized into the one internal model at the
boundary, once, and nothing downstream knows there was more than one dialect.

## Decision

- **A project of its own.** `src/PaymentsBench.Iso20022` holds the whole mapping. It references
  Application (for `InitiatePayment`, `PartnerTransfer`, `Statement`) and nothing else, and uses
  `System.Xml.Linq` with no third-party package. Api references it for `POST /payments/pain001`;
  Infrastructure references it for the statement. Domain, the Application commands and the records are
  unchanged: the dialect ends at this project's API.
- **A hardened loader.** Every document enters through `SecureXml`: `DtdProcessing.Prohibit`
  (ISO 20022 messages carry no DTD, so no entity is ever declared), `XmlResolver = null`, a byte limit
  enforced while reading the stream (the endpoint caps its body at 1 MiB, a statement at 10 MiB) and
  `MaxCharactersInDocument`. A refused document is a `MessageFormatException` carrying the element path.
- **pain.001.001.09 in.** One `InitiatePayment` per `CdtTrfTxInf`: end-to-end id, `InstdAmt` and `Ccy`,
  the debtor IBAN from `PmtInf/DbtrAcct`, the creditor IBAN, and the scheme from `PmtTpInf/SvcLvl/Cd`.
  The idempotency key is `MsgId/PmtInfId/EndToEndId`, so posting the same file again replays instead of
  duplicating. A document is read whole or refused whole. The amount has the limits of the JSON body
  (positive, two decimals).
- **The service-level mapping is one table**, `ServiceLevels` (`SEPA` to `sepa`). It is code, not
  `scenarios/` YAML, because reading YAML would need a package this project does not take; the table is
  one place, and an unmapped value is a validation error naming the element, never a guess. Moving it to
  data is a change to that one class.
- **camt.053.001.08 in.** The account is `Acct/Id/IBAN` or `Acct/Id/Othr/Id`. Each `TxDtls` is one
  `StatementLine`; an entry without details is one line with end-to-end id `NOTPROVIDED`, the value ISO
  20022 prescribes for an absent one. Reversal entries are refused rather than guessed at.
- **The statement date is the date written in `Stmt/CreDtTm`**, not the entries' `BookgDt/Dt` and not
  converted to UTC. It is one mandatory field with no inference. It costs little because reconciliation
  matches on the date it asked for and on each line's own booking date; `Statement.Date` is informational.
  A real bank creates a statement after the day it describes, so the date can be the next morning's;
  the simulator writes the requested day.
- **Writers for both shapes**, `Pain001Writer` (from `PartnerTransfer`s, one block per scheme and debtor
  account) and `Camt053Writer` (from `Statement`), so each message can be tested read, written, read.
- **The simulator renders camt.053 itself.** `StatementXml` in the simulator shares no code with the
  reader; the reader is tested against it, so the partner and our parser are not the same code. The
  simulator answers `GET /statements/{date}` with XML when the caller accepts `application/xml`.
  `HttpStatementSource` asks for it when `Partner:StatementFormat` is `camt053`; the default is `json`.
- **Per-transaction results.** `POST /payments/pain001` answers 200 with a result per transaction (the
  payment, or the problem the handler raised). Each transaction is its own unit of work, so one refusal
  does not undo the others. An unreadable document is a 400 problem with an `element` member; an oversize
  one 413; any content type but `application/xml` 415. `ProblemClassifier` is the one table of exception
  to status, shared by the exception handler and this endpoint.

## Consequences

- A second dialect (another pain.001 version, pain.008) is another reader and table row in this project;
  nothing else changes.
- The samples are ours. They are not validated against the official XSDs and none came from a bank
  (R18); the readers accept what we believe the schema says.
- The gateway still submits JSON to the simulated partner. pain.001 is read at our API and goes no further.
