# Architecture decision records

One file per decision, numbered in the order the decisions were taken: `NNNN-short-title.md`.
A record states its status and date, the context, the decision and its consequences.

- A new decision is a new record, never an edit to an old one. A decision that replaces another
  says so in its own status line, and the old record's status is the only line that changes.
- A pull request that takes a decision adds its record at the next free number on `main` at
  merge time.
- The directory listing is the index; this file deliberately does not repeat it, so it cannot go
  stale. [`../OVERVIEW.md`](../OVERVIEW.md) section 17 summarises the records that have merged.
