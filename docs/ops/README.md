# Operating the bench: telemetry, metrics, dashboard

Everything here is about a **simulated** system: the partner bank is
`PaymentsBench.PartnerSimulator`, and nothing talks to a real one.

## What the services emit

Both the API and the simulator export traces, metrics and logs over OTLP when
`OTEL_EXPORTER_OTLP_ENDPOINT` is set (the Aspire dashboard is such an endpoint). The service name
defaults to the assembly name unless `OTEL_SERVICE_NAME` is set. Requests to `/health`, `/alive`
and `/metrics` are not traced (P15); they still count in the HTTP metrics.

To prove the path without Aspire, run a local OTLP sink and point both services at it:

```bash
OTEL_SINK_DIR=/tmp/sink otelcol-contrib --config docs/ops/otel-collector-debug.yaml   # needs the contrib build
export OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:4317
ASPNETCORE_URLS=http://localhost:5101 dotnet run --project src/PaymentsBench.PartnerSimulator &
ASPNETCORE_URLS=http://localhost:5100 Partner__BaseAddress=http://localhost:5101 dotnet run --project src/PaymentsBench.Api &
```

then walk a payment as in the root README and look at the collector output.

## `/metrics` (Prometheus)

Off by default. `Metrics__Prometheus__Enabled=true` (configuration key `Metrics:Prometheus:Enabled`)
maps `/metrics` with the OpenTelemetry Prometheus exporter; without it the route is a 404. The
endpoint is explicitly anonymous (a scraper has no bearer token), which is why it has its own
switch and should be reachable only from the scraper's network (metrics-exposition §5).

**Package decision.** `OpenTelemetry.Exporter.Prometheus.AspNetCore` is still prerelease
(`1.19.1-beta.1` at the time of writing, matching the 1.19.1 SDK). We use it, pinned centrally,
because it only loads behind the switch, the alternative (an OpenTelemetry Collector with its
Prometheus exporter) adds a service to run for one scrape target, and the pinned beta is the
package the OpenTelemetry project itself documents for this. If the package is withdrawn or
changes shape, the fallback is the collector route: point `OTEL_EXPORTER_OTLP_ENDPOINT` at a
collector whose `prometheus` exporter Prometheus scrapes, and switch `/metrics` off.

### Label bounds (metrics-exposition §1)

Every label, what bounds it, and who enforces the bound:

| Metric | Label | Distinct values | Bound enforced by |
|---|---|---|---|
| `payments_transitions_total` | `status` | 6 (`Initiated`, `Submitted`, `Accepted`, `Settled`, `Rejected`, `Returned`; notification kinds reuse three of these) | `PaymentStatus` / `NotificationKind` enums, `.ToString()` |
| `payments_submitted_oldest_age_seconds` | none | 1 series | no tags |
| `payments_sweep_resolved_total` | `outcome` | 5 (`ResolutionOutcome` enum) | the enum |
| `http_server_request_duration_seconds*` | `http_request_method`, `http_response_status_code`, `http_route`, `url_scheme` | route templates (about 12), methods, status codes | route *templates*, never raw paths; the ServiceDefaults view drops every other tag, `error.type` included |
| `http_client_request_duration_seconds*` | method, status code, `server_address`, `server_port`, `url_scheme` | the API calls one host, the partner | a view allow-list; the host comes from configuration |
| `dotnet_*`, `kestrel_*`, `aspnetcore_*` | runtime tags (GC generation, pool, etc.) | fixed by the runtime | the instrumentation libraries |
| all | `otel_scope_name` | 4 or so (meter names) | the registered meters |

No label carries a payment id, end-to-end id, account, idempotency key or error text. The SDK's
own per-metric limit (2000 series) is the backstop; a metric hitting it reports under
`otel_metric_overflow`.

Nothing here is truncated by the services themselves (no top-N or per-entity view exists), so
there is no ceiling or drop-count series to export (§2, §3). If a per-payment or per-tenant view
is ever added it must be off by default, capped, and export its own ceiling and drop count.

No metric is a pre-averaged gauge: the dashboard reads counters with `rate`/`increase`, and the one
latency instrument is a histogram (`_sum`/`_count`/`_bucket`), so rollups stay correct (§4).

### Instruments owned elsewhere

`payments.transitions` is emitted by the API on `main`. `payments.submitted.oldest_age_seconds`
and `payments.sweep.resolved` are implemented by stream A's sweeper (`docs/plans/stream-a-core.md`,
slice A5); until it lands their dashboard panels show no data, and the alert never fires.

## Grafana and Prometheus, locally

```bash
Metrics__Prometheus__Enabled=true ASPNETCORE_URLS=http://localhost:5100 dotnet run --project src/PaymentsBench.Api
docker compose -f docs/ops/docker-compose.yml up
```

Grafana at <http://localhost:3000> (anonymous viewer, local only) has the **Payments bench**
dashboard already loaded; Prometheus is at <http://localhost:9090>. Everything is provisioned from
the repository (§6):

| File | Purpose |
|---|---|
| `grafana/prometheus.yml` | scrape config: the API's `/metrics` every 15 s |
| `grafana/alerts.yml` | the stuck-Submitted alert rule |
| `grafana/provisioning/datasources/prometheus.yml` | datasource, uid `prometheus` |
| `grafana/provisioning/dashboards/provider.yml` | file provider that loads the JSON below |
| `grafana/payments-bench.json` | the dashboard: transitions by status, oldest Submitted age with the threshold line, sweep resolved by outcome |
| `docker-compose.yml` | Prometheus + Grafana, pinned images |

The compose file was written without a container runtime in the authoring session, so it has been
validated as YAML and JSON but not started; the first person to run it should report problems.

## Alert threshold

**`payments_submitted_oldest_age_seconds > 300` for 2 minutes pages.**

A payment sits at Submitted while its outcome is unknown. The resolver sweep (stream A) is meant
to run every 30 s and pick up payments older than about 60 s, so a healthy bench never shows an
oldest age much above two minutes. 300 s is 2.5 times that: long enough that a slow partner or one
failed sweep does not page, short enough that money pending at an unreachable bank is noticed in
minutes. The `for: 2m` clause stops a single scrape spike from paging. Tune the number with the
sweeper's `Sweeper__Interval` and `Sweeper__MinimumAge` (alert threshold ≥ 2.5 × (interval +
minimum age)); the dashboard's red line and `grafana/alerts.yml` must be changed together.
Register row R8 ("an alert threshold documented") can link here.
