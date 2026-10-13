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

`Submitted` is committed before the partner call; the three submit outcomes (Accepted, Rejected, Unknown) and the resolver, which asks by end-to-end id and resubmits, with the same id, only on `PartnerStatus.NotFound`. An unreachable partner is `PartnerStatus.Unavailable` (`ResolutionOutcome.PartnerUnavailable`), never `NotFound`; a resubmit with no answer is `ResolutionOutcome.StillUnknown`. The resolver runs on request (`POST /payments/{id}/resolve`) and on a schedule (`SubmittedPaymentSweeper`, then `SweepSubmittedPaymentsHandler`). The partner is the simulated bank. Source: `SubmitPayment.cs`, `ResolveSubmittedPayment.cs`, `SweepSubmittedPayments.cs`, `HttpPartnerGateway.cs`, `PartnerResilience.cs` and `src/PaymentsBench.Infrastructure/Sweeping/`.

```mermaid
sequenceDiagram
    participant C as Client
    participant A as API
    participant H as SubmitPaymentHandler
    participant W as SubmittedPaymentSweeper
    participant SW as SweepSubmittedPaymentsHandler
    participant R as ResolveSubmittedPaymentHandler
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
        P--xG: timeout, dropped connection, 5xx or open circuit
        G-->>H: SubmitOutcome.Unknown
        Note over H,S: Nothing is written. The payment stays Submitted.
    end
    A-->>C: payment as it now stands

    Note over C,P: Later, recovery of a payment still Submitted: by request or by the sweep
    C->>A: POST /payments/id/resolve
    A->>R: HandleAsync(ResolveSubmittedPayment)
    W->>SW: every Sweeper:Interval, HandleAsync(SweepSubmittedPayments)
    SW->>S: ListSubmittedBeforeAsync (older than Sweeper:MinimumAge, oldest first, up to Sweeper:BatchSize)
    SW->>R: ISubmittedPaymentResolver.ResolveAsync, one DI scope per payment
    Note over SW,R: A resolve that throws is recorded in SweepResult.Failures and the batch goes on
    R->>S: GetAsync, not Submitted gives ResolutionOutcome.AlreadyResolved
    R->>G: GetStatusAsync(end-to-end id)
    G->>P: GET /transfers/end-to-end id (retried on transport error, attempt timeout or 5xx, never an open circuit, never a POST)
    alt PartnerStatus.Unavailable
        P--xG: open circuit, timeout, transport failure or 5xx
        G-->>R: PartnerStatus.Unavailable (never NotFound)
        Note over R,S: Nothing is written. ResolutionOutcome.PartnerUnavailable, payment stays Submitted, a later resolve or sweep asks again.
    else PartnerStatus.NotFound
        P-->>G: 404
        G-->>R: PartnerStatus.NotFound
        R->>G: SubmitAsync with the same end-to-end id
        G->>P: POST /transfers
        alt resubmit Accepted or Rejected
            P-->>G: 2xx or 422
            R->>S: MarkAccepted, or MarkRejected plus Postings.Release, CommitAsync
        else resubmit Unknown
            P--xG: timeout, dropped connection, 5xx or open circuit
            G-->>R: SubmitOutcome.Unknown
            Note over R,S: Nothing is written. ResolutionOutcome.StillUnknown, payment stays Submitted.
        end
    else Accepted, Settled or Rejected
        P-->>G: status
        R->>S: apply it (Settled posts Postings.Settle, Rejected posts Postings.Release), CommitAsync
    else Returned
        P-->>G: status Returned
        R-->>A: InvalidOperationException, nothing written
    end
    SW-->>W: SweepResult (Examined, Outcomes by ResolutionOutcome, Failures, oldest Submitted age)
    R-->>A: ResolutionOutcome (Accepted, Settled, Rejected, AlreadyResolved, StillUnknown, PartnerUnavailable)
```

### A4. Outbox and inbox

Events are written in the same commit as the state change; `OutboxDispatcher` (after `MigrationGate`) publishes at least once through the `IEventPublisher` that `Outbox:Publisher` selects: `log` (a log line, the default) or `channel` (a bounded in-process channel read by `DedupingConsumer`, which drops a repeated message id). Register R5 is narrowed, not closed: no broker has carried an event. Partner notifications are deduplicated by notification id in the inbox. Source: `OutboxDispatcher.cs`, `OutboxBatchDispatcher.cs`, `OutboxRegistration.cs`, `DedupingConsumer.cs`, `MigrationGate.cs` and `ApplyPartnerNotification.cs`.

```mermaid
flowchart TB
    subgraph W["Write side: one CommitAsync per state change"]
        H["handler<br/>Initiate, Submit, Resolve, ApplyPartnerNotification"]
        C["IUnitOfWork.CommitAsync<br/>payment row, journal entry and outbox row<br/>in the same commit"]
        H --> C
        C --> OB[("outbox table<br/>IOutbox.EnqueueAsync<br/>DispatchedAt is null")]
    end

    subgraph D["OutboxDispatcher (PostgreSQL mode only)"]
        GATE["MigrationGate.WaitAsync<br/>no batch before the schema is ready"]
        POLL["every 2 s: OutboxBatchDispatcher.DispatchBatchAsync<br/>up to 100 undispatched messages, oldest OccurredAt first"]
        PUB["IEventPublisher.PublishAsync<br/>chosen by Outbox:Publisher"]
        MARK["set DispatchedAt, SaveChangesAsync<br/>after the publish returned"]
        GATE --> POLL --> PUB --> MARK
    end

    OB --> POLL
    PUB -- "log (default)" --> LOG["LoggingEventPublisher: a log line<br/>nothing consumes it"]
    PUB -- "channel" --> CH["ChannelEventPublisher<br/>bounded in-process channel, not durable<br/>stands in for a broker"]
    MARK -. "a crash before this line<br/>republishes the message with the same id:<br/>at least once" .-> POLL

    subgraph DC["Consumer, Outbox:Publisher = channel only"]
        DEDUP{"DedupingConsumer<br/>IProcessedMessageStore.Contains<br/>message id = outbox row id"}
        HAND["IOutboxMessageHandler.HandleAsync<br/>then the id is recorded"]
        DROP["duplicate dropped and counted<br/>DuplicatesDropped"]
        DEDUP -- "new" --> HAND
        DEDUP -- "seen" --> DROP
    end

    CH --> DEDUP
    NOBROKER["No broker has carried an event:<br/>register R5 is narrowed, not closed"]
    LOG -.-> NOBROKER
    CH -.-> NOBROKER

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

A value date's statement against the payments settled on that value date, whatever happened to them afterwards (`Settled` and `Returned`, finding F-4 fixed): matches are counted, and the three `BreakKind` values become a work queue. The statement is JSON or camt.053 as `Partner:StatementFormat` says. A return has no statement line of its own (register R18). Source: `src/PaymentsBench.Application/Reconciliation/ReconciliationService.cs` and `src/PaymentsBench.Infrastructure/Partner/HttpStatementSource.cs`.

```mermaid
flowchart TB
    RUN["POST /reconciliation/runs/date<br/>ReconciliationService.ReconcileAsync"]
    ST["statement for the value date<br/>IStatementSource.GetAsync (HttpStatementSource)<br/>from the partner simulator (simulated bank)<br/>GET /statements/date"]
    FMT["Partner:StatementFormat<br/>json (default) or camt053<br/>camt.053 is read by PaymentsBench.Iso20022"]
    SP["payments settled on that value date<br/>IPaymentRepository.ListByStatusAsync<br/>Settled plus Returned, filtered on ValueDate<br/>a later return does not move the settlement day (F-4 fixed)"]

    RUN --> ST
    FMT -.-> ST
    RUN --> SP
    ST --> DEB["Debit lines only, matched by end-to-end id"]
    SP --> DEB

    DEB --> Q1{"statement line has a<br/>payment settled that day?"}
    Q1 -- "no" --> B1["break OnStatementOnly<br/>StatementAmount set, LedgerAmount empty"]
    Q1 -- "yes" --> Q2{"same amount<br/>and currency?"}
    Q2 -- "no" --> B3["break AmountMismatch<br/>both amounts on the row"]
    Q2 -- "yes" --> M["matched, counted"]
    DEB --> Q3{"payment settled that day<br/>missing from the statement?"}
    Q3 -- "yes" --> B2["break InLedgerOnly<br/>LedgerAmount set, StatementAmount empty"]

    B1 --> WQ["ReconciliationReport: Matched count<br/>plus Breaks, a work queue and not an alert"]
    B2 --> WQ
    B3 --> WQ
    M --> WQ
    WQ -.-> R18["Not proved, register R18: a return has no<br/>statement line of its own, so the return day<br/>cannot reconcile"]
```

### A6. Delivery topology

What `AppHost` composes (PostgreSQL, the simulated partner, the API, authservice when `Parameters:auth-signing-key` is set, the Aspire dashboard over OTLP), the Fly.io estate as `flyio/` declares it (declared, never deployed: register R14) and the degraded in-memory mode when there is no `payments` connection string. Source: `src/PaymentsBench.AppHost/AppHost.cs`, `flyio/*.fly.toml` and `InfrastructureRegistration.cs`.

```mermaid
flowchart TB
    subgraph APPHOST["PaymentsBench.AppHost (Aspire composition root)"]
        PG[("postgres<br/>AddPostgres with data volume<br/>databases: payments, authdb")]
        PART["partner<br/>PaymentsBench.PartnerSimulator<br/>partner simulator (simulated bank)"]
        API["api<br/>PaymentsBench.Api<br/>Partner__BaseAddress=http://partner"]
        AUTH["authservice (container, pinned tag)<br/>declared only when Parameters:auth-signing-key is set<br/>otherwise the api runs its Development policy, anonymous allowed"]
        DASH["Aspire dashboard<br/>traces, metrics, logs over OTLP"]
    end

    API -- "ConnectionStrings:payments<br/>WithReference, WaitFor" --> PG
    API -- "HTTP: /transfers, /statements<br/>WithReference, WaitFor" --> PART
    API -- "Auth__Authority: JWKS, JwtBearer<br/>WaitFor" --> AUTH
    AUTH -- "ConnectionStrings:authdb<br/>WaitFor" --> PG
    API -. "OTLP" .-> DASH
    PART -. "OTLP" .-> DASH
    CLI["client, curl"] --> API
    CLI -- "PUT /faults, settle, return" --> PART

    subgraph FLY["Fly.io, region fra, flyio/*.fly.toml: declared, never deployed (register R14)"]
        direction TB
        FAPI["payments-bench-api<br/>flyio/api.fly.toml, public, force_https<br/>health check GET /health<br/>Partner__BaseAddress=http://payments-bench-partner.internal:8080"]
        FAUTH["payments-bench-authservice<br/>flyio/authservice.fly.toml, public<br/>health check GET /health/ready"]
        FPART["payments-bench-partner<br/>flyio/partner.fly.toml<br/>partner simulator (simulated bank), in-memory state<br/>reached over .internal; no public address expected (R26)"]
        FPG[("payments-bench-postgres<br/>flyio/postgres.fly.toml<br/>postgres:17-alpine, volume pgdata<br/>databases: payments, authservice")]
        FAPI -- "Auth__Authority https" --> FAUTH
        FAPI -- "6PN, .internal" --> FPART
        FAPI -- "payments database" --> FPG
        FAUTH -- "authservice database" --> FPG
    end

    subgraph DEG["Degraded mode: no payments connection string"]
        direction TB
        APID["api, run alone with dotnet run<br/>in-memory repository, ledger, outbox, inbox<br/>no migration, no OutboxDispatcher<br/>SubmittedPaymentSweeper still runs (MigrationGate.Completed)<br/>warning in the log at start-up"]
        PARTD["partner simulator (simulated bank)<br/>dotnet run, port 5101"]
        APID -- "HTTP, port 5100 to 5101" --> PARTD
    end
```
