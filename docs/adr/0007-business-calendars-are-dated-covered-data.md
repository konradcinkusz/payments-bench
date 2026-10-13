# ADR 0007: Business calendars are dated, covered data

**Status:** accepted, 2026-10-13

## Context

The value date of a payment is a business-day calculation, and a wrong holiday is a wrong value
date. The first calendar knew weekends only. Closing days differ by scheme: TARGET2 closes on six
named days, Fedwire follows Federal Reserve holidays with an observed-holiday rule, and Faster
Payments runs every day. Computing Easter or observed holidays in code would put scheme rules in
branches (architecture rule 7) and make a wrong date a code change instead of a data change.

## Decision

- A calendar is a file, `scenarios/calendars/<name>.yaml`: whether weekends are closed, the
  closing days as explicit dates, and the covered range (`coveredFrom`, `coveredTo`, both
  inclusive). Each file cites its public source in a comment. There is no Easter algorithm in code.
- Each scheme in `scenarios/schemes.yaml` names its calendar. Weekends and closing days are not
  inferred from the scheme or the currency.
- `HolidayCalendar` (domain) answers only inside its covered range. Outside it, it throws
  `CalendarCoverageException`: a calendar that does not know a date must not call it a business day.
  A value date that would run past the end of coverage therefore fails loudly too, and extending the
  data is the fix.
- Schemes and calendars load at start-up into an `ISchemeCatalog` (Application port; YamlDotNet
  loader in Infrastructure). The directory is configuration (`Schemes__Directory`), defaulting to the
  `scenarios` folder copied next to the binaries. Scheme data is required: a missing or malformed
  file, or a scheme naming a missing calendar, stops start-up with an error naming the file.
- Initiation refuses an unknown scheme or a currency the scheme does not carry, with a domain
  exception (HTTP 422).

## Consequences

- Adding a holiday, a calendar or a scheme is a data change with a reviewable diff and a source line.
- The data has a shelf life: the shipped calendars end on 2027-12-31, after which value-date
  calculation throws until the files are extended. That is deliberate, and it is a maintenance task.
- Faster Payments is modelled with weekends open and no closing days (the scheme runs 24/7/365).
  Bank-level limits and maintenance windows are not modelled. The partner is simulated.
- Fedwire's Sunday-evening opening is treated as part of Monday's business day, not as a Sunday.
- The calendars are proved, but the computed value date is not yet stamped on the payment at submit
  or acceptance (register R11).
