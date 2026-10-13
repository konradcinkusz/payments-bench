# ADR 0006: The site loads no external resource

**Status:** accepted, 2026-10-13

## Context

A page about a payment rail that fetches scripts, fonts or images from third parties makes a
claim about trust it does not itself keep. It also stops working offline, from a downloaded copy,
or behind a corporate proxy, and every external request is a place the page can change without a
commit.

## Decision

The site works from `file://` and loads nothing from anywhere else:

- a strict Content-Security-Policy (`default-src 'none'`) admits only the one inline `<style>` by
  its hash and images from the page itself or `data:`; it also forbids `base`-URL changes and form
  submission;
- no script at all, so no script source needs to be allowed;
- no web fonts, no external stylesheets, no remote images;
- diagrams are inline SVG, themed by CSS classes so that one drawing serves light and dark.

## Consequences

- System fonts only. The page looks different on different machines and that is accepted.
- The diagrams on the page are hand-drawn SVG, duplicated by the Mermaid sources under
  `docs/diagrams/`. The two can drift apart; the drift is accepted, and the Mermaid
  files are the ones a reader can render on GitHub.
- No interactivity beyond links and what CSS can do.
- The hash in the policy must be regenerated with the style; the generator does it
  ([ADR 0005](0005-the-documentation-site-is-generated.md)).
