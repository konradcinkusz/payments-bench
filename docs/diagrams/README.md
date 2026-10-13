# docs/diagrams/

The Mermaid source for every diagram in [`../DIAGRAMS.md`](../DIAGRAMS.md), one per file, ASCII only
so the PDF build under pdflatex accepts it. Read the pictures in `DIAGRAMS.md`; this directory exists
so a diagram can be rendered, diffed or reused on its own.

| Id | File | Shows |
|---|---|---|
| `A1` | [`a1-money-path.mmd`](a1-money-path.mmd) | ledger accounts and the posting each payment event makes |
| `A2` | [`a2-state-machine.mmd`](a2-state-machine.mmd) | the six payment states and every allowed transition |
| `A3` | [`a3-submit-resolve.mmd`](a3-submit-resolve.mmd) | submit, the three outcomes, and unknown-outcome recovery |
| `A4` | [`a4-outbox-inbox.mmd`](a4-outbox-inbox.mmd) | transactional outbox and deduplicating inbox |
| `A5` | [`a5-reconciliation.mmd`](a5-reconciliation.mmd) | statement vs settled payments, matches and breaks |
| `A6` | [`a6-delivery-topology.mmd`](a6-delivery-topology.mmd) | AppHost composition and the degraded in-memory mode |

## The id-join rule

A section `### A1. ...` in `DIAGRAMS.md` owns the one file whose name starts `a1-`. The filename is
free to describe the diagram; the id is the key. The inline ```` ```mermaid ```` block must be
byte-identical to the file. Every `.mmd` needs a section and every section a `.mmd`.

```bash
npm run lint:diagrams     # check every pair (also part of npm run lint and CI)
npm run render:diagrams   # vector PDFs into docs/diagrams/rendered/ (ignored, never committed)
npm run render:diagrams -- a3 a6   # only matching slugs
```

Rendering needs `npm ci` (pinned `@mermaid-js/mermaid-cli`) and a Chromium; set
`PUPPETEER_EXECUTABLE_PATH` to use one already installed.
