#!/usr/bin/env node
// check-links.mjs - verify every relative Markdown link in tracked *.md files:
// the target file must exist and, for .md targets with a #fragment, the fragment
// must match a GitHub-style heading slug. http(s) and mailto links are ignored.
// Node standard library only.
import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync, statSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';

const repoRoot = execFileSync('git', ['rev-parse', '--show-toplevel'], { encoding: 'utf8' }).trim();
const files = execFileSync('git', ['ls-files', '-z', '--', '*.md'], { cwd: repoRoot, encoding: 'utf8' })
  .split('\0')
  .filter((f) => f !== '' && existsSync(join(repoRoot, f)));

const stripFences = (text) => text.replace(/^(```|~~~)[^\n]*\n[\s\S]*?^\1[^\n]*$/gm, '');
const stripInlineCode = (text) => text.replace(/`[^`\n]*`/g, '');

const slug = (heading) =>
  heading
    .replace(/<[^>]*>/g, '')
    .replace(/\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/[`*_~]/g, '')
    .trim()
    .toLowerCase()
    .replace(/[^\p{L}\p{N}\s-]/gu, '')
    .replace(/\s/g, '-');

const anchorCache = new Map();
function anchorsOf(absolute) {
  if (!anchorCache.has(absolute)) {
    const seen = new Map();
    const anchors = new Set();
    for (const m of stripFences(readFileSync(absolute, 'utf8')).matchAll(/^ {0,3}#{1,6}[ \t]+(.+?)[ \t]*#*[ \t]*$/gm)) {
      const base = slug(m[1]);
      const n = seen.get(base) ?? 0;
      seen.set(base, n + 1);
      anchors.add(n === 0 ? base : `${base}-${n}`);
    }
    anchorCache.set(absolute, anchors);
  }
  return anchorCache.get(absolute);
}

const INLINE = /!?\[[^\]]*\]\(\s*<?([^)\s>]+)>?(?:\s+["'][^)]*["'])?\s*\)/g;
const REFDEF = /^ {0,3}\[[^\]]+\]:\s*<?(\S+?)>?(?:\s+["'(].*)?$/gm;
const isExternal = (t) => /^[a-z][a-z0-9+.-]*:/i.test(t) || t.startsWith('//');

const failures = [];
let checked = 0;

for (const file of files) {
  const absolute = join(repoRoot, file);
  const source = stripInlineCode(stripFences(readFileSync(absolute, 'utf8')));
  const targets = [...source.matchAll(INLINE), ...source.matchAll(REFDEF)].map((m) => m[1]);

  for (const raw of targets) {
    if (isExternal(raw)) continue;
    const [pathRaw, fragment] = raw.split('#');
    const pathPart = decodeURIComponent(pathRaw);
    const resolved = pathPart === '' ? absolute
      : pathPart.startsWith('/') ? resolve(repoRoot, `.${pathPart}`)
      : resolve(dirname(absolute), pathPart);
    checked += 1;

    if (!existsSync(resolved)) {
      failures.push({ file, raw, reason: 'target does not exist' });
      continue;
    }
    if (statSync(resolved).isDirectory()) {
      if (!['README.md', 'index.md'].some((n) => existsSync(join(resolved, n)))) {
        failures.push({ file, raw, reason: 'directory has no README.md' });
      }
      continue;
    }
    if (fragment && resolved.endsWith('.md') && !anchorsOf(resolved).has(decodeURIComponent(fragment).toLowerCase())) {
      failures.push({ file, raw, reason: 'no heading matches the anchor' });
    }
  }
}

if (failures.length > 0) {
  console.error(`check-links: ${failures.length} broken link(s) in ${files.length} file(s).`);
  for (const { file, raw, reason } of failures) console.error(`  ${file}: ${raw} (${reason})`);
  process.exit(1);
}
console.log(`check-links: ${checked} relative link(s) across ${files.length} Markdown file(s) - all resolve.`);
