# Security maintenance

## Named maintainer

[@noah-traderlab](https://github.com/noah-traderlab) is the project owner and the named contact responsible for coordinating security maintenance of AI Usage Bar. Reports should use the private channel in [SECURITY.md](../SECURITY.md).

The maintainer coordinates report triage, affected-revision assessment, fixes, regression testing, and release/update guidance. This does not promise a response deadline or uninterrupted support. Current support targets the latest source revision; older distributed binaries require separate identification and verification.

## Work and attribution

Investigation, implementation, and tests may be performed with AI assistance under the owner's direction. Records must distinguish the owner's direction and decisions from AI-performed work, and distinguish observed behavior from hypothetical impact. A maintainer designation is not a claim of personal vulnerability discovery, a CVE, third-party acceptance, or eligibility for any external program.

The [2026-10-23 state-write repair](security-fix-2026-10-23.md) records synthetic failure reproductions, a fix, and regression checks. Its limits remain applicable. Dependency remediation is recorded using the upstream advisory, not presented as a newly discovered vulnerability.

## Reproducible evidence

- [Cross-platform CI](https://github.com/noah-traderlab/ai-usage-bar/actions/runs/39898568296)
- [Private reporting policy](../SECURITY.md)
- Source commit, affected/fixed behavior, test cases and limitations in each repair record

## External users and dependents

Distribution, actual use, and downstream dependency are different facts. This document does not assert a verified number of users or downstream dependents. Stars, forks, downloads, or a role designation alone are not proof of reliance. Users may voluntarily describe actual use and the revision/version in an ordinary Issue, without sharing credentials, private logs, screenshots containing personal data, or security-sensitive details. No endorsement or eligibility statement is requested.
