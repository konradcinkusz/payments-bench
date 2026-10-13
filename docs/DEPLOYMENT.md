# Deploying the service

How the payment service reaches Fly.io, what it costs to keep there, and what a person has to do
once. This covers the **service**. The documentation site is published by a different pipeline;
see [DEPLOYMENT-DOCS.md](DEPLOYMENT-DOCS.md).

Everything here is **simulated money**: the partner app is the simulated bank, the data is
invented, and nothing in the estate talks to a real bank (register row R1). Nothing has been
deployed yet either (register row, "Nothing has been deployed"): this file describes what the first
tag will do, not what has been done.

Principles served: P7 (Fly.io, a cost-shaped topology) and P12 (tag-driven CI/CD with change
detection and ordered deploy), and the shared-service rules for running someone else's service as
your own instance.

## Topology

```text
                    public internet
                          |
          +---------------+----------------+
          |                                |
payments-bench-api  --JWKS (https)--> payments-bench-authservice
   (public, auth)                       (public, signs RS256)
          |                                |
          | http, 6PN                      | 6PN
          v                                v
payments-bench-partner              payments-bench-postgres
 (SIMULATED bank, no public IP)      (no public listener; one volume)
          ^                                ^
          |                                |
          +-- API ------ 6PN ------------- +  databases: payments, authservice
```

| App | What it is | Public | Machines | Config |
|---|---|---|---|---|
| `payments-bench-api` | The payment API, image built from this repository | yes, `https://payments-bench-api.fly.dev` | 1, always on | [`flyio/api.fly.toml`](../flyio/api.fly.toml) |
| `payments-bench-partner` | The **simulated** partner bank | no public IP | 1, always on | [`flyio/partner.fly.toml`](../flyio/partner.fly.toml) |
| `payments-bench-authservice` | This estate's own instance of the shared identity service, from its pinned image | yes, `https://payments-bench-authservice.fly.dev` | 1, always on | [`flyio/authservice.fly.toml`](../flyio/authservice.fly.toml) |
| `payments-bench-postgres` | One PostgreSQL 17 server, volume `pgdata` | no public listener | 1, never scaled | [`flyio/postgres.fly.toml`](../flyio/postgres.fly.toml) |

Region `fra` for all of it, so every in-request hop stays in one region and the volume has one home.

### Decisions

- **One PostgreSQL app, two databases** (`payments`, `authservice`), each with its own role and no
  cross-grants (P3; shared-service-reuse §5). The first-boot script in `postgres.fly.toml` creates
  them and revokes `PUBLIC` connect, so the `payments` role cannot even connect to the
  `authservice` database. Splitting the server later is a connection-string change.
- **Authservice is the consumer's own instance**, named for this system, with its own database and
  its own signing key (shared-service-reuse §1, §4). It is deployed from
  `ghcr.io/konradcinkusz/authservice:v0.3.4`, pinned in one place (`flyio/authservice.fly.toml`),
  never `:latest`, never built here. Its issuer and audience are `PaymentsBench`, and the API's
  `Auth__Issuer` and `Auth__Audience` match; a token minted for another product is refused.
- **The API's authority is authservice's public https URL**, not `.internal` or `.flycast`. The API
  requires https metadata outside Development, and `.internal` and `.flycast` are plain http with
  no certificate for those names, so metadata would be refused. The public name has a real
  certificate, and the proxy starts a stopped machine, which `.internal` would not. The cost is
  one hop out through the Fly proxy and back, once per metadata refresh, not per request.
- **The API reaches the simulated partner at `http://payments-bench-partner.internal:8080`.** The
  partner pins a machine (`min_machines_running = 1`), so `.internal`'s one weakness, that it does
  not wake a stopped machine, does not apply; it needs no private IP allocation and the partner
  needs no public one. The partner is deployed with `--no-public-ips`: its fault controls
  (`PUT /faults`) are open on purpose and must never face the internet.
- **`ASPNETCORE_ENVIRONMENT=Production`, never Development.** In Development the API allows
  anonymous calls (the identity ADR in PR #14, still in review when this was written);
  outside it the API refuses to start without `Auth__Authority`, and with one it requires issuer
  and audience. After each deploy the workflow asserts that an unauthenticated payments request is
  refused with 401.
- **Images are built once per tag** and pushed to
  `ghcr.io/konradcinkusz/payments-bench-<service>:<tag>` by the build job, which has `packages:
  write` and only `GITHUB_TOKEN`; they are deployed with `flyctl deploy --image`. Publishing needs
  no Fly credential, so the build job runs even when there is no Fly token. Fly pulls from GHCR
  anonymously, so the packages must be public (see the first-tag procedure); the repository is
  public and the images hold no secret.

## Cost

Figures are Fly's published list prices as remembered at the time of writing, **not an invoice and
not measured**; check [Fly's pricing page](https://fly.io/docs/about/pricing/) before relying on
them. The reasoning is the part that is meant to last.

| App | Size | Idle cost shape | Why it is not scaled to zero |
|---|---|---|---|
| `payments-bench-api` | shared-cpu-1x, 512 MB | about USD 3 a month | A client waits on it while it calls the partner; its 5 s partner timeout is far below a cold start plus first-boot migration |
| `payments-bench-partner` | shared-cpu-1x, 256 MB | about USD 2 a month | The API calls it inside a payment request over `.internal`, which does not wake a stopped machine |
| `payments-bench-authservice` | shared-cpu-1x, 512 MB | about USD 3 a month | Every validator fetches its JWKS on first request and on every cache expiry: it is on the synchronous path of the API |
| `payments-bench-postgres` | shared-cpu-1x, 1 GB, 3 GB volume | about USD 6 a month plus the volume | Stateful; scaling it to zero would only add a cold database to every request |

That is roughly USD 15 a month for the estate, from list prices. Shared IPv4 addresses are free; a
dedicated IPv4 (about USD 2 a month) is not allocated.

- **What runs when nothing is happening:** four machines, all of them on purpose. Every app is on
  a synchronous request path, so none scales to zero (`min_machines_running = 1`, each with the
  call that forces it written next to the field).
- **The cheaper option and its price:** let the API and the partner scale to zero and save about
  USD 5 a month, at the price of a multi-second stall on the first payment after idle and, for the
  partner, a failed submit, because `.internal` cannot wake it. Moving the partner to `.flycast`
  would fix that but needs a private IP allocation and a proxy hop for no gain at this size.
- **What is off the table:** a public listener on PostgreSQL; sharing one database between
  services; turning off `force_https`; a public address for the simulated partner; pointing the
  estate at another project's authservice or sharing a signing key with one; `:latest` anywhere;
  high availability (a second Postgres machine would be a second, empty volume, not a replica).

## Secrets

Names only. No value is in this repository, and none should ever be pasted into an issue, a pull
request or a log.

### GitHub environment `production`

Create the environment `production` (Settings > Environments); the deploy workflow references it,
so GitHub creates an empty one on first use, and an empty one protects nothing. Before the first
tag, set all three of these:

- **Required reviewers**: a human gate before any job that holds a secret.
- **Deployment branches and tags**: selected, `v*` tags only (and `main` if a manual run is ever
  wanted). Otherwise anyone with write access can run a workflow from a branch of their own, with
  a step that prints the secrets below.
- **A tag ruleset** (Settings > Rules) that lets only maintainers create `v*` tags. Otherwise a tag
  on an unreviewed commit builds, pushes and deploys that commit.

Record the date you checked them in the pull request that tags the release (security review
SEC-10).

| Name | Kind | Used for | How to generate |
|---|---|---|---|
| `FLY_API_TOKEN` | secret | Every `flyctl` step. Absent: the workflows write a summary and finish green | `fly tokens create org` |
| `FLY_ORG` | variable, optional | Organisation for new apps; default `personal` | not a secret |
| `POSTGRES_PASSWORD` | secret | Superuser password of `payments-bench-postgres` | `openssl rand -hex 24` |
| `PAYMENTS_DB_PASSWORD` | secret | Password of role `payments` | `openssl rand -hex 24` |
| `AUTHSERVICE_DB_PASSWORD` | secret | Password of role `authservice` | `openssl rand -hex 24` |
| `AUTH_JWT_PRIVATE_KEY_PEM` | secret | Authservice's RS256 signing key, generated once for this estate and never reused | `openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048` |

Passwords are hex so they are safe inside a connection string. Store the signing key somewhere
durable and outside the repository too: losing it signs every user out, and leaking it lets the
holder mint tokens as anyone.

### Fly secrets, set by the pipeline

Nobody runs `fly secrets set` by hand; the deploy job stages them from the GitHub secrets above
and derives each connection string from a password plus a known host, so nothing is stored per
service. A missing root secret fails the job before anything is deployed.

| App | Fly secret | Derived from |
|---|---|---|
| `payments-bench-postgres` | `POSTGRES_PASSWORD`, `PAYMENTS_DB_PASSWORD`, `AUTHSERVICE_DB_PASSWORD` | the three GitHub secrets of the same names; read at first boot only |
| `payments-bench-authservice` | `ConnectionStrings__DefaultConnection` | `AUTHSERVICE_DB_PASSWORD` plus host `payments-bench-postgres.internal`, database and role `authservice` |
| `payments-bench-authservice` | `Jwt__PrivateKeyPem` | `AUTH_JWT_PRIVATE_KEY_PEM` |
| `payments-bench-api` | `ConnectionStrings__payments` | `PAYMENTS_DB_PASSWORD` plus host `payments-bench-postgres.internal`, database and role `payments` |
| `payments-bench-partner` | none | nothing to protect |

Rotation: the postgres first-boot script runs once, on an empty volume, so changing a database
password in GitHub does not change the role. Rotate in this order: `ALTER ROLE ... PASSWORD` over
`fly proxy 15432:5432 --app payments-bench-postgres`, then update the GitHub secret, then redeploy
the app that uses it.

## Bootstrapping

From the pipeline, not a laptop; `fly launch` would write a config nobody reviewed. One-time human
setup:

1. Create a Fly organisation (or use `personal`) and a token: `fly tokens create org`.
1. Create the GitHub environment `production`, set its protections (previous section), and add
   `FLY_API_TOKEN` and the four root secrets from the table above.
1. Clear the "blocks deployment" table of the
   [security review](security/SECURITY-REVIEW.md): who can obtain a token from authservice
   (SEC-03), the environment protections (SEC-10), and the ownership gap (SEC-01), which is
   acceptable only while the token population is the operator alone.
1. After the first tag has pushed images (below), make the two GHCR packages public.

Nothing else: no app creation, no volume creation. Each deploy job creates its app and, for
PostgreSQL, its volume if missing, and change detection always selects a service whose app does not
exist, so the first tag against an empty organisation provisions the whole estate.

## The first tag

1. Merge to `main` with CI green. Check that the authentication change (PR #14) is on `main`:
   without it the API accepts anonymous calls, and the post-deploy check will fail the deploy.
1. Tag and push: `git tag v0.1.0 && git push origin v0.1.0`. The workflow runs `test`,
   `detect-changes` (no previous tag: everything), then `build`.
1. The build pushes `payments-bench-api` and `payments-bench-partner` to GHCR. On the **first** tag
   only, the two packages are private and Fly cannot pull them, so the deploy jobs stop at "The
   image is pullable without credentials". Set each package's visibility to public (the package's
   settings page on GitHub; no token the workflow holds can do this), then use **Re-run failed
   jobs** on the tag's run. The deploy jobs reuse the images already pushed under that tag.
1. Deploys run in order: postgres, authservice (then a JWKS check), partner, api (then a check that
   `/health` is anonymous 200 and an unauthenticated payments request is 401).
1. Walk one authenticated payment against `https://payments-bench-api.fly.dev`: obtain a token from
   `payments-bench-authservice`, `POST /payments` with an `Idempotency-Key`, then submit it. That
   walk closes the register row; the workflow cannot do it for you.

Without `FLY_API_TOKEN` the same tag still runs `test`, change detection and `build`, and each
deploy job writes what it would have deployed to its step summary and finishes green. That is how
this workflow is exercised in CI without an account.

### What a later tag does

Change detection compares against the previous tag. A change to `Directory.Build.props`,
`Directory.Packages.props`, `global.json`, `.dockerignore`, anything under `scenarios/` (both images carry it) or the service defaults rebuilds both
images; a change under `Domain`, `Application`, `Infrastructure` or `Api` rebuilds the API; the
simulator's sources rebuild the partner; a change to a `flyio/*.fly.toml` selects its app. PostgreSQL
redeploys only when its own config changed or the app is missing, because a redeploy restarts it. A
skipped upstream does not block the chain (`success || skipped`), but a failed one does.

## Operating it

- **Scale** (`flyio-scale.yml`, manual): `status`, `wind-down`, `start`, `api-two`, `api-one`. It
  never touches PostgreSQL.
- **Teardown** (`flyio-destroy.yml`, manual): type `destroy payments-bench`. It destroys the API,
  the partner and authservice, in that order, then the PostgreSQL machine. The volume is **kept**
  unless `delete_volume` is set. The next tag, or a `deploy_all` run, brings the estate back onto the
  same data. Both workflows are token-guarded like the deploy.
- **Reach the database:** `fly proxy 15432:5432 --app payments-bench-postgres`.

## What is not proved

The configs are validated offline (`flyctl config validate`) and the workflow logic is exercised
without a token. No machine has run, no image has been pulled by Fly, and the JWKS check, the
post-deploy checks and the first-boot database script have never run against a real Fly
organisation. The register row "Nothing has been deployed" closes only when a tag deploys the estate
and one authenticated payment is walked against the public URL. There is no database backup or
standby: PostgreSQL has one volume.
