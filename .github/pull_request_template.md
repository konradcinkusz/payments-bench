# Pull request

## Decision

<!-- What changed and why: the decision behind the change, not a restatement of the diff. -->

## Principle served

<!-- The reference-architecture principle (P1-P15) or guide section this serves. -->

## Verification

<!-- What you ran and what it showed. Paste summaries, not claims. -->

## Open / not proved

<!-- Anything left open. A change that closes a docs/REGISTER.md row moves it to Closed with
     the date; a change that opens a gap adds a row. -->

## Checks

- [ ] `dotnet build -warnaserror` and `dotnet test` pass
- [ ] CI is green
- [ ] No secrets; nothing talks to a real bank (the partner is simulated)
- [ ] `docs/REGISTER.md` updated if a gap was closed or opened
