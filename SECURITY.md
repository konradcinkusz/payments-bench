# Security policy

payments-bench is a bench, not a product: an outbound bank-transfer service that shows how a payment
rail moves money exactly once, run against a **simulated** partner bank. Reports about it are still
welcome, because the point of the repository is to show what such a service needs.

## Reporting a vulnerability

Report privately. Use GitHub's private vulnerability reporting on this repository
(**Security** tab, then **Report a vulnerability**). Please do not put details in a public issue,
pull request or discussion.

If the repository shows no **Report a vulnerability** button, open a public issue titled
"Security contact request" with **no details** in it, and a maintainer will arrange a private
channel.

A useful report names the route or file, the request that triggers it, what an attacker gains, and
which token or grant (if any) the request needs. The API distinguishes an ordinary token, the
`payments.operate` grant and the `payments.partner.notify` grant; see section 6 of the
[security review](docs/security/SECURITY-REVIEW.md).

This is a one-maintainer repository with no service-level agreement. The aim is to acknowledge a
report within 7 days and to say what will be done about it within 30. Credit is given if you want
it.

If you find a credential anywhere in the tree or its history, report it privately and treat it as
compromised. The order of operations is rotate first, clean history second (see
[`.github/workflows/secret-scan.yml`](.github/workflows/secret-scan.yml)).

## Supported versions

| Version | Supported |
|---|---|
| `main` | Yes. Fixes land here. |
| Tags (`v*`) | None have been published; nothing has been deployed ([R14](docs/REGISTER.md)). Once tags exist, only the newest is supported. |
| Branches | No. |

## What is simulated

- **The bank.** The partner is [`PaymentsBench.PartnerSimulator`](src/PaymentsBench.PartnerSimulator/Program.cs):
  an invented API shape with faults a test can switch on, not SEPA, Faster Payments or SWIFT, and not
  a sandbox of any real bank. Nothing in this repository talks to a real bank.
- **The money.** No real funds move and no real customer data is held. Account numbers in tests and
  examples are placeholders.
- **Its openness is deliberate.** The simulator has no authentication, including `PUT /faults`, which
  is why a deployment gives it no public address. A report that the simulator's control routes are
  open is already known ([SEC-13](docs/security/SECURITY-REVIEW.md)); a report that one is reachable
  from the internet in a deployed estate is not, and is welcome.

## Scope

In scope: the API (`src/PaymentsBench.Api`), ServiceDefaults, the Dockerfiles, the workflows, the
Fly.io configuration under `flyio/`, and documentation that would lead a reader to a vulnerable
setup.

Out of scope: authservice, the identity service this estate runs from a pinned image (report to its
own repository); Fly.io, GitHub and other platforms; third-party dependencies (report upstream);
volumetric denial of service against a deployed estate; social engineering; and anything that needs
a machine you have already compromised.

## Known issues

[`docs/security/SECURITY-REVIEW.md`](docs/security/SECURITY-REVIEW.md) is a static review with its
findings, an endpoint-by-grant matrix and a readiness ledger. Open findings in it are known;
[`docs/REGISTER.md`](docs/REGISTER.md) lists what is not yet proved. New findings are welcome, as are
corrections to the review.
