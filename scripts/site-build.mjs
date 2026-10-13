#!/usr/bin/env node
// Generates docs/index.html from docs/site/source.html, docs/site/style.css, docs/site/status.json
// and facts read straight from the repository (register, scenarios, ADRs, findings).
// Node standard library only. Deterministic: the same inputs give byte-identical output.
//   node scripts/site-build.mjs           write docs/index.html
//   node scripts/site-build.mjs --check   write nothing; exit 1 if docs/index.html is stale
import { createHash } from 'node:crypto';
import { existsSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const OUT = 'docs/index.html';
const check = process.argv.includes('--check');

const fail = (message) => {
  console.error(`site-build: ${message}`);
  process.exit(1);
};
const rel = (path) => join(root, path);
const read = (path) => readFileSync(rel(path), 'utf8').replace(/\r\n/g, '\n');
const listDir = (dir, test) =>
  (existsSync(rel(dir)) ? readdirSync(rel(dir)) : []).filter(test).sort();

const esc = (value) =>
  String(value)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');

// Inline markdown for text taken from the repository: escape first, then `code` and **bold**.
const inline = (text) =>
  esc(text)
    .replace(/`([^`]+)`/g, '<code>$1</code>')
    .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');

// ---------------------------------------------------------------- status.json

function loadStatus() {
  let status;
  try {
    status = JSON.parse(read('docs/site/status.json'));
  } catch (error) {
    fail(`docs/site/status.json is missing or not valid JSON: ${error.message}`);
  }
  const facts = new Map();
  const walk = (node, path) => {
    if (node && typeof node === 'object' && !Array.isArray(node) && 'value' in node) {
      const here = path.join('.');
      if (typeof node.source !== 'string' || node.source === '') fail(`status.json: fact "${here}" has no source`);
      if (!existsSync(rel(node.source))) fail(`status.json: fact "${here}" cites "${node.source}", which does not exist in the repository`);
      if (typeof node.asOf !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(node.asOf) || Number.isNaN(Date.parse(node.asOf))) {
        fail(`status.json: fact "${here}" needs an asOf date as YYYY-MM-DD`);
      }
      facts.set(here, node);
    } else if (node && typeof node === 'object' && !Array.isArray(node)) {
      for (const [key, child] of Object.entries(node)) walk(child, [...path, key]);
    } else {
      fail(`status.json: "${path.join('.')}" must be an object with value, source and asOf`);
    }
  };
  walk(status, []);
  for (const key of ['repository', 'tests.domain', 'tests.application', 'mergedPullRequests']) {
    if (!facts.has(key)) fail(`status.json: required fact "${key}" is missing`);
  }
  for (const key of ['tests.domain', 'tests.application']) {
    if (!Number.isInteger(facts.get(key).value) || facts.get(key).value < 0) fail(`status.json: "${key}" must be a non-negative integer`);
  }
  if (!/^https:\/\/github\.com\/[\w.-]+\/[\w.-]+$/.test(facts.get('repository').value)) fail('status.json: "repository" must be a https://github.com/<owner>/<repo> URL');
  const prs = facts.get('mergedPullRequests').value;
  if (!Array.isArray(prs) || prs.some((pr) => !Number.isInteger(pr.number) || typeof pr.title !== 'string')) {
    fail('status.json: "mergedPullRequests" must be a list of { number, title }');
  }
  return facts;
}

// ------------------------------------------------------------------- register

function splitRow(line) {
  return line.trim().replace(/^\|/, '').replace(/\|$/, '').split('|').map((cell) => cell.trim());
}

function loadRegister() {
  const lines = read('docs/REGISTER.md').split('\n');
  const closedAt = lines.findIndex((line) => /^##\s+Closed\b/.test(line));
  const openLines = closedAt === -1 ? lines : lines.slice(0, closedAt);
  const closedLines = closedAt === -1 ? [] : lines.slice(closedAt + 1);
  const open = openLines
    .filter((line) => /^\|\s*R\d+\s*\|/.test(line))
    .map((line) => {
      const [id, since, notProved, closesWhen] = splitRow(line);
      if (!id || !since || !notProved || !closesWhen) fail(`docs/REGISTER.md: malformed register row: ${line}`);
      const headline = (notProved.match(/\*\*(.+?)\*\*/) ?? [])[1] ?? notProved;
      return { id, since, headline, notProved, closesWhen };
    });
  if (open.length === 0) fail('docs/REGISTER.md: no register rows (| R1 | ... |) found');
  const closedRows = closedLines.filter((line) => /^\|/.test(line) && !/^\|\s*-/.test(line) && !/^\|\s*#\s*\|/.test(line)).map(splitRow);
  const closedText = closedLines.map((line) => line.trim()).filter((line) => line !== '' && !/^\|/.test(line));
  return { open, closedRows, closedText };
}

function registerHtml(register) {
  const body = register.open
    .map(
      (row) =>
        `<tr><th scope="row" class="mono">${esc(row.id)}</th><td class="mono nowrap">${esc(row.since)}</td><td>${inline(row.notProved)}</td><td>${inline(row.closesWhen)}</td></tr>`,
    )
    .join('\n');
  return `<div class="tbl-wrap"><table class="tbl">
<thead><tr><th scope="col">#</th><th scope="col">Since</th><th scope="col">Not proved</th><th scope="col">Closes when</th></tr></thead>
<tbody>
${body}
</tbody></table></div>`;
}

function registerClosedHtml(register) {
  if (register.closedRows.length > 0) {
    const body = register.closedRows.map((cells) => `<tr>${cells.map((cell) => `<td>${inline(cell)}</td>`).join('')}</tr>`).join('\n');
    return `<div class="tbl-wrap"><table class="tbl"><tbody>\n${body}\n</tbody></table></div>`;
  }
  const text = register.closedText.length > 0 ? register.closedText.join(' ') : 'None yet.';
  return `<p class="note"><strong>Closed rows:</strong> ${inline(text)}</p>`;
}

// ------------------------------------------------------------------ scenarios

function loadScenarios() {
  return listDir('scenarios', (name) => name.endsWith('.yaml') && name !== 'schemes.yaml').map((file) => {
    const lines = read(`scenarios/${file}`).split('\n');
    const top = (key) => {
      const hit = lines.find((line) => line.startsWith(`${key}:`));
      return hit === undefined ? undefined : hit.slice(key.length + 1).trim();
    };
    const id = top('id');
    const cls = top('class');
    let description = top('description');
    if (!id || !cls || description === undefined) fail(`scenarios/${file}: needs id, class and description`);
    if (/^[>|][+-]?$/.test(description)) {
      const at = lines.findIndex((line) => line.startsWith('description:'));
      description = (lines.slice(at + 1).find((line) => line.trim() !== '') ?? '').trim();
    }
    return { file, id, cls, description };
  });
}

function scenariosHtml(scenarios) {
  const body = scenarios
    .map(
      (s) =>
        `<tr><th scope="row" class="mono"><a href="REPO/blob/main/scenarios/${esc(s.file)}">${esc(s.id)}</a></th><td><span class="chip">${esc(s.cls)}</span></td><td>${inline(s.description)}</td></tr>`,
    )
    .join('\n');
  return `<div class="tbl-wrap"><table class="tbl">
<thead><tr><th scope="col">Scenario</th><th scope="col">Class</th><th scope="col">What it describes (first line of its description)</th></tr></thead>
<tbody>
${body}
</tbody></table></div>`;
}

// -------------------------------------------------------------------- schemes

function schemesHtml() {
  const rows = [];
  for (const line of read('scenarios/schemes.yaml').split('\n')) {
    const item = /^\s*-\s+name:\s*(.+)$/.exec(line);
    if (item) {
      rows.push({ name: item[1].trim() });
      continue;
    }
    const pair = /^\s+(currency|cutOff|settlementBusinessDays):\s*(.+)$/.exec(line);
    if (pair && rows.length > 0) rows[rows.length - 1][pair[1]] = pair[2].trim().replace(/^"(.*)"$/, '$1');
  }
  if (rows.length === 0 || rows.some((r) => !r.currency || !r.cutOff || r.settlementBusinessDays === undefined)) fail('scenarios/schemes.yaml: each scheme needs name, currency, cutOff and settlementBusinessDays');
  const body = rows
    .map((r) => `<tr><th scope="row" class="mono">${esc(r.name)}</th><td class="mono">${esc(r.currency)}</td><td class="mono">${esc(r.cutOff)}</td><td class="mono">${esc(r.settlementBusinessDays)}</td></tr>`)
    .join('\n');
  return `<div class="tbl-wrap"><table class="tbl">
<thead><tr><th scope="col">Scheme</th><th scope="col">Currency</th><th scope="col">Cut-off</th><th scope="col">Settlement business days</th></tr></thead>
<tbody>
${body}
</tbody></table></div>`;
}

// ----------------------------------------------------------------------- ADRs

function loadAdrs() {
  return listDir('docs/adr', (name) => /^\d{4}-.+\.md$/.test(name)).map((file) => {
    const lines = read(`docs/adr/${file}`).split('\n');
    const heading = lines.find((line) => line.startsWith('# '));
    if (!heading) fail(`docs/adr/${file}: no "# " title`);
    const status = (lines.find((line) => line.startsWith('**Status:**')) ?? '').replace('**Status:**', '').trim();
    return { file, title: heading.slice(2).trim(), status };
  });
}

function adrsHtml(adrs) {
  const items = adrs
    .map(
      (a) =>
        `<li><a href="REPO/blob/main/docs/adr/${esc(a.file)}">${esc(a.title)}</a>${a.status ? ` <span class="chip">${esc(a.status)}</span>` : ''}</li>`,
    )
    .join('\n');
  return `<ul class="plain">\n${items}\n</ul>`;
}

// ------------------------------------------------------------------- findings

function loadFindings() {
  if (!existsSync(rel('docs/FINDINGS.md'))) return null;
  const lines = read('docs/FINDINGS.md').split('\n');
  const found = [];
  for (let i = 0; i < lines.length; i += 1) {
    const match = /^##\s+(F-\d+)\.\s+(.+)$/.exec(lines[i]);
    if (!match) continue;
    found.push({ id: match[1], title: match[2].trim() });
  }
  return found;
}

function findingsHtml(findings) {
  if (findings === null || findings.length === 0) {
    return '<p class="note"><strong>The findings report is being written.</strong> It will appear here as <code>docs/FINDINGS.md</code> once it is on <code>main</code>; until then this page claims nothing about what the bench caught.</p>';
  }
  const cards = findings
    .map(
      (f) =>
        `<article class="card"><p class="eyebrow">${esc(f.id)}</p><h3>${inline(f.title)}</h3></article>`,
    )
    .join('\n');
  return `<div class="cards cards-2">\n${cards}\n</div>
<p>The evidence for each finding, and whether it is fixed or still open, is in <a href="REPO/blob/main/docs/FINDINGS.md">docs/FINDINGS.md</a>.</p>`;
}

// ------------------------------------------------------------------ documents

function documentsHtml(adrs, hasPdfWorkflow) {
  const link = (path, label, note) => (existsSync(rel(path)) ? `<li><a href="REPO/blob/main/${path}">${esc(label)}</a>${note ? ` <span class="muted">${esc(note)}</span>` : ''}</li>` : '');
  const items = [
    link('README.md', 'README', 'what the service does and how to run it'),
    link('docs/OVERVIEW.md', 'OVERVIEW', 'the long-form account'),
    link('docs/ARCHITECTURE.md', 'ARCHITECTURE', 'the shape and the seven rules'),
    link('docs/REGISTER.md', 'REGISTER', 'what is not yet proved'),
    link('docs/FINDINGS.md', 'FINDINGS', 'what building it caught'),
    link('docs/DIAGRAMS.md', 'DIAGRAMS', 'the diagram sources'),
    link('scenarios/README.md', 'scenarios/', 'the fault scenarios and scheme rules as data'),
    ...adrs.map((a) => `<li><a href="REPO/blob/main/docs/adr/${esc(a.file)}">${esc(a.title)}</a> <span class="muted">decision record</span></li>`),
    '<li><a href="REPO">The GitHub repository</a></li>',
  ].filter(Boolean);
  const pdf = hasPdfWorkflow
    ? '<p class="note">A PDF edition of the overview is built by the “Build Overview PDF” workflow (<code>.github/workflows/build-overview-pdf.yml</code>).</p>'
    : '';
  return `<ul class="plain">\n${items.join('\n')}\n</ul>\n${pdf}`;
}

// ------------------------------------------------------------------ assemble

const SELF_TEST = [/<script/i, / style\s*=/i, /@import/i, /url\(\s*['"]?https?:/i, /\bsrc\s*=\s*["']https?:/i, /\bsrc\s*=\s*["']\/\//i];

function build() {
  const facts = loadStatus();
  const repo = facts.get('repository').value;
  const register = loadRegister();
  const scenarios = loadScenarios();
  const adrs = loadAdrs();
  const findings = loadFindings();
  const hasPdfWorkflow = existsSync(rel('.github/workflows/build-overview-pdf.yml'));
  const css = read('docs/site/style.css').trimEnd();

  const testsTotal = facts.get('tests.domain').value + facts.get('tests.application').value;
  const asOfDates = [...facts.values()].map((f) => f.asOf).sort();
  const prs = facts.get('mergedPullRequests').value;

  const cite = (key) => {
    const fact = facts.get(key);
    return `<span class="src">${esc(fact.source)} · ${esc(fact.asOf)}</span>`;
  };
  const blocks = {
    register: registerHtml(register),
    'register-closed': registerClosedHtml(register),
    scenarios: scenariosHtml(scenarios),
    adrs: adrsHtml(adrs),
    schemes: schemesHtml(),
    findings: findingsHtml(findings),
    documents: documentsHtml(adrs, hasPdfWorkflow),
    'merged-prs': `<ul class="plain">${prs.map((pr) => `<li><a href="${esc(repo)}/pull/${pr.number}">#${pr.number}</a> ${esc(pr.title)}</li>`).join('')}</ul>`,
    'tests-total': esc(testsTotal),
    'count:scenarios': esc(scenarios.length),
    'count:register-open': esc(register.open.length),
    'count:register-closed': esc(register.closedRows.length),
    'count:adrs': esc(adrs.length),
    repo: esc(repo),
    'facts-as-of': esc(asOfDates[asOfDates.length - 1]),
  };

  const source = read('docs/site/source.html');
  const unknown = new Set();
  const body = source
    .replace(/\{\{\s*(fact|cite|src|asof):([\w.]+)\s*\}\}/g, (_, kind, key) => {
      if (!facts.has(key)) fail(`docs/site/source.html cites unknown fact "${key}"`);
      if (kind === 'cite') return cite(key);
      if (kind === 'src') return esc(facts.get(key).source);
      if (kind === 'asof') return esc(facts.get(key).asOf);
      return esc(facts.get(key).value);
    })
    .replace(/\{\{\s*([\w:-]+)\s*\}\}/g, (_, name) => {
      if (!(name in blocks)) unknown.add(name);
      return blocks[name] ?? '';
    })
    .replace(/href="REPO/g, `href="${esc(repo)}`);
  if (unknown.size > 0) fail(`docs/site/source.html uses unknown placeholders: ${[...unknown].join(', ')}`);

  const sections = [...body.matchAll(/<section class="panel" id="([\w-]+)" data-tab="([^"]+)">/g)];
  if (sections.length === 0) fail('docs/site/source.html has no <section class="panel" id=".." data-tab="..">');
  const tabs = sections.map((m) => `<a href="#${m[1]}">${m[2]}</a>`).join('\n');
  const cleanBody = body.replace(/ data-tab="[^"]+"/g, '');

  const hash = createHash('sha256').update(css).digest('base64');
  const csp = `default-src 'none'; style-src 'sha256-${hash}'; img-src 'self' data:; base-uri 'none'; form-action 'none'`;
  const icon = "data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 32 32'%3E%3Crect width='32' height='32' rx='7' fill='%230f5e63'/%3E%3Cpath d='M8 21h16M8 16h16M8 11h10' stroke='white' stroke-width='3' stroke-linecap='round'/%3E%3C/svg%3E";

  const html = `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta http-equiv="Content-Security-Policy" content="${csp}">
<title>payments-bench: an outbound transfer bench with a simulated bank</title>
<meta name="description" content="A bench for the mechanisms an outbound payment rail needs to move money exactly once, with a simulated partner bank and a register of what is not yet proved.">
<meta name="color-scheme" content="light dark">
<link rel="icon" href="${icon}">
<style>${css}</style>
</head>
<body>
<a class="skip" href="#overview">Skip to content</a>
<header class="topbar">
<div class="topbar-in">
<div class="brand"><span class="brand-w">payments-bench</span><span class="brand-s">An outbound transfer bench with a simulated bank</span></div>
<nav class="tabs" aria-label="Sections">
${tabs}
</nav>
</div>
</header>
<main class="wrap">
${cleanBody.trim()}
</main>
<footer class="wrap foot">
<p>Generated by <code>scripts/site-build.mjs</code> from <code>docs/site/source.html</code>, <code>docs/site/status.json</code> and the repository. Facts as of ${blocks['facts-as-of']}. The partner bank is simulated; nothing here has talked to a real bank.</p>
</footer>
</body>
</html>
`;
  const bad = SELF_TEST.filter((re) => re.test(html));
  if (bad.length > 0) fail(`self-test failed: generated page matches forbidden pattern(s) ${bad.join(' ')}`);
  return html;
}

const html = build();
if (check) {
  const current = existsSync(rel(OUT)) ? read(OUT) : null;
  if (current !== html) {
    console.error(`site-build: ${OUT} is stale (it differs from what the generator would write). Run "npm run site:build" and commit the result.`);
    process.exit(1);
  }
  console.log(`site-build: ${OUT} is up to date.`);
} else {
  writeFileSync(rel(OUT), html);
  console.log(`site-build: wrote ${OUT} (${html.length} bytes).`);
}
