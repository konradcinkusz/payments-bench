#!/usr/bin/env bash
# Starts the partner simulator and the API (in memory), runs k6/payments.js, stops them.
# Nothing here talks to a real bank: the partner is src/PaymentsBench.PartnerSimulator.
#
# The API runs as Development with no Auth__Authority, because Development allows anonymous
# calls. An authenticated run against authservice is future work (docs/REGISTER.md).
#
# Environment: VUS, PHASE_SECONDS, DURATION_SECONDS (see k6/README.md), SUMMARY_FILE,
# API_PORT, SIMULATOR_PORT, K6 (path to an existing k6 binary).
set -euo pipefail

K6_VERSION="v1.3.0"
K6_SHA256="84d26fc1f7bc03e02f2e016b3b1b20c032e05dfe461fca82de4e3a6ebe72ddbd" # k6-v1.3.0-linux-amd64.tar.gz

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
api_port="${API_PORT:-5100}"
sim_port="${SIMULATOR_PORT:-5101}"
work="$(mktemp -d)"
export SUMMARY_FILE="${SUMMARY_FILE:-$root/k6-summary.json}"
pids=()

cleanup() {
  for pid in "${pids[@]:-}"; do
    [ -n "$pid" ] && kill "$pid" 2>/dev/null || true
  done
  wait 2>/dev/null || true
  rm -rf "$work"
}
trap cleanup EXIT

k6_bin="${K6:-$(command -v k6 || true)}"
if [ -z "$k6_bin" ]; then
  archive="k6-${K6_VERSION}-linux-amd64"
  echo "k6 not found; downloading ${K6_VERSION} from its GitHub release"
  curl -fsSL -o "$work/k6.tgz" "https://github.com/grafana/k6/releases/download/${K6_VERSION}/${archive}.tar.gz"
  echo "${K6_SHA256}  $work/k6.tgz" | sha256sum --check --quiet
  mkdir "$work/k6"
  tar -xzf "$work/k6.tgz" -C "$work/k6"
  k6_bin="$work/k6/${archive}/k6"
fi

echo "Building (Release)"
dotnet build "$root/PaymentsBench.sln" --configuration Release --nologo -v quiet

wait_for() {
  for _ in $(seq 1 60); do
    curl -fs "$1" >/dev/null 2>&1 && return 0
    sleep 1
  done
  echo "timed out waiting for $1" >&2
  return 1
}

echo "Starting the simulated partner on :${sim_port}"
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="http://localhost:${sim_port}" \
  dotnet "$root/src/PaymentsBench.PartnerSimulator/bin/Release/net10.0/PaymentsBench.PartnerSimulator.dll" >"$work/simulator.log" 2>&1 &
pids+=("$!")

echo "Starting the API (in memory, Development, anonymous) on :${api_port}"
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="http://localhost:${api_port}" \
  Partner__BaseAddress="http://localhost:${sim_port}" \
  dotnet "$root/src/PaymentsBench.Api/bin/Release/net10.0/PaymentsBench.Api.dll" >"$work/api.log" 2>&1 &
pids+=("$!")

wait_for "http://localhost:${sim_port}/faults"
wait_for "http://localhost:${api_port}/health"

status=0
API_URL="http://localhost:${api_port}" SIMULATOR_URL="http://localhost:${sim_port}" \
  "$k6_bin" run "$root/k6/payments.js" || status=$?

echo "--- API log: lines at warning level or above"
grep -E '^(warn|fail|crit)' "$work/api.log" | sort | uniq -c | sort -rn | head -20 || true
exit "$status"
