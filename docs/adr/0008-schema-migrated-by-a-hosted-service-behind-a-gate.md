# ADR 0008: The schema is migrated by a hosted service behind a gate

**Status:** accepted, 2026-10-13

## Context

The PostgreSQL stores created their schema with `EnsureCreated`, which cannot evolve a database
that already exists and records nothing about what was applied (reference architecture P4).
Running the migration inside `Program.cs` before `app.Run()` would hold the process
unresponsive while it works, so an orchestrator cannot tell "migrating" from "dead". And any
other background service that touches the database races the schema if it starts first.

## Decision

- The schema is the EF Core migration `InitialSchema`, generated with a pinned local `dotnet-ef`
  (`.config/dotnet-tools.json`) through a design-time factory whose connection string carries no
  password. `EnsureCreated` appears nowhere outside test code, and tests apply `MigrateAsync` too.
- `MigrationHostedService` is a `BackgroundService` that waits for `ApplicationStarted` and then
  calls `MigrateAsync`, so `/alive` answers while it works.
- `MigrationGate`, a singleton over a `TaskCompletionSource`, completes when the migration
  finishes and faults if it fails. `OutboxDispatcher` awaits it before its first batch; a later
  hosted service does the same. In-memory mode registers an already-completed gate.
- A health check on `/health` is unhealthy until the gate completes, and stays unhealthy if it
  faulted. `/alive` is not affected.
- A failed migration is logged at critical level and faults the gate; the process stays up and
  unhealthy instead of crash-looping, and the dispatcher exits quietly instead of writing to a
  schema it cannot trust.

## Consequences

- The first request after a deploy can arrive before the schema is ready; readiness (`/health`)
  is what keeps traffic away, not start order.
- `MigrationTests` fails the build if the model changes without a migration
  (`HasPendingModelChanges`).
- The `Migrations` folder is treated as generated code by `.editorconfig`; it is regenerated,
  not edited.
- Several instances migrating at once rely on EF Core's PostgreSQL migration lock; a run of many
  instances is not exercised here.
