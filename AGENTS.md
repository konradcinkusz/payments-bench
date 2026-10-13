# Working in this repository

This repository conforms to [architecture-standards](https://github.com/konradcinkusz/architecture-standards);
the plugin is declared in `.claude/settings.json`, so its skills (reference architecture P1-P15,
repo baseline, ticket delivery, research documentation) load with the session. Read, in this order:

1. `README.md` - what the service does and how to run it.
2. `docs/ARCHITECTURE.md` - the shape and the seven rules that matter.
3. `docs/REGISTER.md` - what is not yet proved. A change that closes a row moves it to *Closed*
   with the date; a change that opens a gap adds a row. The register is never silently wrong.
4. `docs/adr/` - decisions already taken. A new decision is a new record, not an edit.

Rules of the road:

- .NET 10. `dotnet build -warnaserror` and `dotnet test` must pass before a push; analyzers are on.
- One pull request per change, against `main`, with CI green before merge. Describe the decision
  behind the change and cite the principle it serves.
- Scheme rules, scenarios and calendars are data under `scenarios/`, never branches in code.
- Nothing in this repository talks to a real bank. The partner is `src/PaymentsBench.PartnerSimulator`;
  say "simulated" wherever a reader could assume otherwise.
- No secrets in the tree, ever; `.env*` is ignored and secret scanning runs in CI.
