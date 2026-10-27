# Public maintenance evidence

This index distinguishes published maintenance work from adoption or independent security validation. It does not claim eligibility for any external program.

## Maintainer and reporting

- The named security maintenance contact is [noah-traderlab](https://github.com/noah-traderlab), whose public GitHub profile displays the name Noah. This is a public profile association, not independent identity verification.
- [Security responsibilities and AI-assisted attribution](security-maintenance.md).
- [Private vulnerability reporting](https://github.com/noah-traderlab/ai-usage-bar/security/advisories/new) is enabled. See [SECURITY.md](../SECURITY.md) for scope and handling. Enabling a reporting channel does not mean a report has been received or accepted.

## Recorded defensive maintenance

- [State-file write correction and reproducible regression record](security-fix-2026-10-23.md), including its limits and AI assistance.
- [Original Windows/macOS/Linux checks](https://github.com/noah-traderlab/ai-usage-bar/actions/runs/39898568296). Tests are evidence of the listed checks, not an independent audit or proof that all vulnerabilities are absent.
- No CVE, paid bounty result, or independently accepted vulnerability disclosure is claimed by this index.

## Published software and traceability

| Distribution | Source and verification | Status |
| --- | --- | --- |
| [Windows 0.1.0 ZIP](https://github.com/noah-traderlab/ai-usage-bar/releases/tag/windows-v0.1.0) | Source revision is included in SOURCE.txt; [build and tests](https://github.com/noah-traderlab/ai-usage-bar/actions/runs/40426692465), 305 unit / 147 team / 30 interoperability / 35 installer checks | Public; not Authenticode signed |
| [macOS Python companion 0.1.0](https://pypi.org/project/ai-usage-bar-mac/0.1.0/) | [Publish workflow](https://github.com/noah-traderlab/ai-usage-bar/actions/runs/40424765591), wheel/sdist provenance on PyPI | Public; Python 3.11+ required |
| Standalone macOS app | [Apple Silicon and Intel app/DMG checks](https://github.com/noah-traderlab/ai-usage-bar/actions/runs/40425926583), bundled join-screen initialization with synthetic service data | Development artifacts; Developer ID signing and notarization outstanding; not a public notarized release |

The Windows release was rebuilt from public source. It does not redistribute the historical archive's unreviewed images or older macOS source. Package installation and GUI initialization checks do not establish successful end-user team operation.

## Adoption evidence

Publication, CI runs, maintainer-authored documentation, and package provenance establish availability and traceability. They do not establish independent users, downstream dependencies, monthly downloads, distribution inclusion, or critical-infrastructure status. Do not substitute release downloads for a package registry's monthly statistics or manufacture dependents/downloads to obtain a metric.

## Intended defensive scope

Work covers source code and local data handling in AI Usage Bar, controlled reproductions using synthetic data, correction validation, and regression tests. This project does not authorize testing third-party systems or real users' private data. Team-sharing features require consent; see the distribution documentation before joining a team.
