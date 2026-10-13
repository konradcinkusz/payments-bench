#!/usr/bin/env node
// Renders docs/diagrams/*.mmd to vector PDF in docs/diagrams/rendered/<slug>.pdf.
//
//   node scripts/render-diagrams.mjs          every diagram
//   node scripts/render-diagrams.mjs a3 a6    only files whose name contains an argument
//
// One source, two outputs: the same .mmd that check-diagrams.mjs keeps identical to
// docs/DIAGRAMS.md is what the PDF build includes. Rendered output is build output and is
// never committed (docs/diagrams/rendered/ is ignored).
//
// Needs the pinned @mermaid-js/mermaid-cli devDependency (`npm ci`). A browser at
// PUPPETEER_EXECUTABLE_PATH is used if set. Node standard library only.

import { existsSync, mkdirSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

const ROOT = dirname(dirname(fileURLToPath(import.meta.url)));
const SRC = join(ROOT, 'docs', 'diagrams');
const OUT = join(SRC, 'rendered');
const MMDC = join(ROOT, 'node_modules', '.bin', process.platform === 'win32' ? 'mmdc.cmd' : 'mmdc');

if (!existsSync(MMDC)) {
  console.error('render-diagrams: mmdc is not installed. Run `npm ci` first (it is a pinned devDependency).');
  process.exit(1);
}

const filters = process.argv.slice(2);
const sources = readdirSync(SRC)
  .filter((f) => f.endsWith('.mmd'))
  .filter((f) => filters.length === 0 || filters.some((k) => f.includes(k)))
  .sort();

if (sources.length === 0) {
  console.error(
    filters.length
      ? `render-diagrams: no .mmd file matched ${filters.join(', ')}`
      : 'render-diagrams: no .mmd files in docs/diagrams/',
  );
  process.exit(1);
}

mkdirSync(OUT, { recursive: true });

// Containers run as root, where Chromium refuses to start with its sandbox on.
const config = join(OUT, '.puppeteer.json');
writeFileSync(
  config,
  JSON.stringify({
    ...(process.env.PUPPETEER_EXECUTABLE_PATH ? { executablePath: process.env.PUPPETEER_EXECUTABLE_PATH } : {}),
    args: ['--no-sandbox', '--disable-setuid-sandbox', '--disable-dev-shm-usage'],
  }),
);

let failed = 0;
for (const file of sources) {
  const slug = file.replace(/\.mmd$/, '');
  const pdf = join(OUT, `${slug}.pdf`);
  const r = spawnSync(MMDC, ['-i', join(SRC, file), '-o', pdf, '--pdfFit', '-b', 'transparent', '-p', config], {
    cwd: ROOT,
    encoding: 'utf8',
  });
  if (r.status === 0 && existsSync(pdf)) {
    console.log(`  ok    ${slug}.pdf`);
  } else {
    failed += 1;
    console.error(`  FAIL  ${slug}`);
    const detail = (r.stderr || r.stdout || '').trim();
    if (detail) console.error(detail.split('\n').map((l) => `        ${l}`).join('\n'));
  }
}

rmSync(config, { force: true });
console.log(`render-diagrams: ${sources.length - failed}/${sources.length} rendered into docs/diagrams/rendered/`);
process.exit(failed === 0 ? 0 : 1);
