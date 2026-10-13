# Diagrams

Every structural claim in this repository as a picture, in Mermaid, rendered by GitHub with no build step.
Each diagram names the source it reflects; when the code and a picture disagree, the code is right and
the picture is a bug. Nothing here talks to a real bank: the partner is the simulator, drawn as
"partner simulator (simulated bank)".

Each diagram exists twice, byte-identical: inline here (the form GitHub renders) and as a file in
[`docs/diagrams/`](diagrams/) (the form that can be rendered to PDF). The section id is the join key
(`### A1.` owns `a1-*.mmd`), and `npm run lint:diagrams` ([`scripts/check-diagrams.mjs`](../scripts/check-diagrams.mjs))
fails when they differ.

## The diagrams

### A1. Money path

Where customer money sits and which payment event moves it: reserve on initiation, release on rejection, settle to the nostro, reverse on return. The accounts are `LedgerAccounts` and the movements are the four entries in `Postings` (`src/PaymentsBench.Domain/Ledger/Postings.cs`).

```mermaid
flowchart LR
    CF["customer-funds<br/>LedgerAccounts.CustomerFunds"]
    PO["pending-outbound<br/>LedgerAccounts.PendingOutbound"]
    NO["nostro<br/>LedgerAccounts.Nostro"]

    CF -- "initiate: Postings.Reserve<br/>debit customer-funds, credit pending-outbound" --> PO
    PO -- "reject: Postings.Release<br/>debit pending-outbound, credit customer-funds" --> CF
    PO -- "settle: Postings.Settle<br/>debit pending-outbound, credit nostro" --> NO
    NO -- "return: Postings.Reverse<br/>debit nostro, credit customer-funds" --> CF

    T1["InitiatePaymentHandler<br/>status Initiated"] -.-> CF
    T2["Rejected, from SubmitPaymentHandler,<br/>ResolveSubmittedPaymentHandler<br/>or a Rejected notification"] -.-> PO
    T3["Settled, from ResolveSubmittedPaymentHandler<br/>or a Settled notification"] -.-> PO
    T4["Returned, from a Returned notification<br/>only after Settled"] -.-> NO
```

### A2. Payment state machine

The six `PaymentStatus` values and every move `PaymentTransitions` allows, nothing more. `Rejected` and `Returned` are terminal. Source: `src/PaymentsBench.Domain/Payments/PaymentTransitions.cs`.

```mermaid
stateDiagram-v2
    [*] --> Initiated : Payment.Initiate
    Initiated --> Submitted : MarkSubmitted
    Submitted --> Accepted : MarkAccepted
    Submitted --> Rejected : MarkRejected
    Submitted --> Settled : MarkSettled
    Accepted --> Settled : MarkSettled
    Accepted --> Rejected : MarkRejected
    Settled --> Returned : MarkReturned
    Rejected --> [*] : terminal, accepts nothing
    Returned --> [*] : terminal, accepts nothing

    note right of Submitted
        Committed before the partner call.
        An unknown outcome stays here
        until the resolver asks the partner.
    end note
```

### A3. Submit and resolve

`Submitted` is committed before the partner call; the three submit outcomes (Accepted, Rejected, Unknown) and the resolver, which asks by end-to-end id and resubmits, with the same id, only on `NotFound`. The partner is the simulated bank. Source: `SubmitPayment.cs`, `ResolveSubmittedPayment.cs` and `HttpPartnerGateway.cs`.

```mermaid
sequenceDiagram
    participant C as Client
    participant A as API
    participant H as SubmitPaymentHandler
    participant S as Store, IOutbox, IUnitOfWork
    participant G as HttpPartnerGateway
    participant P as partner simulator (simulated bank)

    C->>A: POST /payments/id/submit
    A->>H: HandleAsync(SubmitPayment)
    H->>S: MarkSubmitted, enqueue PaymentSubmitted
    H->>S: CommitAsync (Submitted is durable)
    H->>G: SubmitAsync(PartnerTransfer)
    G->>P: POST /transfers (sent once, never retried)
    alt Accepted
        P-->>G: 2xx with partner reference
        G-->>H: SubmitOutcome.Accepted
        H->>S: MarkAccepted, enqueue event, CommitAsync
    else Rejected
        P-->>G: 422 with reason code
        G-->>H: SubmitOutcome.Rejected
        H->>S: MarkRejected, Postings.Release, enqueue event, CommitAsync
    else Unknown
        P--xG: timeout, dropped connection or 5xx
        G-->>H: SubmitOutcome.Unknown
        Note over H,S: Nothing is written. The payment stays Submitted.
    end
    A-->>C: payment as it now stands

    Note over C,P: Later, recovery of a payment still Submitted
    C->>A: POST /payments/id/resolve
    A->>H: ResolveSubmittedPaymentHandler.HandleAsync
    H->>G: GetStatusAsync(end-to-end id)
    G->>P: GET /transfers/end-to-end id (idempotent, retried)
    alt NotFound
        P-->>G: 404
        G-->>H: PartnerStatus.NotFound
        H->>G: SubmitAsync with the same end-to-end id
        G->>P: POST /transfers
        P-->>G: Accepted or Rejected
        H->>S: MarkAccepted or MarkRejected plus Postings.Release, CommitAsync
    else Accepted, Settled or Rejected
        P-->>G: status
        H->>S: apply it (Settled posts Postings.Settle, Rejected posts Postings.Release), CommitAsync
    end
```

### A4. Outbox and inbox

Events are written in the same commit as the state change; `OutboxDispatcher` publishes at least once, to a log line today (register R5: no broker is wired); partner notifications are deduplicated by notification id in the inbox. Source: `OutboxDispatcher.cs` and `ApplyPartnerNotification.cs`.

```mermaid
flowchart TB
    subgraph W["Write side: one CommitAsync per state change"]
        H["handler<br/>Initiate, Submit, Resolve, ApplyPartnerNotification"]
        C["IUnitOfWork.CommitAsync<br/>payment row, journal entry and outbox row<br/>in the same commit"]
        H --> C
        C --> OB[("outbox table<br/>IOutbox.EnqueueAsync<br/>DispatchedAt is null")]
    end

    subgraph D["OutboxDispatcher (PostgreSQL mode only)"]
        POLL["every 2 s: take up to 100 undispatched<br/>messages, oldest OccurredAt first"]
        PUB["IEventPublisher.PublishAsync"]
        MARK["set DispatchedAt, SaveChangesAsync"]
        POLL --> PUB --> MARK
    end

    OB --> POLL
    PUB --> LOG["LoggingEventPublisher: a log line today<br/>no broker wired, register R5"]
    MARK -. "a crash before this line<br/>republishes the message:<br/>at least once" .-> POLL

    subgraph I["Read side: POST /partner/notifications"]
        N["PartnerNotification<br/>NotificationId, EndToEndId, Kind"]
        T{"IInbox.TryRecordAsync<br/>NotificationId"}
        F{"payment found by<br/>end-to-end id?"}
        APPLY["Applied: Settled, Rejected or Returned<br/>state change, posting, outbox row, one commit"]
        N --> T
        T -- "false" --> DUP["Duplicate: nothing applied"]
        T -- "true" --> F
        F -- "no" --> UNK["UnknownPayment: id recorded, then commit"]
        F -- "yes" --> APPLY
    end

    APPLY --> C
```

### A5. Reconciliation

A value date's statement against the payments settled with that value date: matches are counted, and the three `BreakKind` values become a work queue. Source: `src/PaymentsBench.Application/Reconciliation/ReconciliationService.cs`.

```mermaid
flowchart TB
    RUN["POST /reconciliation/runs/date<br/>ReconciliationService.ReconcileAsync"]
    ST["statement for the value date<br/>IStatementSource.GetAsync<br/>from the partner simulator (simulated bank)<br/>GET /statements/date"]
    SP["settled payments with that value date<br/>IPaymentRepository.ListByStatusAsync Settled<br/>filtered on ValueDate"]

    RUN --> ST
    RUN --> SP
    ST --> DEB["Debit lines only, matched by end-to-end id"]
    SP --> DEB

    DEB --> Q1{"statement line has a<br/>settled payment?"}
    Q1 -- "no" --> B1["break OnStatementOnly<br/>StatementAmount set, LedgerAmount empty"]
    Q1 -- "yes" --> Q2{"same amount<br/>and currency?"}
    Q2 -- "no" --> B3["break AmountMismatch<br/>both amounts on the row"]
    Q2 -- "yes" --> M["matched, counted"]
    DEB --> Q3{"settled payment missing<br/>from the statement?"}
    Q3 -- "yes" --> B2["break InLedgerOnly<br/>LedgerAmount set, StatementAmount empty"]

    B1 --> WQ["ReconciliationReport: Matched count<br/>plus Breaks, a work queue and not an alert"]
    B2 --> WQ
    B3 --> WQ
    M --> WQ
```

### A6. Delivery topology

What `AppHost` composes (PostgreSQL, the simulated partner, the API, the Aspire dashboard over OTLP) and the degraded in-memory mode when there is no `payments` connection string. Only what exists on `main` is drawn. Source: `src/PaymentsBench.AppHost/AppHost.cs` and `InfrastructureRegistration.cs`.

```mermaid
flowchart TB
    subgraph APPHOST["PaymentsBench.AppHost (Aspire composition root)"]
        PG[("postgres<br/>AddPostgres with data volume<br/>database: payments")]
        PART["partner<br/>PaymentsBench.PartnerSimulator<br/>partner simulator (simulated bank)"]
        API["api<br/>PaymentsBench.Api<br/>Partner__BaseAddress=http://partner"]
        DASH["Aspire dashboard<br/>traces, metrics, logs over OTLP"]
    end

    API -- "ConnectionStrings:payments<br/>WithReference, WaitFor" --> PG
    API -- "HTTP: /transfers, /statements<br/>WithReference, WaitFor" --> PART
    API -. "OTLP" .-> DASH
    PART -. "OTLP" .-> DASH
    CLI["client, curl"] --> API
    CLI -- "PUT /faults, settle, return" --> PART

    subgraph DEG["Degraded mode: no payments connection string"]
        direction TB
        APID["api, run alone with dotnet run<br/>in-memory repository, ledger, outbox, inbox<br/>no migration, no OutboxDispatcher<br/>warning in the log at start-up"]
        PARTD["partner simulator (simulated bank)<br/>dotnet run, port 5101"]
        APID -- "HTTP, port 5100 to 5101" --> PARTD
    end
```
