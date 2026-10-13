#!/usr/bin/env node
// Verifies that every Mermaid diagram exists twice and that the two copies agree:
// inline in docs/DIAGRAMS.md (the form GitHub renders) and as docs/diagrams/<id>-<slug>.mmd
// (the form that can be rendered, diffed and reused on its own).
//
// The join key is the section id: `### A1. Money path` owns the file `a1-*.mmd`.
// Node standard library only, so it runs before anything has been installed.
//
//   node scripts/check-diagrams.mjs      exit 0 = every pair present and identical, 1 = not

import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = dirname(dirname(fileURLToPath(import.meta.url)));
const DOC = join(ROOT, 'docs', 'DIAGRAMS.md');
const DIR = join(ROOT, 'docs', 'diagrams');

for (const [path, what] of [[DOC, 'docs/DIAGRAMS.md'], [DIR, 'docs/diagrams/']]) {
  if (!existsSync(path)) {
    console.error(`check-diagrams: ${what} is missing.`);
    process.exit(1);
  }
}

// Sections, and the first mermaid fence under each heading.
const sections = [];
let current = null;
let fence = null;
for (const line of readFileSync(DOC, 'utf8').split('\n')) {
  const heading = line.match(/^###\s+([A-Z]\d+)\.\s+(.+)$/);
  if (fence === null && heading) {
    current = { id: heading[1].toLowerCase(), title: heading[2].trim(), source: null };
    sections.push(current);
  } else if (fence === null && line.trim() === '```mermaid') {
    fence = [];
  } else if (fence !== null && line.trim() === '```') {
    if (current && current.source === null) current.source = `${fence.join('\n')}\n`;
    fence = null;
  } else if (fence !== null) {
    fence.push(line);
  }
}

if (sections.length === 0) {
  console.error('check-diagrams: docs/DIAGRAMS.md declares no "### A1. ..." sections.');
  process.exit(1);
}

const files = readdirSync(DIR).filter((f) => f.endsWith('.mmd'));
const claimed = new Set();
const failures = [];

for (const s of sections) {
  const label = `${s.id.toUpperCase()} ("${s.title}")`;
  if (s.source === null) {
    failures.push(`Section ${label} has no mermaid block.`);
    continue;
  }
  const matches = files.filter((f) => f.startsWith(`${s.id}-`));
  if (matches.length !== 1) {
    failures.push(
      matches.length === 0
        ? `Section ${label} has no file; expected docs/diagrams/${s.id}-<slug>.mmd.`
        : `Section ${label} matches more than one file: ${matches.join(', ')}.`,
    );
    continue;
  }
  claimed.add(matches[0]);
  if (readFileSync(join(DIR, matches[0]), 'utf8') !== s.source) {
    failures.push(
      `docs/diagrams/${matches[0]} and section ${label} of docs/DIAGRAMS.md differ. They are one diagram: copy the edited one over the other.`,
    );
  }
}

for (const f of files) {
  if (!claimed.has(f)) failures.push(`docs/diagrams/${f} has no matching section in docs/DIAGRAMS.md.`);
  if (/[^\x00-\x7F]/.test(readFileSync(join(DIR, f), 'utf8'))) {
    failures.push(`docs/diagrams/${f} contains non-ASCII characters; the PDF build (pdflatex) needs ASCII source.`);
  }
}

if (failures.length > 0) {
  console.error('check-diagrams: FAILED\n');
  for (const f of failures) console.error(`  - ${f}`);
  process.exit(1);
}
console.log(`check-diagrams: ${sections.length} diagram(s), each paired with docs/diagrams/ and byte-identical.`);
