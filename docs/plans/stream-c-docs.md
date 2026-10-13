# Stream C plan: documentation, overview, PDF, site

Stream C owns the reading surface of this repository: the long-form overview, the findings
report, the diagrams, a PDF edition of the overview, a generated static site, and the
documentation lint that keeps all of them honest. Streams A (core and correctness) and B
(platform) own the code; this stream describes only what has merged on `main`, never ahead of it.

Principle served: P14 (documentation lives in the repository, next to what it describes) from
[architecture-standards](https://github.com/konradcinkusz/architecture-standards), and the
research-documentation standard's track for documents that borrow the house style without
being studies. The documentation model is
[agent-eval-bench](https://github.com/konradcinkusz/agent-eval-bench): its approach and
structure are reproduced, its prose is not.

## Scope

Owned by this stream: `docs/` except `docs/plans/stream-a-*.md`, `docs/plans/stream-b-*.md`,
register rows R2-R10 and ADRs other streams add; `README.md` (shared with A and B: rebase
before every merge, never revert their lines); `package.json` for documentation tooling;
`.markdownlint*`; `.github/workflows/build-overview-pdf.yml` and
`.github/workflows/deploy-pages.yml`; the documentation lint job in `.github/workflows/ci.yml`.

## Rules every slice follows

- Every number names the file it was measured in. Nothing is invented; the register is
  quoted, not paraphrased into something rosier.
- The partner is simulated; every document says so wherever a reader could assume otherwise.
- The site loads nothing external: no scripts, fonts, images or stylesheets from anywhere, and
  it renders from `file://`.
- Generated output is never committed except `docs/index.html`, which is generated and checked
  for staleness in CI (ADR to record why).
- No secrets; no force-push to `main`; one PR per slice against `main`, CI green before merge.

## Slices

| # | Slice | Branch | Owner | Depends on | PR |
|---|---|---|---|---|---|
| C0 | This plan | `claude/stream-c/plan` | lead | - | |
| C1 | Doc tooling: `package.json` (pinned `markdownlint-cli2`, `@mermaid-js/mermaid-cli`), `.markdownlint-cli2.jsonc` + `.markdownlint.jsonc`, `scripts/check-links.mjs`, a `lint-docs` job in `ci.yml`, existing Markdown made lint-clean, LaTeX block in `.gitignore` | `claude/stream-c/tooling` | sub-session | C0 | |
| C2 | Diagrams: `docs/diagrams/*.mmd` (money path, state machine, submit/resolve sequence, outbox/inbox, reconciliation, delivery topology), `scripts/render-diagrams.mjs`, `scripts/check-diagrams.mjs`, `docs/DIAGRAMS.md`, `lint:diagrams` in the lint job | `claude/stream-c/diagrams` | sub-session | C1 | |
| C3 | `docs/START-HERE.md` (Diátaxis map), `docs/FINDINGS.md` (F-1, F-2 with evidence), `docs/OVERVIEW.md` (long-form source of truth) | `claude/stream-c/overview` | lead | C1 | |
| C4 | PDF track: `docs/papers/house-preamble.tex`, `docs/papers/payments-bench-overview.tex`, `scripts/check-papers.mjs` in the lint job, `.github/workflows/build-overview-pdf.yml` (dispatch, PRs on `docs/papers/**` or `docs/diagrams/**` as artifact, `v*` tags as release asset); compiled locally before merge | `claude/stream-c/pdf` | lead | C2, C3 | |
| C5 | Site: `scripts/site-build.mjs` (Node stdlib only, `--check`), `docs/site/source.html`, `docs/site/status.json`, generated `docs/index.html` with hand-drawn inline SVG, `site:check` in the lint job | `claude/stream-c/site` | sub-session | C1 | |
| C6 | Publishing and front door: `.github/workflows/deploy-pages.yml`, `docs/DEPLOYMENT-DOCS.md` (the one manual Pages step), `README.md` restructured on agent-eval-bench's headings, ADRs for this stream's decisions at the next free numbers | `claude/stream-c/publish` | sub-session | C3, C4, C5 | |
| C7 | Final consistency pass after A and B report done: numbers in OVERVIEW, README, site and PDF equal what `main` measures; register closed rows match; PDF and site rebuilt | `claude/stream-c/final` | lead | A, B, C1-C6 | |

C2, C3 and C5 run in parallel once C1 has merged. The overview and its LaTeX edition carry the
argument and are written by the lead, not delegated.

## Decisions this stream will record as ADRs

1. The overview PDF is built on demand, on pull requests that touch its sources, and on `v*`
   tags as a release asset; never committed.
1. The site is generated from one source and one facts file by a dependency-free script whose
   `--check` mode fails CI when the committed page is stale; it is not hand-maintained.
1. The site loads no external resource and carries a Content-Security-Policy that allows
   nothing but its own inline style by hash.

## Findings

What building and testing this bench caught, recorded in `docs/FINDINGS.md`. Streams A and B:
add a line here (or in your own plan's findings section) for anything your work catches, with
the evidence, and stream C will write it up.

- F-1: enums serialised as integers broke partner notifications and statement lines until
  `JsonStringEnumConverter` was configured in the API, the simulator and the gateway's
  serializer options. Found by the HTTP walk-through.
- F-2: the resilience pipeline's `TimeoutRejectedException` escaped the gateway's catch and
  turned a timeout-after-accept into a 500 instead of an Unknown outcome; committed-before-call
  still let the resolver recover without a duplicate. Found by the HTTP walk-through.
- F-3, F-4, F-5 (open, found while writing `docs/OVERVIEW.md`; the fixes are in the core, so they
  are offered to stream A): the resolver returns `SafeToResubmit` after a resubmit whose outcome is
  unknown; a payment settled and later returned is an `OnStatementOnly` break for its settlement
  day; the partner client's retry predicate never matches an exception, so a `GET` that times out
  is not retried. Evidence for each is in `docs/FINDINGS.md`.

## Done means

C1-C6 merged with CI green including the docs lint job; `build-overview-pdf.yml` green on its
PR with the artifact present; the PDF compiled locally at least once; this file ticks each
slice with its PR number; C7 done after streams A and B finish.
