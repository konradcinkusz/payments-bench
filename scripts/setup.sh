#!/usr/bin/env bash
#
# setup.sh - one-command onboarding (repo-baseline section 3).
#
# Steps: 1 prerequisites (installs dotnet-sdk-10.0 from the Ubuntu archive if missing),
# 2 secret scanner, 3 git hook, 4 restore, 5 build with -warnaserror, 6 tests, 7 optional
# integrations. Mirrors the CI job `build-and-test` in .github/workflows/ci.yml (restore,
# build, test) plus the local-only hook install. Idempotent: safe to re-run.
#
# Usage:
#   scripts/setup.sh            run all steps; a missing secret scanner is a warning
#   scripts/setup.sh --check    strict: a missing secret scanner is a failure

set -euo pipefail

REPO_ROOT="$(git rev-parse --show-toplevel)"
cd "${REPO_ROOT}"

STRICT=0
[ "${1:-}" = "--check" ] && STRICT=1

red()   { printf '\033[0;31m%s\033[0m\n' "$*"; }
green() { printf '\033[0;32m%s\033[0m\n' "$*"; }
amber() { printf '\033[0;33m%s\033[0m\n' "$*"; }
dim()   { printf '\033[0;90m%s\033[0m\n' "$*"; }
step()  { printf '\n\033[1m%s\033[0m\n' "$*"; }

DEGRADED=0

# -- 1. Prerequisites ---------------------------------------------------------
step "1/7 Prerequisites"

if ! command -v git >/dev/null 2>&1; then
  red "  git is not installed. Install: https://git-scm.com/downloads"
  exit 1
fi
green "  git $(git --version | cut -d' ' -f3)"

if ! command -v dotnet >/dev/null 2>&1; then
  amber "  dotnet is not installed; this repository needs the .NET 10 SDK."
  IS_UBUNTU=0
  if [ -r /etc/os-release ] && grep -qE '^ID=ubuntu$' /etc/os-release; then IS_UBUNTU=1; fi
  if [ "${IS_UBUNTU}" -eq 1 ] && command -v sudo >/dev/null 2>&1; then
    echo "  Installing dotnet-sdk-10.0 from the Ubuntu archive (sudo)..."
    sudo apt-get update -qq
    sudo apt-get install -y -qq --no-install-recommends dotnet-sdk-10.0
  else
    red "  Automatic install is supported on Ubuntu with sudo only."
    echo "  Install the .NET 10 SDK: https://dotnet.microsoft.com/download/dotnet/10.0"
    exit 1
  fi
fi

DOTNET_VERSION="$(dotnet --version)"
if [ "${DOTNET_VERSION%%.*}" -lt 10 ]; then
  red "  dotnet ${DOTNET_VERSION} found; this repository needs 10.0 or newer (see global.json)."
  echo "  Install: https://dotnet.microsoft.com/download/dotnet/10.0"
  exit 1
fi
green "  dotnet ${DOTNET_VERSION}"

# -- 2. Secret scanner --------------------------------------------------------
step "2/7 Secret scanner"

if command -v gitleaks >/dev/null 2>&1; then
  green "  gitleaks on PATH - the hook will use it directly."
elif command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
  green "  gitleaks not on PATH, Docker is running - the hook uses the container image CI uses."
else
  DEGRADED=1
  amber "  No secret scanner available."
  echo  "  The hook REFUSES TO COMMIT without one - by design (P5), not a bug to work around."
  echo  "  Install gitleaks (https://github.com/gitleaks/gitleaks#installing) or Docker."
fi

# -- 3. Git hook --------------------------------------------------------------
step "3/7 Git hook"
"${REPO_ROOT}/scripts/install-hooks.sh"

# -- 4. Restore ---------------------------------------------------------------
step "4/7 Restore"
dotnet restore PaymentsBench.sln

# -- 5. Build -----------------------------------------------------------------
step "5/7 Build (-warnaserror)"
dotnet build PaymentsBench.sln --no-restore -warnaserror

# -- 6. Test ------------------------------------------------------------------
step "6/7 Test"
dotnet test PaymentsBench.sln --no-build

# -- 7. Optional integrations -------------------------------------------------
step "7/7 Optional integrations (a fresh clone runs without any of them)"

if command -v docker >/dev/null 2>&1; then
  green "  docker found"
else
  dim   "  docker missing (optional - needed for container images and the local secret-scan fallback)"
fi
if command -v k6 >/dev/null 2>&1; then
  green "  k6 found"
else
  dim   "  k6 missing (optional - needed for load tests): https://grafana.com/docs/k6/latest/set-up/install-k6/"
fi
if command -v flyctl >/dev/null 2>&1; then
  green "  flyctl found"
else
  dim   "  flyctl missing (optional - needed for deploying to Fly.io): https://fly.io/docs/flyctl/install/"
fi

# -- Summary ------------------------------------------------------------------
step "Ready"
echo "  dotnet run --project src/PaymentsBench.Api    run the API (in memory without a connection string)"
echo "  scripts/scan-secrets.sh                       mirror the CI secret scan"

if [ "${DEGRADED}" -eq 1 ]; then
  echo
  if [ "${STRICT}" -eq 1 ]; then
    red "setup --check: a secret scanner is required and none was found."
    exit 1
  fi
  amber "Setup finished, but committing will be refused until a scanner is installed."
fi
