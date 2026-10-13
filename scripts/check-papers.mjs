#!/usr/bin/env node
/**
 * check-papers.mjs — the LaTeX documents in this repository hold the house
 * style, and their language editions stay in step.
 *
 * See architecture-standards, docs/research/00-RESEARCH-DOCUMENTATION.md,
 * "Checking the papers". Copy this file to <repo>/scripts/check-papers.mjs,
 * fill in the CONFIGURATION block below, and run it in the same lint job as
 * the repository's other documentation checks.
 *
 * WHY THIS EXISTS.
 *
 *   The house preamble's whole claim is that a document from any repository
 *   in the estate looks like it came from the same shop. That claim was false
 *   and nothing said so: `ab-ove`'s two overview editions carried none of the
 *   seven house markers — not the colors, the section formatting, the header,
 *   the status marker, the path macro, the geometry or the link style — while
 *   `agent-eval-bench` and `marcus-shop` carried all seven. A convention
 *   about copying a preamble correctly has nothing to fail, so it failed
 *   quietly for as long as nobody diffed two PDFs side by side.
 *
 *   The Beamer theme, distributed over the same period as a FILE rather than
 *   a block to copy, propagated byte-identically. That is the lesson this
 *   check enforces: \input the house preamble, do not retype it.
 *
 * WHAT IT CHECKS, AND WHAT IT DELIBERATELY DOES NOT.
 *
 *   It checks four things, all of them mechanical:
 *
 *     R1  Every document root \inputs the house preamble.
 *     R2  Every document root defines the preamble's contract before that
 *         \input, so a missing \headerleft is a named lint failure rather
 *         than an "Undefined control sequence" eighty lines into a CI log.
 *     R3  The repository's copy of the house preamble still matches the one
 *         it was taken from, by digest.
 *     R4  Language editions come in pairs, neither half is edited alone, and
 *         a document with editions calls its figures through \dgm rather than
 *         writing a rendered path by hand.
 *
 *   It does NOT compile anything. A build is what catches a broken document;
 *   this catches a document that builds perfectly and is wrong anyway, which
 *   is the failure a green pipeline hides. Run both.
 *
 *   It does NOT check that a translation is correct — no script can. R4's
 *   coupling half checks that somebody looked, which is the failure that
 *   actually bites: a figure renumbered in one edition and left stale in the
 *   other, with nothing going red.
 *
 * Zero dependencies: this runs in the same job as the repository's other
 * documentation checks.
 *
 * Usage: node scripts/check-papers.mjs [base-ref]
 * Exit:  0 = conformant, and coupled if a base ref was given; 1 = not.
 */

import { readdirSync, existsSync, readFileSync, statSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { join } from 'node:path';

// ===========================================================================
// CONFIGURATION — the only part an adopting repository edits.
// ===========================================================================

/**
 * Directories holding LaTeX document roots. Every `*.tex` in one of these is a
 * document root and is checked, EXCEPT the shared files named in SHARED below
 * — those are \inputted, not compiled, so they have no contract to meet.
 */
const PAPER_DIRS = ['docs/papers'];

/**
 * Files inside PAPER_DIRS that are included rather than compiled: the house
 * preamble itself, and any shared section trunk (see the standard, "One trunk,
 * several editions"). Paths are relative to the repository root; a directory
 * listed here exempts everything under it.
 */
const SHARED = ['docs/papers/house-preamble.tex', 'docs/papers/shared'];

/** The repository's copy of the house preamble. */
const HOUSE_PREAMBLE = 'docs/papers/house-preamble.tex';

/**
 * SHA-256 of the HOUSE-PREAMBLE.tex this copy was taken from, so a local edit
 * to the house style is a deliberate act with a visible diff rather than a
 * drift nobody notices. Regenerate after a deliberate update:
 *
 *   shasum -a 256 docs/papers/house-preamble.tex
 *
 * Set to null to skip R3 — appropriate only while a repository is mid-adoption
 * and the row is open in its deviation register.
 */
const HOUSE_PREAMBLE_SHA256 = '8f6cb17b11a2775a161f2400bafc3a2d8300bc538348d7e215da0ed76d2c2ccb';
// Taken from architecture-standards docs/research/HOUSE-PREAMBLE.tex at commit
// 2396c4cb4d892c65cfc0c25c62e35741234287ca, unmodified.

/**
 * Language-edition suffixes this repository publishes, e.g. ['.pl'] for
 * `overview.tex` beside `overview.pl.tex`. Empty means monolingual: R4 is then
 * structural only (it still refuses a stray edition file nothing declares).
 */
const EDITION_SUFFIXES = [];

/**
 * Documents exempt from R1/R2, each with the reason. An exemption is a
 * deviation: it belongs in the repository's deviation register too, with a
 * closing condition. An entry here with an empty reason fails the check —
 * an exemption nobody had to justify is one nobody will revisit.
 */
const EXEMPT = {
  // 'docs/papers/thesis.tex': "the institution's own class; the house style would breach the template",
};

// ===========================================================================

const repoRoot = execFileSync('git', ['rev-parse', '--show-toplevel'], { encoding: 'utf8' }).trim();
const baseRef = process.argv[2];

/** The four commands the house preamble requires before its \input. */
const CONTRACT = ['paperstatus', 'headerleft', 'pdftitleline', 'pdfauthorline'];

const failures = [];
const read = (relative) => readFileSync(join(repoRoot, relative), 'utf8');
const isShared = (relative) => SHARED.some((s) => relative === s || relative.startsWith(`${s}/`));

/** Every `*.tex` under PAPER_DIRS that is a compiled root rather than an include. */
function documentRoots() {
  const roots = [];

  for (const dir of PAPER_DIRS) {
    const absolute = join(repoRoot, dir);

    if (!existsSync(absolute) || !statSync(absolute).isDirectory()) {
      console.error(`check-papers: '${dir}' is declared to hold papers but does not exist.`);
      process.exit(1);
    }

    for (const name of readdirSync(absolute)) {
      const relative = `${dir}/${name}`;

      if (name.endsWith('.tex') && !isShared(relative)) {
        roots.push(relative);
      }
    }
  }

  return roots.sort();
}

const roots = documentRoots();

if (roots.length === 0) {
  console.error('check-papers: no document roots found. That cannot be right — failing loudly.');
  process.exit(1);
}

for (const [file, reason] of Object.entries(EXEMPT)) {
  if (!reason || !reason.trim()) {
    failures.push(`${file} is exempt with no reason given. An exemption nobody had to justify is one nobody will revisit.`);
  }
}

// ---- R1 + R2: the house preamble, and its contract -------------------------

for (const root of roots) {
  if (root in EXEMPT) {
    continue;
  }

  const source = read(root);
  const inputIndex = source.search(/^\s*\\input\{[^}]*house-preamble\}/m);

  if (inputIndex === -1) {
    failures.push(
      `${root} does not \\input the house preamble. ` +
        `Add \\input{house-preamble}, or add the file to EXEMPT with the reason and open a deviation row.`,
    );
    continue;
  }

  // The contract must be met BEFORE the \input, because the preamble reads
  // these while it is being read. Defining them afterwards compiles a
  // document whose header is empty and whose PDF metadata is wrong.
  const before = source.slice(0, inputIndex);

  for (const command of CONTRACT) {
    const defined = new RegExp(`\\\\(newcommand|renewcommand|providecommand|def)\\s*\\{?\\\\${command}\\b`).test(before);

    if (!defined) {
      failures.push(`${root} does not define \\${command} before \\input{house-preamble}.`);
    }
  }
}

// ---- R3: the house preamble has not drifted from its source ---------------

if (HOUSE_PREAMBLE_SHA256) {
  if (!existsSync(join(repoRoot, HOUSE_PREAMBLE))) {
    failures.push(`${HOUSE_PREAMBLE} is missing. Every document here \\inputs it.`);
  } else {
    const actual = createHash('sha256').update(read(HOUSE_PREAMBLE)).digest('hex');

    if (actual !== HOUSE_PREAMBLE_SHA256) {
      failures.push(
        `${HOUSE_PREAMBLE} no longer matches the copy it was taken from.\n` +
          `    expected ${HOUSE_PREAMBLE_SHA256}\n` +
          `    actual   ${actual}\n` +
          `    If the change is deliberate, propose it back to architecture-standards and update ` +
          `HOUSE_PREAMBLE_SHA256 in the same commit.`,
      );
    }
  }
}

// ---- R4: editions pair, couple, and call figures by slug -------------------

const primaryOf = (root) => {
  for (const suffix of EDITION_SUFFIXES) {
    if (root.endsWith(`${suffix}.tex`)) {
      return `${root.slice(0, -`${suffix}.tex`.length)}.tex`;
    }
  }
  return null;
};

const primaries = roots.filter((root) => primaryOf(root) === null);

for (const root of roots) {
  const primary = primaryOf(root);

  if (primary !== null && !roots.includes(primary)) {
    failures.push(`${root} is an edition of ${primary}, which does not exist.`);
  }
}

for (const primary of primaries) {
  for (const suffix of EDITION_SUFFIXES) {
    const edition = `${primary.slice(0, -'.tex'.length)}${suffix}.tex`;

    if (!existsSync(join(repoRoot, edition))) {
      failures.push(`${primary} has no ${suffix} edition. Expected ${edition}.`);
    }
  }
}

// A document that has editions must not write a rendered diagram path by
// hand: two paths per figure is two things to keep in step, and the estate
// has already maintained `b1-reader-loop.pdf` against `b1-reader-loop.pl.pdf`
// by hand in two files. \dgm resolves the edition from one slug.
if (EDITION_SUFFIXES.length > 0) {
  for (const root of roots) {
    const source = read(root);
    const hardcoded = [...source.matchAll(/\\includegraphics(?:\[[^\]]*\])?\{([^}]*rendered\/[^}]*)\}/g)];

    for (const [, path] of hardcoded) {
      failures.push(`${root} includes ${path} by path. Call it by slug instead: \\includegraphics{\\dgm{<slug>}}.`);
    }
  }
}

// The coupling half needs a diff, and CI passes a base ref.
if (baseRef && EDITION_SUFFIXES.length > 0) {
  let changed;

  try {
    changed = execFileSync('git', ['diff', '--name-only', `${baseRef}...HEAD`], { encoding: 'utf8' })
      .split('\n')
      .filter(Boolean);
  } catch {
    console.error(`check-papers: cannot diff against '${baseRef}'. Fetch it first, or omit the argument to run the structural checks alone.`);
    process.exit(1);
  }

  for (const primary of primaries) {
    for (const suffix of EDITION_SUFFIXES) {
      const edition = `${primary.slice(0, -'.tex'.length)}${suffix}.tex`;
      const touchedPrimary = changed.includes(primary);
      const touchedEdition = changed.includes(edition);

      if (touchedPrimary !== touchedEdition) {
        const [edited, stale] = touchedPrimary ? [primary, edition] : [edition, primary];

        failures.push(
          `${edited} was edited and ${stale} was not. ` +
            `If this change genuinely did not need the other edition to move, say so in the pull request.`,
        );
      }
    }
  }
}

// ---- Report ----------------------------------------------------------------

if (failures.length > 0) {
  console.error(`check-papers: ${failures.length} problem${failures.length === 1 ? '' : 's'}.\n`);

  for (const failure of failures) {
    console.error(`  - ${failure}`);
  }

  console.error('');
  process.exit(1);
}

const coupled = baseRef ? `, coupled against ${baseRef}` : '';
console.log(`check-papers: ${roots.length} document root${roots.length === 1 ? '' : 's'} hold the house style${coupled}.`);
