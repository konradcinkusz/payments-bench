# ADR 0002: Money is a double-entry, append-only ledger

**Status:** accepted, 2026-10-13

## Context

A balance column on a payment or an account is easy to write and impossible to audit: a wrong
number has no history. Customer funds are safeguarded, so the question "where is this money
right now" must have one answer that can be derived, not one that was last written.

## Decision

Every money movement is a `JournalEntry` with at least two `JournalLine`s that balance per
currency. Entries are appended, never updated or deleted; a correction is another entry. Balances
are sums over lines. The accounts are `customer-funds`, `pending-outbound` and `nostro`, and the
postings for each payment event are in one file, `Postings`.

## Consequences

- The ledger tests can prove that a returned payment nets every account to zero, which is the
  property that matters and is unprovable with balance columns.
- `BalanceAsync` is a sum over rows. At volume that needs a materialised balance maintained by
  the same entries; the entries stay the source of truth either way.
