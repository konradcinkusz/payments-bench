#!/usr/bin/env bash
#
# install-hooks.sh - install the repository's git hooks (currently: pre-commit secret scan).
#
# Mirrors no CI job; it installs the local half of the control whose CI half is
# .github/workflows/secret-scan.yml. Idempotent: re-running replaces the hook with the
# committed version and backs up a different pre-existing one once.
#
# Usage: scripts/install-hooks.sh

set -euo pipefail

REPO_ROOT="$(git rev-parse --show-toplevel)"
SRC="${REPO_ROOT}/scripts/hooks/pre-commit"
DST="$(git rev-parse --git-path hooks)/pre-commit"

mkdir -p "$(dirname "${DST}")"
if [ -e "${DST}" ] && ! cmp -s "${SRC}" "${DST}"; then
  cp "${DST}" "${DST}.backup"
  echo "Existing hook backed up to ${DST}.backup"
fi
cp "${SRC}" "${DST}"
chmod +x "${DST}"
echo "pre-commit installed -> ${DST}"
