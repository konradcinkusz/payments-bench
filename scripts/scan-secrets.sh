#!/usr/bin/env bash
#
# scan-secrets.sh - local mirror of the CI job in .github/workflows/secret-scan.yml.
#
# The default mode scans full git history with .gitleaks.toml, exactly as CI does, so
# "it passed locally" means something.
#
# Usage:
#   scripts/scan-secrets.sh            full history (what CI runs)
#   scripts/scan-secrets.sh --staged   staged changes only (what the pre-commit hook runs)
#   scripts/scan-secrets.sh --dir      working tree as files, ignoring git history

set -euo pipefail

REPO_ROOT="$(git rev-parse --show-toplevel)"
CONFIG="${REPO_ROOT}/.gitleaks.toml"
IMAGE="ghcr.io/gitleaks/gitleaks:latest"
MODE="${1:-history}"

red()   { printf '\033[0;31m%s\033[0m\n' "$*"; }
green() { printf '\033[0;32m%s\033[0m\n' "$*"; }
dim()   { printf '\033[0;90m%s\033[0m\n' "$*"; }

if [ ! -f "${CONFIG}" ]; then
  red "scan-secrets: .gitleaks.toml missing from the repository root."
  exit 1
fi

# Validate the mode before choosing a runner so every runner scans the same thing.
case "${MODE}" in
  --staged|--dir|history) ;;
  *) red "Unknown mode: ${MODE}"; echo "Usage: scripts/scan-secrets.sh [--staged|--dir]"; exit 2 ;;
esac

# gitleaks v8.19 renamed detect/protect to git/dir and moved the target from --source to a
# positional argument. $1 = CLI generation (new|old), $2 = repo path as the scanner sees it.
SUB=()
set_args() {
  case "${MODE}:$1" in
    --staged:new) SUB=(git --staged "$2") ;;
    --staged:old) SUB=(protect --staged "--source=$2") ;;
    --dir:new)    SUB=(dir "$2") ;;
    --dir:old)    SUB=(detect --no-git "--source=$2") ;;
    history:new)  SUB=(git "$2") ;;
    history:old)  SUB=(detect "--source=$2") ;;
  esac
}

dim "Mode: ${MODE}"
if command -v gitleaks >/dev/null 2>&1; then
  if gitleaks git --help >/dev/null 2>&1; then set_args new "${REPO_ROOT}"; else set_args old "${REPO_ROOT}"; fi
  gitleaks "${SUB[@]}" --config="${CONFIG}" --redact --no-banner --verbose
elif command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
  dim "gitleaks not on PATH - running the container image (the one CI uses)."
  if docker run --rm "${IMAGE}" git --help >/dev/null 2>&1; then set_args new /repo; else set_args old /repo; fi
  docker run --rm -v "${REPO_ROOT}:/repo" -w /repo "${IMAGE}" \
    "${SUB[@]}" --config=/repo/.gitleaks.toml --redact --no-banner --verbose
else
  red "scan-secrets: neither gitleaks nor a running Docker daemon is available."
  echo "Install gitleaks: https://github.com/gitleaks/gitleaks#installing"
  exit 1
fi

green "Secret scan clean."
