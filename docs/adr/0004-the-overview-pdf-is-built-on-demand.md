# ADR 0004: The overview PDF is built on demand, on pull requests touching its sources, and on tags

**Status:** accepted, 2026-10-13

## Context

[`OVERVIEW.md`](../OVERVIEW.md) is the source of truth for the long-form argument. A reader who
will not open a repository wants it as a single PDF. Principle P14 of
[architecture-standards](https://github.com/konradcinkusz/architecture-standards) says
documentation lives in the repository next to what it describes, and the research-documentation
standard treats a PDF as build output. The same choice was taken for the documentation model,
[agent-eval-bench](https://github.com/konradcinkusz/agent-eval-bench), in its ADR 0006.

Three ways to produce the file were available: commit it, build it by hand when someone
remembers, or build it in CI.

## Decision

The PDF is compiled from LaTeX sources in `docs/papers/` by
`.github/workflows/build-overview-pdf.yml` and is never committed. The workflow runs:

- on `workflow_dispatch`, for a build whenever someone wants one;
- on pull requests whose changes touch `docs/papers/**`, `docs/diagrams/**`, the diagram render
  script, the npm package files or the workflow itself, attaching the PDF as an artifact;
- on `v*` tags, attaching the PDF to the release as an asset.

Only the release job holds `contents: write`; the build job holds `contents: read`.

A committed PDF is rejected because a binary in the tree is out of date the moment the Markdown
changes, and every regeneration is an opaque diff. A manual-only build is rejected because here
the documents are a product, not a by-product: a LaTeX error or a stale figure must be caught on
the pull request that causes it, not discovered at release time. The path filter keeps the cost
at zero for the pull requests that do not touch the sources, which is most of them.

## Consequences

- No PDF exists in the repository. A reader gets one from a workflow artifact or a release.
- A pull request that touches the sources carries a LaTeX build in its checks, which is slower
  than the Markdown lint alone.
- The edition can lag `OVERVIEW.md` between releases. The Markdown is the source of truth; the
  PDF header carries the build date and its title block
  names `docs/OVERVIEW.md` as its source; it does not name a commit.
