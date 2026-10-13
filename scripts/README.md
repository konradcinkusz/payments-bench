# Scripts

All scripts are bash with `set -euo pipefail`, are self-sufficient (any one runs alone), and
state in their header which CI job they mirror. Run them from anywhere inside the checkout.

| Script | What it does | Mirrors |
|---|---|---|
| `setup.sh` | One-command onboarding: prerequisites (installs `dotnet-sdk-10.0` from the Ubuntu archive if `dotnet` is missing), secret scanner check, hook install, restore, build with `-warnaserror`, tests, optional integrations. Idempotent. `--check` makes a missing secret scanner a failure. | `ci.yml` (`build-and-test`) |
| `install-hooks.sh` | Copies `hooks/pre-commit` into `.git/hooks/` (backs up a different existing hook once). | local half of `secret-scan.yml` |
| `hooks/pre-commit` | Runs gitleaks on staged changes with `.gitleaks.toml`. Refuses to commit if no scanner is available. | `secret-scan.yml` (staged instead of full history) |
| `scan-secrets.sh` | Runs gitleaks with `.gitleaks.toml`. Default: full history. `--staged`: what the hook scans. `--dir`: working tree as files. Falls back to the gitleaks container image if the binary is missing. | `secret-scan.yml` |

Nothing here talks to a real bank; the partner is the simulator in
`src/PaymentsBench.PartnerSimulator`.

## Environment variables

Documented once, with placeholders only, in [`../secrets.env.example`](../secrets.env.example).
Copy it to `.env` (gitignored) for local use. `.env*` is ignored except the example.

| Tier | Variable | Used by | Source of the value |
|---|---|---|---|
| A - always required | none | | A fresh clone builds, tests and runs in memory. |
| B - required in CI / deploys | `FLY_API_TOKEN` | deploy workflow (later slice) | GitHub secret; `flyctl tokens create deploy`. Deploy steps skip green without it. |
| C - optional | `Auth__Authority` | API (identity slice) | OIDC authority; unset in Development means anonymous with a warning. |
| C - optional | `K6_BASE_URL` | k6 scripts (later slice) | URL of a running API. |

Tier B and C rows belong to slices that have not all landed; the example file lists them so
the contract is written before the code reads them.

## Tooling versions

| Tool | Where pinned | Version |
|---|---|---|
| .NET SDK | `global.json` (roll forward to latest feature band) | 10.0.100+ |
| actionlint | `.github/workflows/lint.yml` (`ACTIONLINT_VERSION`); runs shellcheck when it is installed, so install shellcheck locally to match CI. Per-file ignores live in `.github/actionlint.yaml`. | 1.7.12 |
| gitleaks | CI and fallback use `ghcr.io/gitleaks/gitleaks:latest` on purpose (a pinned scanner stops detecting what upstream adds later; see the comment in `secret-scan.yml`) | latest |

## Troubleshooting

Keyed on the text you will see.

| Error text | Cause | Fix |
|---|---|---|
| `pre-commit: no secret scanner available - refusing to commit.` | Neither `gitleaks` nor a running Docker daemon. The refusal is deliberate (P5). | Install gitleaks or start Docker. `git commit --no-verify` bypasses locally; CI still blocks the push. |
| `pre-commit: secrets detected in staged changes - commit refused.` | A credential-shaped string is staged. | Rotate it first, then remove it and read it from the environment. A false positive gets a narrow, commented allowlist entry in `.gitleaks.toml`. |
| `pre-commit: the secret scanner failed to run (exit 1)` | Tooling failure, not a finding: bad flags, malformed `.gitleaks.toml`, or Docker stopped. | `scripts/scan-secrets.sh --staged` for full output. |
| `Automatic install is supported on Ubuntu with sudo only.` | `dotnet` missing on a non-Ubuntu machine or without sudo. | Install the .NET 10 SDK from <https://dotnet.microsoft.com/download/dotnet/10.0>, then re-run `setup.sh`. |
| `dotnet 9.x found; this repository needs 10.0 or newer` | An older SDK shadows the new one. | Install .NET 10; check `dotnet --list-sdks` and `global.json`. |
| `A compatible .NET SDK was not found` | `global.json` requires 10.0.100+. | Same as above. |
| `error CS...` / `warning ... treated as an error` during step 5 | `-warnaserror` is on; analyzers are part of the build. | Fix the warning; do not suppress it without a recorded reason. |
| `bad interpreter: /usr/bin/env bash^M` | CRLF line endings in a script. | `.gitattributes` forces LF; re-checkout, or `git add --renormalize .`. |
| `Cannot connect to the Docker daemon` | Docker is installed but not running. | Start Docker, or install gitleaks natively. |
| `Secret scan failed` in the PR step summary | CI found a credential-shaped string in history. | Rotate first; see the step summary, then `scripts/scan-secrets.sh`. |
