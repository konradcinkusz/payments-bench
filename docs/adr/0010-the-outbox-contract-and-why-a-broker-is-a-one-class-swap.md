# ADR 0010: The outbox contract, and why a broker is a one-class swap

**Status:** accepted, 2026-10-13

## Context

Events leave the service through the outbox (architecture rule 5): written in the same
transaction as the state change, read later by a dispatcher. The dispatcher published to a log
line. "At-least-once, and consumers dedupe on the message id" was a design claim that nothing
tested, and a real broker is out of scope for this bench (register row R5). No event bus is
introduced until a use case needs one (reference architecture section 4).

## Decision

- `IEventPublisher` stays the only seam. `OutboxBatchDispatcher.DispatchBatchAsync` is one pass
  over the outbox, called by the `OutboxDispatcher` background service on a timer and by tests
  directly. It publishes undispatched rows in `OccurredAt` order and marks a row dispatched only
  after its publish returned.
- `ChannelEventPublisher` publishes an `OutboxEnvelope(MessageId, Type, Payload)` onto a bounded
  in-process `System.Threading.Channels` channel. `Outbox:Publisher` selects `log` (default,
  unchanged behaviour) or `channel`; each implementation is one registration line in
  `AddOutboxPublisher` (P10), and an unknown value stops start-up naming the valid ones (P8).
  `channel` also runs a `DedupingConsumer` in the host.
- `DedupingConsumer` is the consumer's side of the contract: it keeps its own record of applied
  message ids (`IProcessedMessageStore`, in memory here; a real consumer keeps it in its own
  database), invokes the handler once per id and counts the duplicates it drops. The id is
  recorded after the handler returned.

## What an `IEventPublisher` must honour

- `PublishAsync` returns only once the destination holds the message (a broker acknowledgement,
  not a socket write). The dispatcher marks the row dispatched right after; a publisher that
  returns early turns a crash into a lost event.
- The message id is the outbox row id and reaches the consumer unchanged. It is the consumer's
  dedupe key; a publisher that mints its own id defeats the contract.
- Messages are offered in `OccurredAt` order. Ordering is best effort: a redelivery, a retry or
  a partitioned broker can reorder, and consumers must not rely on it.
- A failure is an exception. The row stays undispatched and the next batch tries again.

## What it need not do

- Dedupe. Delivery is at-least-once by design: if the process dies between the publish and the
  dispatched mark, the same message is published again. Deduping belongs to the consumer.
- Be transactional with the database, order across payments, or retry internally.
- Be durable in the channel implementation. The channel loses what it holds on restart; it
  exists to make the contract testable, not to carry production traffic.

## In-memory (degraded) mode has no dispatcher

Without a `payments` connection string the service runs on in-memory stores and registers no
outbox dispatcher and no publisher at all; events enqueued there are never read. `Outbox:Publisher`
has no effect in that mode.

## Consequences

- The contract is proved in process by `OutboxContractTests` (delivery once with the row id as
  message id, a lost dispatched mark leading to a republish that the consumer applies once and
  counts as one duplicate, `OccurredAt` order, nothing published for a failed commit),
  `DedupingConsumerTests` and `OutboxPublisherSelectionTests`.
- A broker implementation is one class behind `IEventPublisher` plus one registration case. It
  does not touch the dispatcher, the outbox or the consumer's dedupe.
- R5 is narrowed, not closed: no broker has ever carried an event. The channel cannot show broker
  acknowledgement semantics, redelivery timing, partitioning or consumer crashes between
  receive and apply.
