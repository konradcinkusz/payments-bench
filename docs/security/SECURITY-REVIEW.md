# Security review: payments-bench

**Reviewed:** 2026-10-13 · **Method:** [`SECURITY-REVIEW.md`](https://github.com/konradcinkusz/architecture-standards/blob/main/docs/guides/SECURITY-REVIEW.md) in architecture-standards (§1 method, §2 finding format, §3 priorities and ledger, §8 authorization structure, §9 recurring blockers, §10 checklist) and P5 · **Skeleton:** OWASP Top 10 (2021), plus the API-specific items of the OWASP API Security Top 10 (2023)

**Scope:** `main` at `636c29a`: the API, the simulated partner, infrastructure, ServiceDefaults, both Dockerfiles, every workflow, and the deployment slice that merged as PR #18 while this review was in progress (`flyio/*.fly.toml`, `.github/workflows/deploy.yml`, `flyio-scale.yml`, `flyio-destroy.yml`, `docs/DEPLOYMENT.md`; the files that merged are byte-identical to the branch head `124fec1` that was read first) the load slice (PR #19: `load.yml`, `scripts/load-test.sh`, `k6/`) and the sweeper (PR #21).

**What is simulated.** The partner bank is [`PaymentsBench.PartnerSimulator`](../../src/PaymentsBench.PartnerSimulator/Program.cs): an invented API shape with switchable faults, no authentication by design. Nothing here talks to a real bank, moves real money or holds real customer data; account numbers in tests and examples are placeholders. The review nevertheless treats the API as if it fronted a real rail, because the bench exists to show what that takes, and because a review that grades "it is only simulated" leniently proves nothing.

**What this document is not.** A static review. Section 12 says so again at the end, on purpose.

## 1. Summary

- **18 findings** (`SEC-01` to `SEC-18`): 3 at P1, 6 at P2, 6 at P3, 3 at P4. **Fixed in this PR: 4** (SEC-02, SEC-04, SEC-07, and SEC-06 on Fly). **Open: 14**, of which 7 sit in stream A's code (Domain, Application, Infrastructure, endpoint logic) and are register rows with owner "stream A", and 7 are deployment settings, decisions or stream B follow-ups.
- **The sharpest finding was a webhook open to every token holder (SEC-02).** `POST /partner/notifications` falls under the fallback policy, which asks only for an authenticated user, so any caller with a token could post `Rejected` for a payment the partner had accepted and release the customer's reserved funds while the transfer was still on its way. Fixed here: the endpoint now needs a grant on the token (`payments.partner.notify`). The ledger reads and reconciliation runs got the same treatment with `payments.operate` (SEC-04). Neither grant is issued by anything in this repository; until authservice mints them, those four endpoints answer 403 to everyone, which is the safe way to be wrong.
- **The structural gap is that a payment has no owner (SEC-01, SEC-05).** Any authenticated caller can read, list, submit and replay the idempotency key of any other caller's payment. That is a data-model change (stream A's domain and schema) and is a register row, not a patch in this PR. It does not block a **single-operator** deployment as long as who can obtain a token is verified (SEC-03); it blocks any deployment where a second party holds a token.
- **What stays open and blocks deployment:** SEC-01 (until the single-operator condition is documented as a decision), SEC-03 (who can mint a token is unverified here), SEC-10 (the `production` environment must restrict who can run the deploy; the deploy document called that optional until this PR, and the settings themselves are still to be made).
- **Verification.** The fixes carry 42 new test cases (`AuthorizationPolicyTests` 24, `SecurityPostureTests` 11, `RequestLimitsTests` 7); with the endpoint grouping reverted, 10 of them fail. The request cap was also exercised against a real Kestrel (a 200 KB body answers 413). The open findings were reproduced against the real API in a throwaway run with two callers (section 5, SEC-01/05/09; SEC-09's collision stopped reproducing once the concurrency slice merged). Not verified: anything that needs Docker (no daemon in this session) or a deployed estate (nothing is deployed, register R14).

## 2. How this review was done

1. Read every route, the identity wiring, the exception handler, ServiceDefaults, the infrastructure stores, both Dockerfiles and every workflow; then the deployment slice and the load slice once they merged.
1. Walked the OWASP categories in section 3. Every "not applicable" carries the `git grep` that proves it, run on this tree, command and output pasted. Where a grep returns nothing, the scope count at the top of section 3 shows the search is not empty, and for SQL a positive control shows the persistence layer is inside it.
1. Wrote the endpoint x grant matrix (section 6) from the route table and the policy registration, not from the README.
1. Tried the claims. A throwaway run of the real API (Production shape, a test-held JWKS, two callers `alice` and `bob`) produced the evidence quoted in SEC-01, SEC-05 and SEC-09 (re-run after the concurrency slice merged). A real Kestrel on loopback produced the 413 in SEC-07. `dotnet list package --vulnerable --include-transitive` against nuget.org reported none for any of the fifteen projects. The integration suite was not run (it needs Docker; CI runs it).
1. Fixed what is small and in stream B's files (section 9); recorded the rest (register rows, ledger).

The method's own caveat applies: reviews are point-in-time. The continuous complement is already in the tree and is listed under positive findings: CodeQL, gitleaks over full history, Dependabot.

## 3. OWASP walk

Outcome per category: **finding** (see section 5), **clean** (reviewed, nothing found; see section 4), or **N/A** (structural reason and the grep that proves it).

Search scope for every `src/*.cs` grep below:

```text
$ git ls-files 'src/*.cs' | wc -l
89
```

### 3.1 OWASP Top 10 (2021)

| Category | Outcome | Where |
|---|---|---|
| A01 Broken Access Control | **Findings** | SEC-01, 02, 04, 05, 11; deny-by-default is clean (P-1) |
| A02 Cryptographic Failures | Clean, partly N/A | TLS terminates at Fly's proxy (SEC-16 for the residue); JWT in P-2; hashing and passwords N/A (3.3) |
| A03 Injection | Clean, N/A for three sub-classes | SQL, OS command and XSS are N/A with greps in 3.3; XML/XXE became applicable with the ISO 20022 slice and is clean (P-12) |
| A04 Insecure Design | **Findings** | SEC-01, 05, 08, 09 (no owner, no throttle, global identifiers) |
| A05 Security Misconfiguration | **Findings** | SEC-06, 07, 13, 16; positives P-3, P-6, P-7 |
| A06 Vulnerable and Outdated Components | Clean | `dotnet list package --vulnerable` reports none; Dependabot and CodeQL present (P-9); image and action pins in SEC-15 |
| A07 Identification and Authentication Failures | Clean, one precondition open | JWT validation is strict (P-2); who can obtain a token is SEC-03 |
| A08 Software and Data Integrity Failures | **Findings** (CI/CD only) | SEC-10, 14, 15; deserialization N/A (3.3) |
| A09 Security Logging and Monitoring Failures | **Finding** | SEC-12 (no caller identity on money-moving actions); log content is clean (P-8) |
| A10 Server-Side Request Forgery | Clean | no outbound address is caller-controlled (3.3) |

### 3.2 OWASP API Security Top 10 (2023)

| Item | Outcome | Where |
|---|---|---|
| API1 Broken Object Level Authorization | **Finding** | SEC-01, SEC-05 |
| API2 Broken Authentication | Clean | P-2; token issuance SEC-03 |
| API3 Broken Object Property Level Authorization (mass assignment, excess exposure) | Clean | P-5: requests bind explicit records, responses are explicit views |
| API4 Unrestricted Resource Consumption | **Findings** | SEC-07 (fixed), SEC-08 (rate limiting), SEC-17 |
| API5 Broken Function Level Authorization | **Findings** | SEC-02, SEC-04 (fixed), SEC-11 |
| API6 Unrestricted Access to Sensitive Business Flows | **Finding** | SEC-08, SEC-05, SEC-09 (initiate and submit are unthrottled and their identifiers squattable) |
| API7 Server-Side Request Forgery | Clean | 3.3 |
| API8 Security Misconfiguration | **Findings** | as A05 |
| API9 Improper Inventory Management | Clean | one API version, no legacy routes; the route table is section 6; OpenAPI is behind the token outside Development (P-3) |
| API10 Unsafe Consumption of APIs | **Finding** | SEC-18 (the partner's responses are trusted without bounds) |

### 3.3 Justified "not applicable" results, with the grep

**SQL injection (A03).** Persistence is EF Core with LINQ and parameterized queries; there is no raw SQL anywhere in `src`. The control shows the persistence layer is in the search scope.

```text
$ git grep -nE 'FromSql|ExecuteSql|SqlQuery|SqlCommand|NpgsqlCommand|CommandText|migrationBuilder\.Sql' -- 'src/*.cs'
(no output)
```

```text
$ git grep -c 'DbSet<' -- 'src/*.cs'
src/PaymentsBench.Infrastructure/Persistence/PaymentsDbContext.cs:5
```

*Preventive guidance, for the pull request that makes this false:* use `FromSql`/`ExecuteSql` with interpolated parameters (they parameterize), never `FromSqlRaw` with concatenation; keep a migration's `Sql(...)` to constants.

**OS command injection (A03).** No process is started by any service.

```text
$ git grep -nE 'Process\.Start|ProcessStartInfo|Runtime\.Exec' -- 'src/*.cs'
(no output)
```

*If one is ever added:* no shell, an argument list not a string, an allow-list for the executable, no caller input in arguments.

**XML and XXE (A03): applicable, reviewed, clean.** This was a justified N/A when the review began (the one structured format loaded was YAML). The ISO 20022 slice (PR #24, R2 closed) added `POST /payments/pain001` and a `camt.053` reader, so XML is now parsed and the category moved from N/A to a positive finding (P-12). The only door into the parser is `SecureXml`, which refuses a DTD outright, attaches no resolver, caps entity expansion at zero characters and bounds the size before the parser sees a byte:

```text
$ git grep -nE 'DtdProcessing|XmlResolver|MaxCharactersFromEntities|MaxCharactersInDocument' -- 'src/*.cs'
src/PaymentsBench.Iso20022/SecureXml.cs:52:            DtdProcessing = DtdProcessing.Prohibit,
src/PaymentsBench.Iso20022/SecureXml.cs:53:            XmlResolver = null,
src/PaymentsBench.Iso20022/SecureXml.cs:54:            MaxCharactersInDocument = maxBytes,
src/PaymentsBench.Iso20022/SecureXml.cs:55:            MaxCharactersFromEntities = 0,
```

*Keep it that way:* nothing may call `XDocument.Load`/`XmlReader.Create` except through `SecureXml`; a test for the file-entity and entity-expansion cases already exists (`Pain001Tests`, `Camt053Tests`, `Pain001EndpointTests`).

**Cross-site scripting and HTML rendering (A03).** The API produces JSON and `application/problem+json` only; it renders no HTML, serves no static files, has no Razor and no redirects. There is no frontend in the repository: the only TypeScript is the Playwright suite under `tests/PaymentsBench.E2E`, which drives the API and the generated documentation page from outside, and is excluded from the listing below.

```text
$ git grep -nE 'Results\.(Content|Text|File|Redirect)|LocalRedirect|text/html|AddRazor|AddControllers|UseStaticFiles|UseFileServer|PhysicalFile|IFormFile' -- 'src/*.cs'
src/PaymentsBench.PartnerSimulator/Program.cs:116:        return Results.Content(StatementXml.Render(date, "NOSTRO-SIM", lines.Select(l => (l.endToEndId, l.amount, l.currency, l.direction))), StatementXml.MediaType);
```

```text
$ git ls-files | grep -E '\.(tsx?|jsx|vue|svelte|cshtml|razor)$' | grep -v '^tests/PaymentsBench.E2E/'
(no output)
```

**Insecure deserialization (A08).** `System.Text.Json` only, with no polymorphic type information read from the payload and no binary formatter.

```text
$ git grep -nE 'BinaryFormatter|NetDataContractSerializer|LosFormatter|SoapFormatter|TypeNameHandling|Newtonsoft|JsonPolymorphic|JsonDerivedType' -- 'src/*.cs' '*.csproj' Directory.Packages.props
(no output)
```

**CORS, cookies and CSRF (A01/A05, guide section 8).** There is no browser client, no cookie and no session: authentication is a bearer token in a header, which a cross-site request cannot attach. With no `AddCors`, browsers are denied cross-origin reads by default.

```text
$ git grep -nE 'AddCors|UseCors|Access-Control|AddCookie|AddSession|UseSession|Antiforgery|SetCookie|Cookies\.Append' -- 'src/*.cs'
(no output)
```

*If a browser client is ever added:* named origins only; never a wildcard on a PaaS apex such as `*.fly.dev` together with credentials (it trusts every tenant of the platform); tokens in an HttpOnly cookie set by a server route, never in web storage.

**Password hashing, account lifecycle (A02/A07).** This service stores no password, issues no token and has no registration, confirmation or reset flow; authservice does all of it. The one hash in the tree is a non-secret SHA-256 of a request for idempotency comparison. Identifiers use `Guid.NewGuid()`, which the guide allows for ids and nothing else; no value an attacker could present as proof is generated here.

```text
$ git grep -nEi 'PasswordHasher|Rfc2898|BCrypt|Argon2|\bMD5\b|\bSHA1\b|RijndaelManaged|TripleDES|ResetPassword|ConfirmEmail|UserManager|SignInManager' -- 'src/*.cs'
(no output)
```

```text
$ git grep -nE 'SHA256|RandomNumberGenerator|new Random|Guid\.NewGuid' -- 'src/*.cs' ':!*Migrations*'
src/PaymentsBench.Application/Payments/InitiatePayment.cs:102:            Guid.NewGuid(),
src/PaymentsBench.Application/Payments/RequestHash.cs:18:        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
src/PaymentsBench.Domain/Ledger/Postings.cs:13:        Guid.NewGuid(),
src/PaymentsBench.Domain/Ledger/Postings.cs:23:        Guid.NewGuid(),
src/PaymentsBench.Domain/Ledger/Postings.cs:33:        Guid.NewGuid(),
src/PaymentsBench.Domain/Ledger/Postings.cs:43:        Guid.NewGuid(),
src/PaymentsBench.Infrastructure/Persistence/EfCoreStores.cs:237:            Id = Guid.NewGuid(),
src/PaymentsBench.PartnerSimulator/Program.cs:56:    var transfer = transfers.GetOrAdd(request.EndToEndId, id => new SimulatedTransfer(id, $"PTR-{Guid.NewGuid():N}"[..12], request.Amount, request.Currency, "Accepted", null, null));
```

*Not verifiable from this repository:* authservice's password-hashing parameters and whether it allows open self-registration. Both are recorded under SEC-03 as things to look at on the authservice side before deployment; recording "the framework default" is not a review.

**Path traversal and user-supplied file names (guide section 6).** No request value reaches the file system: the only file access is the start-up loader for scheme and calendar data, in Infrastructure, with a directory from configuration and calendar names taken from the repository's own YAML.

```text
$ git grep -nE 'File\.|Directory\.|FileStream|StreamReader|Path\.' -- 'src/PaymentsBench.Api/*.cs' 'src/PaymentsBench.Application/*.cs' 'src/PaymentsBench.Domain/*.cs'
src/PaymentsBench.Api/Errors/ProblemDetailsExceptionHandler.cs:25:        if (exception is MessageFormatException { ElementPath.Length: > 0 } format)
```

**SSRF (A10, API7).** The only outbound HTTP is to the partner. Its base address comes from configuration (`Partner:BaseAddress`), and the caller-supplied end-to-end id is path-escaped (`Uri.EscapeDataString`) and sent as a relative URI, so it cannot change the host. This is *clean* rather than N/A: the sites are listed so the next reviewer does not redo it.

```text
$ git grep -nE 'HttpClient|BaseAddress|PostAsJsonAsync|GetFromJsonAsync|client\.GetAsync|new Uri\(' -- 'src/PaymentsBench.Api/*.cs' 'src/PaymentsBench.Application/*.cs' 'src/PaymentsBench.Infrastructure/*.cs' ':!*Migrations*'
src/PaymentsBench.Infrastructure/InfrastructureRegistration.cs:76:        var partnerBaseAddress = configuration["Partner:BaseAddress"] ?? "http://partner";
src/PaymentsBench.Infrastructure/InfrastructureRegistration.cs:78:        services.AddHttpClient<IPartnerGateway, HttpPartnerGateway>(PartnerClientName, client =>
src/PaymentsBench.Infrastructure/InfrastructureRegistration.cs:80:                client.BaseAddress = new Uri(partnerBaseAddress);
src/PaymentsBench.Infrastructure/InfrastructureRegistration.cs:99:        services.AddHttpClient<IStatementSource, HttpStatementSource>(client =>
src/PaymentsBench.Infrastructure/InfrastructureRegistration.cs:100:            client.BaseAddress = new Uri(partnerBaseAddress));
src/PaymentsBench.Infrastructure/Partner/HttpPartnerGateway.cs:15:public sealed class HttpPartnerGateway(HttpClient client) : IPartnerGateway
src/PaymentsBench.Infrastructure/Partner/HttpPartnerGateway.cs:21:            using var response = await client.PostAsJsonAsync("/transfers", transfer, PartnerJson.Options, cancellationToken);
src/PaymentsBench.Infrastructure/Partner/HttpPartnerGateway.cs:50:            using var response = await client.GetAsync(new Uri($"/transfers/{Uri.EscapeDataString(endToEndId)}", UriKind.Relative), cancellationToken);
src/PaymentsBench.Infrastructure/Partner/HttpStatementSource.cs:31:public sealed class HttpStatementSource(HttpClient client, PartnerStatementSettings settings) : IStatementSource
src/PaymentsBench.Infrastructure/Partner/HttpStatementSource.cs:36:        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/statements/{statementDate:yyyy-MM-dd}", UriKind.Relative));
```

**Mass assignment (API3).** No endpoint binds a domain type or a free-form object: bodies are the three records in `Contracts/Requests.cs`, every field named; responses are the explicit views in `Contracts/Views.cs`. The grep below finds no `[FromBody]`, no `JsonElement`/`JsonNode` and no `dynamic` in the API project.

```text
$ git grep -nE '\[FromBody\]|ReadFromJsonAsync|JsonElement|JsonNode|dynamic |ExpandoObject' -- 'src/PaymentsBench.Api/*.cs'
(no output)
```

**Open redirects.** Covered by the redirect pattern in the XSS grep above; `Results.Created` is given a relative path built from a server-generated GUID.

**Forwarded headers, HTTPS redirection, HSTS, rate limiting.** None is configured, and nothing reads `Request.Scheme`, `Request.Host` or the remote address, so a wrong forwarded-header setup cannot corrupt a decision today. What the absence costs is in SEC-08 (rate limiting) and SEC-16 (host and transport hardening).

```text
$ git grep -nE 'Request\.(Scheme|Host|Headers)|RemoteIpAddress|X-Forwarded|UseForwardedHeaders|UseHttpsRedirection|UseHsts|AddRateLimiter|UseRateLimiter|RequireRateLimiting' -- 'src/*.cs'
(no output)
```

## 4. Positive findings

What is solid, so the next review does not re-check it. The ones marked **(test)** are pinned by a test so losing them turns the build red.

- **P-1 Deny by default, mechanically.** The fallback policy requires an authenticated user for every endpoint (`IdentityRegistration.cs:82-83`); the anonymous list is `/health`, `/alive` (`Extensions.cs:113-114`), `/metrics` when its own switch is on (`Extensions.cs:120`) and OpenAPI in Development only (`IdentityRegistration.cs:89-91`). It is short enough to read aloud. **(test)** `IdentityTests`, `MetricsEndpointTests`, and `SecurityPostureTests.A_deployed_host_answers_nothing_but_health_without_a_token`, which also covers `/swagger` and an unknown route.
- **P-2 Token validation is strict.** RS256 only (`ValidAlgorithms`, `IdentityRegistration.cs:78`), issuer, audience, lifetime and signing key all validated (`:72-77`), metadata over HTTPS outside Development (`:60`, `:68`), inbound claim mapping off so claims keep the names the issuer chose (`:69`), and issuer and audience are mandatory whenever an authority is set so a token minted for another product is refused. The API holds no key. **(test)** wrong key, wrong audience, wrong algorithm, no token (`IdentityTests`).
- **P-3 Fail closed on start-up.** Outside Development a missing `Auth__Authority`, `Auth__Issuer` or `Auth__Audience` stops the process naming the setting (`IdentityRegistration.cs:35`). OpenAPI is behind the token outside Development. **(test)**
- **P-4 Error responses leak nothing.** Every error leaves as `application/problem+json`; unexpected exceptions are logged server-side and answered with a fixed text (`ProblemClassifier.cs:25`); malformed JSON is answered with fixed text, not the parser's message; `/health` says only `Healthy`. **(test)** `SecurityPostureTests` (Production and Development, with a repository that throws a message containing a connection string).
- **P-5 No mass assignment, no excess exposure.** Request records name every field; `PaymentView` omits the debtor and creditor account numbers; outbox events carry ids, amounts, scheme and reason codes but no account numbers, so the publisher's log line (`EventPublishers.cs:22`) cannot leak them.
- **P-6 Containers.** Both images are multi-stage, run as `USER $APP_UID` (`Dockerfile:40` / `:37`) and CI asserts the uid is not 0 (`containers.yml`, "Container does not run as root"). Nothing sensitive is baked in: the final stage copies only `/app/publish`; `.dockerignore` excludes `.env*`, `.git`, `.github`, `tests`, `docs`, `scripts`, `flyio`; the only `ENV` is `ASPNETCORE_URLS`. The published image contains the compiled service, `appsettings.json`, `appsettings.Development.json` (an empty connection string and a localhost partner address) and `scenarios/` data, all of which are in the public repository anyway. That is why the images can be public (the deploy procedure makes them public so Fly can pull from GHCR).
- **P-7 Secrets appear only by name.** `secrets.env.example` is placeholders; `.env*` is ignored (`.gitignore:11-12`); gitleaks runs on full history on every PR, every push to `main` and weekly, with a "rotate first" failure message (`secret-scan.yml`). The pattern greps below find no credential-shaped string and no private key. In the deployment slice, secrets are GitHub environment secrets staged onto Fly by the pipeline and listed by name only; the signing key is generated once and never reused.

```text
$ git grep -nEi '(password|secret|api[_-]?key|token|private[_-]?key)[a-z_]*\s*[:=]\s*["'\''][^"'\''<{$ ]{8,}' -- . ':!package-lock.json' ':!docs/index.html' ':!tests'
(no output)
```

```text
$ git grep -n 'BEGIN [A-Z ]*PRIVATE KEY' -- .
(no output)
```

```text
$ git ls-files | grep -E '(^|/)\.env' ; git check-ignore -v .env .env.local
.gitignore:11:.env    .env
.gitignore:12:.env.*    .env.local
```

- **P-8 Logs.** The log sites in the API, Application and Infrastructure projects log mode names, setting names, type names and message ids; none logs a request body, a token or an account number.

```text
$ git grep -nE 'Log(Information|Warning|Error|Debug|Trace)\(|LoggerMessage' -- 'src/PaymentsBench.Api/*.cs' 'src/PaymentsBench.Application/*.cs' 'src/PaymentsBench.Infrastructure/*.cs'
src/PaymentsBench.Api/Errors/ProblemDetailsExceptionHandler.cs:39:    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception.")]
src/PaymentsBench.Api/Identity/IdentityRegistration.cs:99:            logger.LogWarning("auth: {Mode}. No '{Setting}': every endpoint is open. This is allowed in Development only.", mode.Description, AuthorityKey);
src/PaymentsBench.Api/Identity/IdentityRegistration.cs:103:            logger.LogInformation("auth: {Mode}.", mode.Description);
src/PaymentsBench.Infrastructure/InfrastructureRegistration.cs:111:            logger.LogWarning("No '{Connection}' connection string: running DEGRADED with {Mode} storage. Nothing survives a restart.", PaymentsConnectionName, mode.Name);
src/PaymentsBench.Infrastructure/InfrastructureRegistration.cs:115:            logger.LogInformation("Storage: {Mode}.", mode.Name);
src/PaymentsBench.Infrastructure/Outbox/DedupingConsumer.cs:35:        logger.LogInformation("Consumed {Type} {MessageId}", message.Type, message.MessageId);
src/PaymentsBench.Infrastructure/Outbox/DedupingConsumer.cs:87:            logger.LogInformation("Dropped duplicate {Type} {MessageId}", message.Type, message.MessageId);
src/PaymentsBench.Infrastructure/Outbox/DedupingConsumer.cs:97:            logger.LogError(ex, "Handler failed for {Type} {MessageId}; not recorded as applied.", message.Type, message.MessageId);
src/PaymentsBench.Infrastructure/Outbox/EventPublishers.cs:22:        logger.LogInformation("Published {Type} {MessageId}: {Payload}", type, messageId, payload);
src/PaymentsBench.Infrastructure/Outbox/OutboxDispatcher.cs:27:            logger.LogError(ex, "Outbox dispatcher not started: the schema migration failed.");
src/PaymentsBench.Infrastructure/Outbox/OutboxDispatcher.cs:43:                logger.LogError(ex, "Outbox dispatch failed; will retry.");
src/PaymentsBench.Infrastructure/Persistence/MigrationHostedService.cs:28:            logger.LogInformation("Database schema migrated.");
src/PaymentsBench.Infrastructure/Sweeping/SubmittedPaymentSweeper.cs:30:            logger.LogInformation("Submitted-payment sweeper is disabled ({Key}:Enabled=false).", SweeperOptions.SectionName);
src/PaymentsBench.Infrastructure/Sweeping/SubmittedPaymentSweeper.cs:44:            logger.LogError(ex, "Submitted-payment sweeper not started: the schema migration failed.");
src/PaymentsBench.Infrastructure/Sweeping/SubmittedPaymentSweeper.cs:48:        logger.LogInformation(
src/PaymentsBench.Infrastructure/Sweeping/SubmittedPaymentSweeper.cs:65:                logger.LogError(ex, "Submitted-payment sweep failed; will retry in {Interval}.", settings.Interval);
src/PaymentsBench.Infrastructure/Sweeping/SubmittedPaymentSweeper.cs:88:            logger.LogWarning(failure.Exception, "Sweep could not resolve payment {PaymentId}; it stays Submitted for the next sweep.", failure.PaymentId);
src/PaymentsBench.Infrastructure/Sweeping/SubmittedPaymentSweeper.cs:93:            logger.LogInformation(
```

- **P-9 Dependency and code scanning exist.** Dependabot for NuGet, GitHub Actions and both Dockerfiles (`.github/dependabot.yml`, weekly); CodeQL with `security-and-quality` on every PR, push and weekly (`codeql.yml`); `dotnet list package --vulnerable --include-transitive` reports no vulnerable package in any of the fifteen projects today.
- **P-10 Workflows are written defensively.** Every workflow declares top-level `permissions`, read-only except `deploy-pages.yml` (which adds `pages: write` and `id-token: write` for the Pages deployment); other write scopes are granted per job and only where needed (`packages: write` for the image build, `security-events: write` for CodeQL, `contents: write` for the PDF release); no `pull_request_target`, no `workflow_run`, no `curl | sh`; no `${{ }}` expression from event data is spliced into a script: the six `inputs.*` uses (deploy, scale, destroy and load) are all `env:` assignments read as shell variables, so a crafted input cannot become code. flyctl is downloaded at a pinned version and verified against a SHA-256 before use, and `scripts/load-test.sh` does the same for k6.

```text
$ git grep -nE '\$\{\{ *(github\.event|github\.head_ref|github\.ref_name|inputs\.)' -- .github/workflows
.github/workflows/deploy.yml:60:      DEPLOY_ALL: ${{ inputs.deploy_all }}
.github/workflows/flyio-destroy.yml:39:      CONFIRM: ${{ inputs.confirm }}
.github/workflows/flyio-destroy.yml:40:      DELETE_VOLUME: ${{ inputs.delete_volume }}
.github/workflows/flyio-scale.yml:35:      ACTION: ${{ inputs.action }}
.github/workflows/load.yml:40:          VUS: ${{ inputs.vus }}
.github/workflows/load.yml:41:          PHASE_SECONDS: ${{ inputs.phase_seconds }}
```

```text
$ git grep -nE 'pull_request_target|workflow_run|curl[^|]*\|[ ]*(sudo )?(ba)?sh' -- .github scripts
(no output)
```

```text
$ git grep -nE '^permissions:|^    permissions:|: write' -- .github/workflows
.github/workflows/build-overview-pdf.yml:21:#  "LaTeX build output" block in .gitignore). `contents: write` is granted only
.github/workflows/build-overview-pdf.yml:40:permissions:
.github/workflows/build-overview-pdf.yml:106:    permissions:
.github/workflows/build-overview-pdf.yml:107:      contents: write
.github/workflows/ci.yml:8:permissions:
.github/workflows/codeql.yml:18:permissions:
.github/workflows/codeql.yml:30:    permissions:
.github/workflows/codeql.yml:33:      security-events: write
.github/workflows/containers.yml:11:permissions:
.github/workflows/deploy-pages.yml:22:permissions:
.github/workflows/deploy-pages.yml:24:  pages: write
.github/workflows/deploy-pages.yml:25:  id-token: write
.github/workflows/deploy.yml:9:#   Fly token exists. It is the only job with `packages: write`.
.github/workflows/deploy.yml:26:permissions:
.github/workflows/deploy.yml:171:    permissions:
.github/workflows/deploy.yml:173:      packages: write   # the only job that may write packages; GITHUB_TOKEN is its only credential
.github/workflows/flyio-destroy.yml:22:permissions:
.github/workflows/flyio-scale.yml:18:permissions:
.github/workflows/lint.yml:18:permissions:
.github/workflows/load.yml:17:permissions:
.github/workflows/secret-scan.yml:24:permissions:
```

- **P-12 XML is parsed safely (added after PR #24).** `SecureXml` is the single entry for pain.001 and camt.053: DTD prohibited, no resolver, no entity expansion, a size bound, and malformed documents answered 400 naming the element. The endpoint additionally pins its own limit (1 MiB, `Pain001Endpoints.cs`), which is the per-endpoint cap SEC-07 recommended for a body that is legitimately larger than the 64 KiB default. **(test)** file-entity and entity-bomb documents are refused in `Pain001Tests`, `Camt053Tests` and `Pain001EndpointTests`. Two small residues: a 400 detail carries the XML parser's own message (line and position; no path, since nothing is resolved), and the transaction count is bounded only by the 1 MiB size (SEC-08).
- **P-11 The deployment topology is private where it should be (as written; nothing is deployed yet).** PostgreSQL has no `[http_service]` and no `[[services]]` and is deployed `--no-public-ips`; the partner is deployed `--no-public-ips`; `force_https = true` on the three public-capable apps; `ASPNETCORE_ENVIRONMENT = "Production"` on all four; `/metrics` is not enabled (no `Metrics__` setting). The two databases have separate roles with `REVOKE CONNECT ... FROM PUBLIC`. The post-deploy check proves a payments route answers 401 without a token, which would catch a deployment that came up open.

```text
$ git grep -nE 'no-public-ips|\[\[services\]\]|\[http_service\]|force_https|ASPNETCORE_ENVIRONMENT|Metrics__|Limits__' -- flyio .github/workflows/deploy.yml
.github/workflows/deploy.yml:279:        # --no-public-ips: the database is reached only at <app>.internal:5432.
.github/workflows/deploy.yml:280:        run: flyctl deploy --config flyio/postgres.fly.toml --app "$APP" --ha=false --no-public-ips --yes --wait-timeout 300
.github/workflows/deploy.yml:425:        # --no-public-ips: the simulated bank (and its open fault controls) is reached only over 6PN.
.github/workflows/deploy.yml:426:        run: flyctl deploy --config flyio/partner.fly.toml --app "$APP" --image "$IMAGE" --ha=false --no-public-ips --yes --wait-timeout 300
flyio/api.fly.toml:24:  ASPNETCORE_ENVIRONMENT = "Production"
flyio/api.fly.toml:42:[http_service]
flyio/api.fly.toml:44:  force_https = true                # the proxy redirects http to https; bearer tokens never travel in clear
flyio/authservice.fly.toml:18:  ASPNETCORE_ENVIRONMENT = "Production"
flyio/authservice.fly.toml:33:[http_service]
flyio/authservice.fly.toml:35:  force_https = true                # tokens and passwords never travel in clear
flyio/partner.fly.toml:4:# Private by design. The deploy passes --no-public-ips, so the app has no public address; the API
flyio/partner.fly.toml:20:  ASPNETCORE_ENVIRONMENT = "Production"
flyio/partner.fly.toml:23:[http_service]
flyio/partner.fly.toml:25:  force_https = true                # moot without a public IP; kept so an allocated one is never plain http
flyio/postgres.fly.toml:17:# No [http_service] and no [[services]] block, deliberately: the database is reachable only at
flyio/postgres.fly.toml:18:# payments-bench-postgres.internal:5432 over 6PN. The deploy also passes --no-public-ips. A
```

```text
$ git grep -nE '^USER|^HEALTHCHECK|^ENV|^COPY --from|^RUN apt|^FROM' -- 'src/PaymentsBench.Api/Dockerfile' 'src/PaymentsBench.PartnerSimulator/Dockerfile'
src/PaymentsBench.Api/Dockerfile:8:FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
src/PaymentsBench.Api/Dockerfile:26:FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
src/PaymentsBench.Api/Dockerfile:32:RUN apt-get update \
src/PaymentsBench.Api/Dockerfile:37:COPY --from=build /app/publish .
src/PaymentsBench.Api/Dockerfile:39:ENV ASPNETCORE_URLS=http://+:8080
src/PaymentsBench.Api/Dockerfile:41:USER $APP_UID
src/PaymentsBench.Api/Dockerfile:44:HEALTHCHECK --interval=15s --timeout=3s --start-period=20s --retries=3 \
src/PaymentsBench.PartnerSimulator/Dockerfile:8:FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
src/PaymentsBench.PartnerSimulator/Dockerfile:22:FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
src/PaymentsBench.PartnerSimulator/Dockerfile:28:RUN apt-get update \
src/PaymentsBench.PartnerSimulator/Dockerfile:33:COPY --from=build /app/publish .
src/PaymentsBench.PartnerSimulator/Dockerfile:35:ENV ASPNETCORE_URLS=http://+:8080
src/PaymentsBench.PartnerSimulator/Dockerfile:37:USER $APP_UID
src/PaymentsBench.PartnerSimulator/Dockerfile:40:HEALTHCHECK --interval=15s --timeout=3s --start-period=20s --retries=3 \
```

## 5. Findings

Format per guide section 2: location, issue, current code, risk, attack scenario, impact, recommendation, better. Priorities are time windows (section 7). Status and owners are in the ledger (section 8). A finding with no attack scenario would be a style note; there are none of those here, and the things considered and dropped for lack of a scenario are listed at the end of the section.

### SEC-01 A payment has no owner: any authenticated caller can read, list and submit any payment

**Priority P1 · Risk High · OPEN, owner stream A · Register R21**

- **Location:** `src/PaymentsBench.Api/Endpoints/PaymentsEndpoints.cs:23` (list), `:28` (get), `:33` (submit), `:39` (resolve); `src/PaymentsBench.Domain/Payments/Payment.cs` (no owner field); the only check is `IdentityRegistration.cs:82-83`.
- **Issue:** Authorization stops at "has a valid token for this audience"; nothing records or compares who created a payment (BOLA, API1).
- **Current code:**

  ```csharp
  private static async Task<IResult> GetAsync(Guid id, IPaymentRepository repository, CancellationToken cancellationToken) =>
      Results.Ok(PaymentView.From(await repository.GetAsync(id, cancellationToken) ?? throw new PaymentNotFoundException(id)));
  ```

- **Attack scenario:** `alice` and `bob` hold ordinary tokens for the same audience. Alice creates a payment (`POST /payments`, key `order-1001`, 250 EUR). Bob calls `GET /payments`: the page contains Alice's payment with its end-to-end id, amount, status and partner reference. He calls `GET /payments/{alice's id}`: 200, the same. He calls `POST /payments/{alice's id}/submit`: 200, status `Submitted`; Alice's payment went to the partner at a moment Bob chose. Reproduced against the real API in a throwaway run (Production shape, two signed tokens): all three answered 200. The end-to-end ids he reads are exactly what SEC-02's webhook needed, so before this PR's fix the two chained.
- **Impact:** Confidentiality of every customer's payments; integrity of timing for every initiated payment; the chain into SEC-02 (closed).
- **Recommendation:** Stamp the token's `sub` on the payment at initiation (a required `OwnerId` on `Payment`, a migration, tokens without `sub` refused); make `Get`, `List` and `Submit` take the caller and answer **404, not 403**, for someone else's payment so existence is not confirmed; callers with `payments.operate` see all.
- **Better:** An account model: a payment belongs to a debtor account, accounts belong to customers, and the check is a resource-based `IAuthorizationService` call at the repository boundary, so no endpoint can forget it.
- **Why not fixed here:** it is a domain and schema change in stream A's code. It is a register row with a closing condition (a test in which `bob` gets 404 for `alice`'s payment on both the in-memory and EF stores).
- **Condition that downgrades it:** if the deployment's token population is one trusted operator (SEC-03 verified, recorded as a decision), this is a single-tenant concern and the deployment may proceed; it must not proceed with a second token holder.

### SEC-02 The partner webhook accepted any authenticated caller, and moved money on their word

**Priority P1 · Risk Critical · FIXED in this PR (grant `payments.partner.notify`); the partner-signature half stays open as R13**

- **Location:** `src/PaymentsBench.Api/Endpoints/PartnerNotificationEndpoints.cs:10` (route), `src/PaymentsBench.Application/Payments/ApplyPartnerNotification.cs` (`MarkSettled`/`MarkRejected`/`MarkReturned` plus a ledger posting each); `src/PaymentsBench.Api/ApiServiceCollectionExtensions.cs:41` (the fix).
- **Issue:** The route fell under the fallback policy (`RequireAuthenticatedUser`), so the sender needed a token but not a *particular* token. Any caller could post a settlement, rejection or return for any end-to-end id.
- **Current code (before):**

  ```csharp
  app.MapPost("/partner/notifications", ApplyAsync)
  ```

- **Attack scenario:** A payer holds an ordinary token. They initiate a payment of 5,000 EUR and `POST /payments/{id}/submit`; the partner accepts (`Accepted`: the transfer is on its way and will settle). They then post `{"notificationId":"x-1","endToEndId":"<their e2e id>","kind":"Rejected","reasonCode":"AC01"}`. `Accepted → Rejected` is an allowed move (`PaymentTransitions.cs`), so the handler marks the payment rejected and posts `Release` (`pending-outbound → customer-funds`): the 5,000 is returned to the customer in the ledger while the bank pays it out. Variants: `Settled`/`Returned` on someone else's payment to corrupt the ledger and reconciliation; or send a notification id the partner will use later (`N-123`), so the genuine one is answered `Duplicate` and dropped (the inbox dedupes on id alone).
- **Impact:** Loss of funds the ledger claims to hold; ledger and partner statement permanently out of step; genuine partner notifications suppressed.
- **Recommendation (done):** the route needs a grant on the token, `payments.partner.notify` (a `role` value or one value of the `scope` claim), configurable as `Auth:Grants:PartnerNotifications`; absent the grant, 403. The grant is issued to the partner's integration identity only.
- **Better (open, R13):** the partner signs the payload (HMAC) or presents a client certificate (mTLS), and a test rejects an unsigned one; a bearer token proves the sender is *a* client of authservice with the grant, not that the bank sent this body. Also: bind the notification to the payment's partner reference, and refuse a `Rejected` for a payment the partner has `Accepted` unless the partner's own status lookup agrees.
- **Tests:** `AuthorizationPolicyTests` (403 without the grant, near-miss values refused, role and scope shapes accepted, the operations grant does not open the webhook, 401 without a token, open in Development anonymous mode); with the endpoint grouping reverted, 10 of them fail.

### SEC-03 Who can obtain a token is not decided or checked in this repository

**Priority P1 · Risk High · OPEN (verify before the first deployment), owner stream B and the authservice operator · Register R26**

- **Location:** `flyio/authservice.fly.toml:15` (the pinned image); `docs/DEPLOYMENT.md` first-tag step 5 ("obtain a token from `payments-bench-authservice`", no statement of who may); `src/PaymentsBench.AppHost/AppHost.cs:27`.
- **Issue:** Every authorization decision in the API reduces to "holds a token for audience `PaymentsBench`". Whether a stranger can obtain one (open self-registration, default roles, whether `payments.operate` and `payments.partner.notify` can be self-assigned) is a property of authservice's configuration, which this repository neither sets nor checks. Its source is never read or modified from here.
- **Current code:**

  ```toml
  Jwt__Issuer = "PaymentsBench"
  Jwt__Audience = "PaymentsBench"
  ```

- **Attack scenario:** The estate's authservice will be public at `payments-bench-authservice.fly.dev`. If authservice allows registration, a stranger registers, receives a token for the right audience and is then an "authenticated caller": SEC-01 (read everything), SEC-05 (squat keys), SEC-08 (unlimited payments into a 3 GB volume), SEC-09 (squat end-to-end ids). If registration is closed or admin-only, the same findings are insider findings.
- **Impact:** Converts every "authenticated" finding from insider to internet. This is why it is P1 although nothing here is wrong.
- **Recommendation:** Before the first deployment, read authservice's configuration for the deployed version: registration endpoints and whether they are enabled, default claims, how a grant is assigned and by whom, password-hashing parameters (record them, do not trust the word "default"), login rate limiting and lockout. Record the answers in `docs/DEPLOYMENT.md`. If registration is open, close it for this estate or ensure the grants cannot be self-assigned.
- **Better:** Machine callers (the partner, the operator tooling) use a client-credentials flow from an allow-list of clients, each with only its grants; human registration stays off.

### SEC-04 The ledger and reconciliation were readable and triggerable by any authenticated caller

**Priority P2 · Risk High · FIXED in this PR (grant `payments.operate`)**

- **Location:** `src/PaymentsBench.Api/Endpoints/LedgerEndpoints.cs:13` (entries of any payment), `:18` (balance of any account), `ReconciliationEndpoints.cs:9`; `src/PaymentsBench.Application/Reconciliation/ReconciliationService.cs:38-41` (loads every settled and returned payment); `ApiServiceCollectionExtensions.cs:42-43` (the fix).
- **Issue:** These are back-office functions. `GET /ledger/balances/customer-funds/EUR` is the aggregate of every customer's funds; `GET /payments/{id}/ledger` is any payment's journal; `POST /reconciliation/runs/{date}` calls the partner for a statement and loads all settled and returned payments for that date's comparison.
- **Attack scenario:** Any token holder reads the aggregate customer balance and the movements of any payment id (ids leak through SEC-01), and calls reconciliation in a loop: each call is a partner round trip plus a full read of settled payments, an amplification against the partner and the database from one cheap request.
- **Impact:** Disclosure of balances and movements; resource amplification.
- **Recommendation (done):** both groups need `payments.operate`, configurable as `Auth:Grants:Operations`; 403 without it. Reconciliation without the grant is refused *before* it reaches the partner (tested).
- **Better:** once SEC-01 lands, `GET /payments/{id}/ledger` should also be open to the payment's owner; and reconciliation should page rather than load every settled payment.
- **Tests:** `AuthorizationPolicyTests.Ledger_reads_need_the_operations_grant`, `A_reconciliation_run_is_refused_before_it_reaches_the_partner_without_the_grant`, `The_operations_grant_lets_a_reconciliation_run_through_to_its_handler`, the renamed-grant test.

### SEC-05 The idempotency key is global: another caller can replay it and learn the payment

**Priority P2 · Risk High · OPEN, owner stream A · Register R21**

- **Location:** `src/PaymentsBench.Application/Payments/InitiatePayment.cs:86-91`; `src/PaymentsBench.Infrastructure/Persistence/PaymentsDbContext.cs:27` (unique index on the key alone); `InMemoryStores.cs:32`.
- **Issue:** The key's scope is the whole service, not the caller.
- **Current code:**

  ```csharp
  var existing = await payments.FindByIdempotencyKeyAsync(command.IdempotencyKey, cancellationToken);
  if (existing is not null)
  {
      return existing.RequestHash == hash
          ? existing
          : throw new IdempotencyConflictException(command.IdempotencyKey);
  }
  ```

- **Attack scenario:** Alice created a payment with key `order-1001`. Bob, with his own token, posts the *same body* with the same key: **201, and the response is Alice's payment** (same id). Posting a *different* body with Alice's key: **409 "Idempotency key 'order-1001' was already used with a different request."**, which confirms the key exists and echoes it. Both reproduced. Third path, squatting: Bob registers likely keys (`order-1002`, `order-1003`, or a date-stamped pattern) before Alice uses them; her genuine requests then get 409 and she cannot initiate.
- **Impact:** Cross-caller disclosure of a payment (needs the full body, so partial), an existence oracle for keys, and a denial of service on guessable keys.
- **Recommendation:** Scope the key by owner: unique on `(OwnerId, IdempotencyKey)`, `FindByIdempotencyKeyAsync(owner, key)`. It comes with SEC-01 because it needs the owner.
- **Better:** Keys that the server mints and expires, or a documented key format that includes entropy (UUID), checked at validation.

### SEC-06 The Development anonymous policy is one mis-set environment variable away from an open payment API

**Priority P2 · Risk High (likelihood low) · FIXED for Fly in this PR; other hosts remain**

- **Location:** `src/PaymentsBench.Api/Identity/IdentityRegistration.cs:31-56`; `src/PaymentsBench.Api/Properties/launchSettings.json` (Development by default); `.github/workflows/containers.yml` runs the API image as Development on purpose; `flyio/api.fly.toml:24` pins `Production`.
- **Issue:** The only thing separating the open mode from a deployment was the environment name (ADR 0015 states this and asks the deployment slice not to do it). Nothing enforced it.
- **Attack scenario:** An operator copy-pastes an environment block from the README or the CI smoke test into `fly secrets set`/`fly machine run`, or runs `docker run -e ASPNETCORE_ENVIRONMENT=Development` on the published image, with no `Auth__Authority`. The API starts open: anyone who can reach it can initiate and submit transfers and read every payment. The deploy workflow's post-deploy check would catch it, but only after the open API has been serving traffic.
- **Impact:** Total loss of authentication.
- **Recommendation (done):** the anonymous branch refuses to start when `FLY_APP_NAME` is set (Fly sets it on every machine), naming both settings. `AuthorizationPolicyTests.Anonymous_mode_on_fly_refuses_to_start_even_if_the_environment_says_development`, and the converse (a configured authority on Fly starts normally).
- **Residual:** a VM, Kubernetes, Azure or any other host sets no such marker. Fly is the deployment target; this is recorded in section 10.
- **Better:** make the open mode an explicit opt-in as well (`Auth__AllowAnonymous=true`), set by `launchSettings.json`, the AppHost and the CI smoke test only, so an environment name alone can never open the API. That changes `containers.yml` and the load script, which are other slices' files, so it is a follow-up, not part of this PR.

### SEC-07 No request-size limit below Kestrel's 30 MB default

**Priority P2 · Risk Medium · FIXED in this PR (64 KiB, `Limits:MaxRequestBodyBytes`)**

- **Location:** `src/PaymentsBench.ServiceDefaults/Extensions.cs:51` (the fix); before it, nothing in the tree set a limit (the grep for `MaxRequestBodySize` in section 3.3 finds only the new line); `flyio/api.fly.toml:53-54,64-65` (250 concurrent requests at most, 512 MB).
- **Issue:** Kestrel's default cap is 30,000,000 bytes, and the largest legitimate body here is a payment initiation of a few hundred bytes.
- **Attack scenario:** An authenticated caller posts a 25 MB JSON string as `endToEndId` to `POST /payments`, two hundred at a time (the proxy's soft limit is 200, `flyio/api.fly.toml:54`). Each request is buffered before validation can reject it: roughly 5 GB against a 512 MB machine, an out-of-memory kill, and every in-flight request on that machine ends in an unknown outcome (recoverable by design, but it is a self-inflicted outage for the price of one token).
- **Impact:** Availability.
- **Recommendation (done):** a 64 KiB default, configurable, applied to the API and the simulator through ServiceDefaults; the `Server` response header is dropped. Verified by posting 200 KB to a real Kestrel: **413** with a problem body (the test server does not enforce Kestrel limits, so the unit tests pin the configuration and this run pins the behaviour).
- **Better:** a per-endpoint cap when a larger body legitimately exists. `POST /payments/pain001` (PR #24) now does this: it raises its own limit to 1 MiB on the request and reads through `SecureXml`.

### SEC-08 No rate limiting

**Priority P2 · Risk Medium · OPEN, owner stream B · Register R23**

- **Location:** absent: `src/PaymentsBench.Api/ApiServiceCollectionExtensions.cs:31-44` and the grep in section 3.3 (no `AddRateLimiter`); `flyio/api.fly.toml:50-54` limits in-flight requests (250 hard, 200 soft), not their rate.
- **Issue:** Nothing bounds how fast one caller may create payments, submit or resolve them, or call an endpoint that costs a partner round trip. Guide section 9 item 3 names partial or missing rate limiting as a recurring launch blocker.
- **Attack scenario:** A token holder posts one 1 MiB `pain.001` document (`POST /payments/pain001`) carrying thousands of transactions: each becomes a payment, a ledger entry and an outbox row from a single request, and the size cap is the only bound on the count. Or loops `POST /payments` with fresh keys: unbounded payments, ledger rows and outbox rows into the 3 GB PostgreSQL volume (`flyio/postgres.fly.toml:37`), or loops `POST /payments/{id}/submit` and `.../resolve` to hammer the partner. An unauthenticated caller floods any path: each costs a 401, and the proxy's concurrency limit queues legitimate requests behind them.
- **Impact:** Storage exhaustion, partner overload, degraded service.
- **Recommendation:** ASP.NET Core's `AddRateLimiter`: a global limiter partitioned by `sub` for authenticated requests and by the client IP (trusted `Fly-Client-IP`, via forwarded-headers configuration, SEC-16) for anonymous ones; limits and the on/off switch as configuration; 429 as problem details with `Retry-After`; separate, tighter limit on the money-moving routes.
- **Better:** also at the edge, and on authservice's own login and token endpoints, which need their own limit (SEC-03).
- **Why not fixed here:** the limits are a decision (numbers), the partition key behind Fly's proxy needs forwarded-header trust, and an enabled limiter would change the load numbers the load slice has measured (R14/R15 in review). It deserves its own PR and an ADR.
- **Interim:** every endpoint needs a token and the token population is the operator (SEC-03), so this is "before deployment", not "blocks deployment".

### SEC-09 The end-to-end id is global: another caller's id answers 409, an existence oracle that can be squatted

**Priority P3 · Risk Low · OPEN, owner stream A · Register R22** (re-rated from P2 after the concurrency slice, see below)

- **Location:** `src/PaymentsBench.Application/Payments/InitiatePayment.cs:84-111` (no end-to-end id check in the handler); `src/PaymentsBench.Infrastructure/InMemory/InMemoryStores.cs:110-124` and `src/PaymentsBench.Infrastructure/Persistence/EfCoreStores.cs:284-288` (a duplicate idempotency key or end-to-end id becomes `ConcurrencyConflictException.Duplicate`); `PaymentsDbContext.cs:28` (the unique index); `src/PaymentsBench.PartnerSimulator/Program.cs:56` (`GetOrAdd` by end-to-end id).
- **What changed during this review:** when the review began, the in-memory store accepted a second payment with an existing end-to-end id (reproduced: 201, two payments sharing one id, the partner's `GetOrAdd` handing the second submit the first transfer) while PostgreSQL raised an unhandled constraint violation. The concurrency slice (R6, closed) made both stores refuse the duplicate and the exception handler answer **409**. The collision and the divergence are gone; the finding is re-rated for what is left.
- **Issue:** The caller chooses the end-to-end id and it is unique across all callers, so a second caller's request with the same id is refused, with a message ("created concurrently. Re-read it instead of creating it again.") that is wrong for this case and tells the caller nothing about why.
- **Attack scenario (reproduced after the fix):** Alice creates a payment with end-to-end id `E2E-A1`. Bob posts a different payment with `E2E-A1` and a new key: **409 "Concurrent modification"**, which confirms that id exists (an oracle for ids Bob can guess). Squatting: Bob pre-registers predictable ids (`INV-2026-0001`, `INV-2026-0002`, ...) before Alice's system uses them; her genuine initiations are refused with 409 and she cannot pay.
- **Impact:** Denial of service on guessable ids and an existence oracle; no integrity loss.
- **Recommendation:** Scope end-to-end id uniqueness by owner (with SEC-01, R21), and answer the duplicate with its own problem ("End-to-end id already used") rather than the concurrency message; the handler checks before writing so the common case is not an exception.
- **Better:** the partner-facing id is minted by the service and the caller's end-to-end id is stored as a reference.

### SEC-10 Who may run the production deploy, and who may read its secrets, is a setting the document calls optional

**Priority P2 · Risk High · OPEN (repository settings; the document half is fixed in this PR), owner stream B · Register R26**

- **Location:** `.github/workflows/deploy.yml:17-19` (trigger: any `v*` tag push, and `workflow_dispatch`), `:57,210,291,382,440` (`environment: production`), `flyio-scale.yml:32`, `flyio-destroy.yml:36`; `docs/DEPLOYMENT.md`, which said "Restrict it to protected `v*` tags and require a reviewer **if you want** a human gate" until this PR.
- **Issue:** The secrets (`FLY_API_TOKEN`, `AUTH_JWT_PRIVATE_KEY_PEM`, three database passwords) live in the `production` environment. Whether a job from an arbitrary branch may receive them, and whether a tag on an unreviewed commit may deploy, is decided by GitHub configuration that nothing in the tree enforces.
- **Attack scenario:** (1) Anyone with write access (a compromised collaborator account or token) edits a copy of `deploy.yml` on a branch to print `${{ secrets.AUTH_JWT_PRIVATE_KEY_PEM }}` and starts it with `workflow_dispatch` from that branch: unless the environment's deployment-branch policy forbids it, the job receives the environment secrets. (2) They push tag `v9.9.9` on an unreviewed commit: the `test` job passes, the `build` job pushes images built from that commit, and the estate is redeployed with it.
- **Impact:** The signing key lets the holder mint tokens as anyone (`docs/DEPLOYMENT.md` says so); the Fly token controls every app in its org (SEC-14); the database passwords reach the data.
- **Recommendation:** Make it a requirement, not an option. Environment `production`: required reviewer; deployment branches and tags restricted to `v*` tags (and `main` for manual runs); a tag ruleset limiting creation of `v*` to maintainers. `docs/DEPLOYMENT.md` now states all three as requirements to complete before the first tag (done in this PR); the settings themselves cannot be set or checked from the tree, so the date they were verified is recorded in the pull request that tags the release.
- **Better:** short-lived credentials where the platform allows them; per-app deploy tokens (SEC-14).

### SEC-11 `resolve` and `submit` are open to every authenticated caller

**Priority P3 · Risk Medium · OPEN, owner stream A · Register R24**

- **Location:** `src/PaymentsBench.Api/Endpoints/PaymentsEndpoints.cs:33` (submit), `:39` (resolve).
- **Issue:** `resolve` asks the partner and may **resubmit** a payment (ARCHITECTURE rule 3); it is an operator action: since the sweep (R8, closed) `SweepSubmittedPaymentsHandler` resolves Submitted payments on a schedule, internally and not over HTTP, so this route is a manual override. `submit` triggers the money movement. Neither has any role requirement beyond "authenticated" (and no ownership, SEC-01).
- **Attack scenario:** A token holder calls `POST /payments/{id}/resolve` in a tight loop against a `Submitted` payment: each call is a partner lookup and, when the partner answers "not found", a resubmission whose timing the sweep (minimum age, batch size) is meant to own. With a partner fault window (`TimeoutAfterAccept`), the attacker chooses when the "ask first" step runs.
- **Impact:** Partner load; loss of control over resolution timing.
- **Recommendation:** `resolve` requires `payments.operate` (one `.RequireAuthorization("operations")` on the route; the policy exists since this PR); `submit` requires the payment's owner or the operator. They are stream A's endpoint file and tests, hence a register row.

### SEC-12 No record of who did what

**Priority P3 · Risk Medium · OPEN, owner stream A · Register R24**

- **Location:** `src/PaymentsBench.Application/Payments/InitiatePayment.cs`, `SubmitPayment.cs`, `ApplyPartnerNotification.cs` (no actor); the logging grep in section 3.3 (no log line carries a subject); `PaymentMetrics` counts by status only.
- **Issue:** State-changing commands, outbox events and logs carry no caller identity.
- **Attack scenario (investigation, not exploit):** After an abuse like SEC-02, or a dispute ("I never submitted that"), nobody can say which token submitted a payment or posted the `Rejected`; the outbox events say what happened but not who made it happen. The attacker's best property is that nothing identifies them.
- **Impact:** No accountability, no forensics, no evidence for a dispute.
- **Recommendation:** Record an `Actor` (token `sub`, plus `client_id`/`azp` when present) on the three state-changing commands and on outbox events; add `sub` to the request log scope.
- **Better:** an append-only audit table written in the same unit of work.

### SEC-13 The simulator's control surface is open, and its privacy rests on one flag nobody verifies afterwards

**Priority P3 · Risk Medium (bench only) · OPEN, owner stream B · Register R26**

- **Location:** `src/PaymentsBench.PartnerSimulator/Program.cs:35,37,76,87` (`GET`/`PUT /faults`, `settle`, `return`: no authentication); `flyio/partner.fly.toml:4-6` and `:13-17` (a documented hand-run path), `.github/workflows/deploy.yml:426` (`--no-public-ips`).
- **Issue:** The simulator is open by design (ADR 0015). In a deployment, its privacy is the absence of a public IP, achieved by a flag in a workflow; nothing checks the result, and the private network (6PN) is shared by every app in the Fly organization.
- **Attack scenario:** (1) Someone redeploys the partner by hand with `flyctl deploy` (the toml documents the path) without `--no-public-ips`; Fly allocates a public address and `PUT /faults {"kind":"RejectAll"}` is on the internet: every submit fails, or `POST /transfers/{id}/settle` fabricates settlements that the API reconciles against. (2) A compromised neighbour on 6PN (authservice is a third-party image) calls the same endpoints.
- **Impact:** The bench's availability and the integrity of its demonstrations. No real bank is involved; this is the reason it is P3 and not higher.
- **Recommendation:** After the partner deploy, `flyctl ips list --app payments-bench-partner --json | jq -e 'length == 0'` fails the job if any address exists (a step to add to `deploy.yml`; the output shape of `flyctl ips list --json` cannot be checked offline, so it is not guessed at here).
- **Better:** a dedicated Fly organization for the estate, and a shared-secret header on the simulator's control routes, so even an open network does not give the fault controls away.

### SEC-14 The Fly token is organization-wide

**Priority P3 · Risk Medium · OPEN, owner stream B · Register R26**

- **Location:** `docs/DEPLOYMENT.md` (`FLY_API_TOKEN` from `fly tokens create org`); `deploy.yml:59,212,293,384,442`, `flyio-destroy.yml:38`, `flyio-scale.yml:34`.
- **Issue:** One secret authorizes creating, redeploying and destroying every app in the organization, and reading their secrets.
- **Attack scenario:** The token leaks by the SEC-10 route, or from a compromised action or `flyctl` binary in a job that holds it. The holder runs `flyctl apps destroy` on everything in the org, or `flyctl ssh console` into the API to read `ConnectionStrings__payments`.
- **Impact:** Total loss of the estate, and of anything else in the same organization.
- **Recommendation:** Put the estate in a Fly organization of its own (`FLY_ORG` already exists as a variable), so the token's reach equals the estate's; rotate on a schedule.
- **Better:** after the first deployment (which creates the apps), switch the update path to per-app deploy tokens (`fly tokens create deploy -a <app>`), keeping the org token out of CI.

### SEC-15 Supply-chain pins: tags, not digests

**Priority P3 · Risk Low · OPEN, owner stream B · Register R26**

- **Location:** the action list under P-10 (third-party actions by major tag); `.github/workflows/secret-scan.yml:46` (`gitleaks:latest`, a documented trade); `src/PaymentsBench.AppHost/AppHost.cs:27` and `flyio/authservice.fly.toml:15` (`authservice:v0.3.4`); `flyio/postgres.fly.toml:24` (`postgres:17-alpine`); `src/PaymentsBench.Api/Dockerfile:8,25` and the simulator's (`dotnet/sdk:10.0`, `aspnet:10.0`); `Dockerfile:31-33` (`apt-get install curl`, unpinned, left in the final image for the health check).
- **Issue:** Every one of these resolves, at pull time, to whatever the tag points at then.
- **Attack scenario:** The maintainer account of `docker/build-push-action` (or the registry entry for `authservice:v0.3.4`) is compromised and the tag re-pointed. The first runs in the `build` job, the only job with `packages: write`, and pushes an attacker-controlled image under this project's name, which Fly then pulls. The second replaces the one service that holds the signing key, so every token can be forged.
- **Impact:** Code execution in the estate; token forgery.
- **Recommendation:** Pin third-party actions to a full commit SHA with the tag in a comment (Dependabot's github-actions ecosystem, already configured, keeps them current); pin `authservice` and `postgres` by digest (`image@sha256:...`) in the toml and the AppHost. Keep `gitleaks:latest` as the documented exception.
- **Better:** sign the images (cosign) and verify the digest at deploy; drop `curl` from the runtime image by using Fly's own checks.
- **Not done here:** digests need the registry, and the tag-to-digest choice for `authservice` is for its owner.

### SEC-16 Host and transport hardening is absent

**Priority P4 · Risk Low · OPEN, owner stream B · Register R26**

- **Location:** `src/PaymentsBench.Api/appsettings.json:15` and `src/PaymentsBench.PartnerSimulator/appsettings.json:8` (`"AllowedHosts": "*"`); no HSTS, no HTTPS redirection, no forwarded-headers configuration (grep in section 3.3); `flyio/api.fly.toml:44` (TLS ends at Fly's proxy).
- **Issue:** Host filtering is off, and the application is unaware it is behind a TLS-terminating proxy.
- **Attack scenario:** Host-header manipulation has no effect today, because nothing builds an absolute URL from the host (verified by grep). The real exposure is a client that calls `http://payments-bench-api.fly.dev/...`: the proxy redirects to HTTPS (`force_https`), but the first request, with its `Authorization: Bearer` header, has already crossed the network in clear. Someone on the path replays the token until it expires. HSTS cannot help a non-browser client.
- **Impact:** Token exposure for clients that use `http://`.
- **Recommendation:** Set `AllowedHosts` to `payments-bench-api.fly.dev` once the Host header Fly's health check sends is known (a wrong value fails every health check and therefore the deploy; this is why it is not changed blind); say in `docs/DEPLOYMENT.md` that clients must use `https://` only; add forwarded-headers handling (trusting the Fly proxy only) when SEC-08 needs a client IP.
- **Better:** serve nothing on port 80 (drop the redirect), so a mistyped `http://` fails instead of leaking.

### SEC-17 Paging arithmetic overflows

**Priority P4 · Risk Low · OPEN, owner stream A · Register R25**

- **Location:** `src/PaymentsBench.Application/Ports/PaymentQuery.cs:29` (`public int Skip => (Page - 1) * Limit;`, with `Page` unbounded), `src/PaymentsBench.Infrastructure/Persistence/EfCoreStores.cs:100`.
- **Issue:** `page=2147483647&limit=100` overflows `int` to a negative skip.
- **Attack scenario:** A token holder requests that page. In memory the first page comes back (observed: 200). On PostgreSQL the negative `OFFSET` is rejected by the server and surfaces as a 500 plus an error log line each time (not reproduced: no PostgreSQL here).
- **Impact:** Log noise and avoidable 500s.
- **Recommendation:** Compute in `long`, clamp `Page` to a maximum, answer 400 beyond it.

### SEC-18 The partner's responses are trusted without bounds

**Priority P4 · Risk Low · OPEN, owner stream A · Register R25**

- **Location:** `src/PaymentsBench.Infrastructure/Partner/HttpPartnerGateway.cs:59` (`Enum.Parse<PartnerStatus>(body.Status, ...)`), `HttpStatementSource.cs:47`, `src/PaymentsBench.Infrastructure/InfrastructureRegistration.cs:76-98` (no response size limit; the `HttpClient` default is 2 GiB).
- **Issue:** API10: the service consumes its partner as if it were well-behaved, which is the one assumption the bench exists to refute.
- **Attack scenario:** A compromised or buggy partner answers a status lookup with `"status":"Unrecognised"`: `Enum.Parse` throws an `ArgumentException`, which the status call's catch filter does not cover (R16, closed, mapped an open circuit to `Unavailable`; an unrecognised status is the same class of failure and is not mapped). `resolve` answers 500, and the sweep records the failure and tries the payment again at every sweep, forever. Sweeps take the oldest Submitted payments first, 50 at a time (`Sweeper:BatchSize`), so enough such payments keep every batch full and younger Submitted payments are never examined. Or it returns a multi-gigabyte statement that `GetFromJsonAsync` buffers until the API runs out of memory. Today the partner is the simulator.
- **Impact:** Payments stuck at `Submitted` for good, and head-of-line blocking of the sweep; availability.
- **Recommendation:** `Enum.TryParse`, with an unknown status mapped to the unknown-outcome path (never guessed); `MaxResponseContentBufferSize` of about 1 MiB on both partner clients; validate currency and amount on statement lines.

### Considered and dropped (no attack scenario, so style notes)

- `flyio/api.fly.toml` cited "ADR 0004" for the Development policy; it is ADR 0015. A wrong cross-reference in a comment, corrected in this PR; not a finding.
- `DomainException` messages are returned to the caller and echo input (a scheme name, a key). They are `application/problem+json`, which a browser will not render as HTML, and the caller supplied the text; no scenario beyond SEC-05's oracle, which is counted there.
- JWT clock skew is the framework default (5 minutes). Short token lifetime and revocation are authservice's (section 10).
- `appsettings.Development.json` ships in the published image. It holds an empty connection string and a localhost address; nothing to protect.
- The OpenAPI document is served to any token holder outside Development (no UI). The route table is in a public repository (section 10).

## 6. Endpoint x grant matrix

Every route of the API and of the simulator, its method, what protects it **as implemented after this PR** (with `file:line`), and what it **should be**. The API has no roles; since this PR it has two grants, carried on the token as a `role` value or as one value of the `scope` claim: `payments.operate` (back-office) and `payments.partner.notify` (the partner's integration). "Token" means a valid token for the audience, with neither grant.

### 6.1 The API (`src/PaymentsBench.Api`, and the endpoints ServiceDefaults adds)

| Method and route | Defined at | Anonymous | Token | `payments.operate` | `payments.partner.notify` | Implemented by | Should be |
|---|---|---|---|---|---|---|---|
| `GET /health` | `Extensions.cs:113` | yes | yes | yes | yes | `.AllowAnonymous()` | anonymous (probe); answers `Healthy` only (P-4) |
| `GET /alive` | `Extensions.cs:114` | yes | yes | yes | yes | `.AllowAnonymous()` | anonymous (probe) |
| `GET /metrics` (only if `Metrics:Prometheus:Enabled`) | `Extensions.cs:120` | yes | yes | yes | yes | `.AllowAnonymous()`, off by default | off; if on, reachable only from the scraper's network (section 10) |
| `GET /openapi/v1.json` | `ApiServiceCollectionExtensions.cs:35` | Development only | yes | yes | yes | `AllowAnonymousInDevelopment`, else fallback (`IdentityRegistration.cs:82-83`) | authenticated at most; no UI is served |
| `POST /payments` | `PaymentsEndpoints.cs:16` | no | yes | yes | yes | fallback policy | authenticated; stamps the owner (SEC-01) |
| `POST /payments/pain001` | `Pain001Endpoints.cs:23` | no | yes | yes | yes | fallback policy | authenticated; stamps the owner (SEC-01); a cap on transactions per document (SEC-08) |
| `GET /payments` | `PaymentsEndpoints.cs:23` | no | **all payments** | all | all | fallback policy | the caller's own payments; all for `payments.operate` (SEC-01) |
| `GET /payments/{id}` | `PaymentsEndpoints.cs:28` | no | **any payment** | any | any | fallback policy | own payment, else 404; any for `payments.operate` (SEC-01) |
| `POST /payments/{id}/submit` | `PaymentsEndpoints.cs:33` | no | **any payment** | any | any | fallback policy | owner or `payments.operate` (SEC-01, SEC-11) |
| `POST /payments/{id}/resolve` | `PaymentsEndpoints.cs:39` | no | **any payment** | any | any | fallback policy | `payments.operate`; the sweep (R8, closed) is the automatic path (SEC-11) |
| `POST /partner/notifications` | `PartnerNotificationEndpoints.cs:10` | no | **no (403)** | no | yes | group policy `partner-notifications` (`ApiServiceCollectionExtensions.cs:41`) | `payments.partner.notify` **and** a partner signature or mTLS (R13) |
| `POST /reconciliation/runs/{date}` | `ReconciliationEndpoints.cs:9` | no | **no (403)** | yes | no | group policy `operations` (`ApiServiceCollectionExtensions.cs:42`) | `payments.operate` |
| `GET /payments/{id}/ledger` | `LedgerEndpoints.cs:13` | no | **no (403)** | yes | no | group policy `operations` (`ApiServiceCollectionExtensions.cs:43`) | owner or `payments.operate` (SEC-01) |
| `GET /ledger/balances/{account}/{currency}` | `LedgerEndpoints.cs:18` | no | **no (403)** | yes | no | group policy `operations` (`ApiServiceCollectionExtensions.cs:43`) | `payments.operate` |

Cells in bold are where the answer differs from "authenticated is enough". Before this PR the last four rows read "yes" in the Token column; that was SEC-02 and SEC-04.

**Does any endpoint need a scope beyond "authenticated"?** Yes, and the review's answer, case by case:

| Endpoint | Needs more than "authenticated"? | Finding |
|---|---|---|
| Partner notifications | Yes: the partner's grant, and later its signature | SEC-02 (fixed), R13 |
| Reconciliation runs | Yes: operator | SEC-04 (fixed) |
| Ledger balances | Yes: operator | SEC-04 (fixed) |
| Ledger entries of a payment | Yes: owner or operator | SEC-04 (fixed for the operator rule), SEC-01 (owner) |
| `resolve` | Yes: operator or internal | SEC-11 (open) |
| List payments, get payment, submit | Yes, but by ownership rather than by a scope | SEC-01 (open) |
| Initiate payment | No | none |

### 6.2 The simulated partner (`src/PaymentsBench.PartnerSimulator`)

Open by design: a simulated bank has no callers to authenticate, and its control routes are the point of it. In a deployment the protection is the network, not the application (`--no-public-ips` in `deploy.yml`).

| Method and route | Defined at | Protected by | Should be |
|---|---|---|---|
| `GET /health`, `GET /alive` | `Program.cs:14` (via `MapDefaultEndpoints`) | nothing, anonymous | anonymous (probe) |
| `GET /diagnostics/calls` | `Program.cs:33` | nothing | private network only; reports call counts only |
| `GET /faults` | `Program.cs:35` | nothing | private network only |
| `PUT /faults` | `Program.cs:37` | nothing | private network only; SEC-13 |
| `POST /transfers` | `Program.cs:43` | nothing | private network only; the API is the only caller |
| `GET /transfers/{endToEndId}` | `Program.cs:71` | nothing | private network only |
| `POST /transfers/{endToEndId}/settle` | `Program.cs:76` | nothing | private network only; SEC-13 |
| `POST /transfers/{endToEndId}/return` | `Program.cs:87` | nothing | private network only; SEC-13 |
| `GET /statements/{date}` | `Program.cs:98` | nothing | private network only |

## 7. Priorities and time windows

P1 is *immediately*, and before any deployment that a party you do not control can reach; P2 within a week; P3 within two weeks; P4 long-term. Windows are counted from this review, 2026-10-13.

| Priority | Window | Findings | Status |
|---|---|---|---|
| **P1** | Immediately | SEC-01 Payments have no owner · SEC-02 Webhook open to any token holder · SEC-03 Token issuance unverified | SEC-02 fixed; SEC-01 and SEC-03 open |
| **P2** | By 2026-10-20 | SEC-04 Ledger and reconciliation open · SEC-05 Global idempotency key · SEC-06 Development policy on a deployment · SEC-07 No request cap · SEC-08 No rate limiting · SEC-10 Production environment settings | SEC-04, 06 (Fly), 07 fixed; the rest open |
| **P3** | By 2026-10-27 | SEC-11 `resolve`/`submit` open · SEC-12 No audit trail · SEC-13 Simulator exposure unchecked · SEC-09 End-to-end id global (re-rated) · SEC-14 Org-wide Fly token · SEC-15 Tag pins | open |
| **P4** | Long-term (next review: 2027-01-13) | SEC-16 Host and transport · SEC-17 Paging overflow · SEC-18 Partner responses | open |

An open P2 whose window passes without a decision is the thing this table exists to prevent: if a date is missed, the row is re-dated here with the reason, not left alone.

## 8. Readiness ledger

Legend: **OPEN**, **FIXED**, **NOT APPLICABLE**. "Context" is what already exists that makes the fix cheap. "Owner" is the stream whose files the fix touches. Register rows are in `docs/REGISTER.md`.

### 8.1 Blocks deployment

| ID | Status | Pri | Location | Problem | Context | Action | Owner |
|---|---|---|---|---|---|---|---|
| SEC-01 | OPEN | P1 | `PaymentsEndpoints.cs:23,28,33,39` | A payment has no owner; any token holder reads and submits any payment | `sub` is already on every token; `FindByIdempotencyKeyAsync` and `ListAsync` are the two places the filter goes; the `operations` policy exists for the operator bypass | Add `OwnerId` (domain, migration, both stores); owner-scoped get/list/submit, 404 for others; refuse tokens without `sub`. Until then: deploy single-operator only, with SEC-03 verified | stream A (R21) |
| SEC-02 | FIXED | P1 | `PartnerNotificationEndpoints.cs:10` | Webhook open to any token holder | Policy infrastructure in `Identity/AuthorizationPolicies.cs` | Done: grant `payments.partner.notify`, 22 tests in `AuthorizationPolicyTests`. Remaining: partner signature or mTLS (R13) | stream B |
| SEC-03 | OPEN | P1 | `flyio/authservice.fly.toml:15`, `docs/DEPLOYMENT.md` | Who can obtain a token is not decided or checked here | The estate runs its own authservice instance, so its configuration is ours to set | Read the deployed version's registration, default-claim, hashing and rate-limit configuration; record the answers; close registration or make grants non-self-assignable | stream B + authservice operator (R26) |
| SEC-10 | OPEN | P2 | `deploy.yml:17-19,57,210,291,382,440`, `docs/DEPLOYMENT.md` | Production environment restrictions were "if you want" | The workflows already reference `environment: production` everywhere, so the gate is a settings change | Document half done (`docs/DEPLOYMENT.md` says "must"). Remaining: set required reviewer, deployment branches/tags = `v*` and the tag ruleset on the live repository; record the verification date | stream B (R26) |

### 8.2 Should be done before deployment

| ID | Status | Pri | Location | Problem | Context | Action | Owner |
|---|---|---|---|---|---|---|---|
| SEC-04 | FIXED | P2 | `LedgerEndpoints.cs:13,18`; `ReconciliationEndpoints.cs:9` | Ledger and reconciliation open to any token holder | Same policy infrastructure as SEC-02 | Done: grant `payments.operate`. Remaining: owner access to a payment's own ledger (with SEC-01) | stream B |
| SEC-05 | OPEN | P2 | `InitiatePayment.cs:86-91`; `PaymentsDbContext.cs:27` | Idempotency key is global: cross-caller replay, existence oracle, squatting | Needs the owner from SEC-01; the index and the lookup are one line each | Unique on `(OwnerId, IdempotencyKey)`; lookup by both. Blocks any multi-caller deployment together with SEC-01 | stream A (R21) |
| SEC-06 | FIXED (Fly) | P2 | `IdentityRegistration.cs:42-50` | Development anonymous policy one variable from an open API | The anonymous branch is one `if` | Done for Fly (`FLY_APP_NAME`). Follow-up: explicit `Auth__AllowAnonymous` opt-in so the environment name alone cannot open the API | stream B |
| SEC-07 | FIXED | P2 | `Extensions.cs:51` | 30 MB default request body | ServiceDefaults is shared by the API and the simulator | Done: 64 KiB default, configurable; 413 verified on a real Kestrel | stream B |
| SEC-08 | OPEN | P2 | `ApiServiceCollectionExtensions.cs:31-44` | No rate limiting | `AddRateLimiter` is in the framework; `sub` is on every token | Per-`sub` limiter, anonymous limiter by trusted client IP, 429 problem, limits as configuration; ADR; re-measure the load numbers | stream B (R23) |
| SEC-09 | OPEN | P3 | `InitiatePayment.cs:84-111`; `EfCoreStores.cs:284-288`; `PaymentsDbContext.cs:28` | End-to-end id is global: another caller's id answers 409 (oracle, squatting) with a misleading message | The stores already map the duplicate to a 409; the handler can check first | Owner-scoped uniqueness with SEC-01; a duplicate-id problem of its own | stream A (R22) |
| SEC-11 | OPEN | P3 | `PaymentsEndpoints.cs:33,39` | `resolve`/`submit` open to every authenticated caller | The `operations` policy exists; one `.RequireAuthorization("operations")` on the resolve route | Apply it; owner rule for `submit` after SEC-01 | stream A (R24) |
| SEC-12 | OPEN | P3 | `InitiatePayment.cs`, `SubmitPayment.cs`, `ApplyPartnerNotification.cs` | No actor on commands, events or logs | `sub` is on the principal; the log scope is one middleware | `Actor` on commands and outbox events; `sub` in the request log scope | stream A (R24) |
| SEC-13 | OPEN | P3 | `PartnerSimulator/Program.cs:35,37,76,87`; `partner.fly.toml:4-6` | Simulator exposure rests on one unchecked flag | `flyctl ips list --json` is scriptable | Post-deploy check that the partner has no address; dedicated Fly org; control-route secret | stream B (R26) |
| SEC-14 | OPEN | P3 | `docs/DEPLOYMENT.md`, `deploy.yml:59,212,293,384,442` | Org-wide Fly token | `FLY_ORG` is already a variable | Dedicated org; per-app deploy tokens after first deployment; rotation schedule | stream B (R26) |
| SEC-15 | OPEN | P3 | actions (all workflows); `secret-scan.yml:46`; `authservice.fly.toml:15`, `postgres.fly.toml:24`; `AppHost.cs:27`; both `Dockerfile`s | Tags, not digests | Dependabot already covers github-actions and docker | SHA-pin actions; digest-pin authservice and postgres | stream B (R26) |
| SEC-16 | OPEN | P4 | `appsettings.json:15` (API and simulator); `api.fly.toml:44` | No host filter, HSTS or forwarded-headers handling | `force_https` already redirects | `AllowedHosts` after learning Fly's health-check Host; document https-only clients | stream B (R26) |
| SEC-17 | OPEN | P4 | `PaymentQuery.cs:29` | `Skip` overflows `int` | `PaymentQuery.Create` already clamps `Limit` | `long` arithmetic and a page maximum | stream A (R25) |
| SEC-18 | OPEN | P4 | `HttpPartnerGateway.cs:59`; `InfrastructureRegistration.cs:76-98` | Partner responses trusted unbounded | The unknown-outcome path already exists | `TryParse` to unknown; response size cap; validate statement lines | stream A (R25) |

### 8.3 Not applicable

| Item | Status | Because | Evidence |
|---|---|---|---|
| SQL injection | NOT APPLICABLE | EF Core, no raw SQL | section 3.3 |
| OS command injection | NOT APPLICABLE | no process is started | section 3.3 |
| XSS, markdown, HTML rendering | NOT APPLICABLE | JSON only, no frontend | section 3.3 |
| Insecure deserialization | NOT APPLICABLE | `System.Text.Json`, no type information | section 3.3 |
| Path and file-name validation (guide section 6) | NOT APPLICABLE | no request value reaches the file system | section 3.3 |
| CORS, cookies, CSRF | NOT APPLICABLE | bearer tokens, no browser client | section 3.3 |
| Tokens in browser storage (guide section 4) | NOT APPLICABLE | no frontend | section 3.3 |
| Password hashing, confirmation, reset | NOT APPLICABLE here | authservice's; checked under SEC-03 | section 3.3 |
| SSRF | NOT APPLICABLE (clean) | the one outbound address is configuration | section 3.3 |

## 9. What this PR changed

| Change | Files | Tests |
|---|---|---|
| Two grants and the policies that need them; webhook, ledger and reconciliation moved out of "any authenticated caller" | `src/PaymentsBench.Api/Identity/AuthorizationPolicies.cs` (new), `IdentityRegistration.cs`, `ApiServiceCollectionExtensions.cs` (three group lines) | `AuthorizationPolicyTests` (22 cases) |
| The anonymous Development policy refuses to start on Fly | `IdentityRegistration.cs:42-50` | `AuthorizationPolicyTests` (2 cases) |
| 64 KiB request body cap, `Server` header removed | `src/PaymentsBench.ServiceDefaults/Extensions.cs` | `RequestLimitsTests` (7 cases), plus the real-Kestrel run |
| Positive findings pinned | none | `SecurityPostureTests` (11 cases) |
| The deploy document states the `production` environment protections as requirements, and the pre-deployment checks as a bootstrapping step; a wrong ADR reference in `api.fly.toml` corrected | `docs/DEPLOYMENT.md`, `flyio/api.fly.toml` | none (documentation) |
| JWT test host extracted so the new tests do not copy it | `tests/PaymentsBench.Api.Tests/JwtTestHost.cs` (new), `IdentityTests.cs` | the existing identity tests, unchanged in meaning |

Decisions, because nobody can be asked (recorded as [ADR 0016](../adr/0016-scoped-grants-for-the-webhook-and-back-office-reads.md)):

1. **A grant is a `role` value or one value of the `scope` claim.** The token's shape is authservice's choice and cannot be read from here, so both are accepted; claim names are as issued (inbound claim mapping is off). The default names are `payments.partner.notify` and `payments.operate`; `Auth:Grants:PartnerNotifications` and `Auth:Grants:Operations` rename them.
1. **Fail closed.** If authservice does not mint these grants, the four endpoints answer 403 to everyone, including the operator. The simulated partner never calls the webhook itself (the scenarios and the README post to it by hand), and the k6 load script runs against the Development policy, where both policies are satisfied by anyone. A deployment that needs them gets them by configuring authservice, which is SEC-03's checklist.
1. **Development anonymous mode is unchanged** apart from the Fly guard: with no authority there is no authentication scheme to challenge, so both policies are `RequireAssertion(_ => true)` there.
1. **The routes are not edited.** The grants are applied as groups around the endpoint maps in `UsePaymentsApi`, so the endpoint files (stream A's) are untouched and identity stays out of them.
1. **No rate limiter, no ownership model, no `AllowedHosts`** here, for the reasons in SEC-08, SEC-01 and SEC-16.

## 10. Residual risks

What was deliberately not fixed, and why it is acceptable for the launch the ledger describes: a **single-operator, simulated-partner bench**. Each is a decision, not an oversight.

- **No revocation.** Tokens are validated statelessly against the JWKS, with the framework's default 5-minute clock skew. A stolen token works until it expires; lifetime and rotation are authservice's. Acceptable: the estate moves no real money.
- **The webhook is a bearer token with a grant, not a partner signature (R13).** A leaked integration token can still forge notifications. Acceptable while the partner is simulated.
- **The simulator is open and reachable from the whole Fly private network.** Acceptable for a simulator with no public address; SEC-13 asks for the check that keeps it that way.
- **`/metrics` is anonymous when switched on.** Off by default and not set in any Fly config. If anyone sets `Metrics__Prometheus__Enabled` on a public app it publishes route, status and runtime counters. Keep it off, or scrape it over a private listener.
- **The OpenAPI document is served to any token holder outside Development.** The route table is in a public repository; there is no UI.
- **PostgreSQL traffic is plaintext on the private network** (`Host=payments-bench-postgres.internal`, no TLS). The network is Fly's encrypted mesh; acceptable for a bench database with one volume and no standby.
- **Account numbers are stored unencrypted at column level.** Disk encryption belongs to the platform and was not verified here. The numbers are placeholders in a simulated estate.
- **Degraded storage is silent in Production.** If `ConnectionStrings__payments` is missing, the API runs in memory (P8) and reports healthy; the post-deploy check (401 on an unauthenticated request) passes. State then vanishes at restart and splits across instances. The log line is the only signal. A deployment that stages the secret by name, as `deploy.yml` does, makes this unlikely; it is worth a start-up refusal in Production, which is a stream A decision about P8.
- **Third parties.** authservice (pinned, not inspected), Fly's proxy and network, GitHub Actions and GHCR, Microsoft's base images. Their compromise is outside this repository's control; SEC-15 narrows the window.
- **GHCR visibility is set by hand** (the packages must be public for Fly to pull). Nothing sensitive is in the images (P-6), so public is acceptable; nothing verifies the setting afterwards beyond the pre-deploy `docker manifest inspect`.
- **Unverified from this session:** the Docker image contents (no daemon here; reasoned from the Dockerfiles and `.dockerignore`), the integration tests against PostgreSQL (CI runs them), anything about a running estate (none exists), GitHub settings (environment protection, tag rules, private vulnerability reporting).

## 11. The four recurring launch blockers, and the checklist

Guide section 9, checked explicitly:

1. **Production CORS origins never configured.** Not applicable: no browser client and no CORS configuration, so browsers deny cross-origin reads (section 3.3).
1. **Email confirmation disabled "temporarily".** Not in this repository; it is authservice's setting and is part of SEC-03's read.
1. **Rate limiting present in *most* services.** Absent in the API (SEC-08), unknown in authservice (SEC-03): this is the one that matches the guide's warning.
1. **Token storage inconsistent between frontends.** Not applicable: no frontend.

Guide section 10 checklist:

- [x] Category list worked through; every N/A justified with evidence; positive findings recorded; pentest caveat stated.
- [x] Findings in the standard format, each with an attack scenario.
- [x] Time-windowed priorities; status ledger with context column; blocks-deploy and before-deploy split; residual risks listed.
- [x] No tokens in web storage (no frontend); signature verification at every gate (the fallback policy); header set not applicable to a JSON-only API.
- [x] CSPRNG for anything presentable as proof (nothing is); password-hash parameters recorded (not ours; SEC-03).
- [x] Path and name validation: not applicable, with evidence.
- [x] Encode at render: not applicable (no rendering); OpenAPI behind the token outside Development.
- [x] Deny-by-default with a short anonymous list; endpoint x role matrix produced; no identity by email; CORS not applicable.
- [x] The four recurring launch blockers checked explicitly.

## 12. Closing

This review read the code, the configuration and the workflows, ran the fixes' tests, and exercised a few behaviours against the real API on one machine. It did not send crafted traffic at a running estate, fuzz an input, test authservice, the Fly platform or the network path, or try to break out of a container. **This is a static review, not a penetration test.**
