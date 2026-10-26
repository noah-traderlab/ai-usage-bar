# Python package release and adoption evidence

## Scope

The `ai-usage-bar-mac` distribution contains only the Python macOS companion, not the Windows application. Version 0.1.0 matches the current Python source. Packaging tests do not validate end-user macOS GUI behavior. The package name is provisional until PyPI accepts the first upload; a pending publisher does not reserve it.

## Verified before publication

Build sdist and wheel using `python -m build`, validate with `python -m twine check --strict dist/*`, and run `python tests/check_python_package.py`. This inspects wheel contents/licenses and installs it offline into an isolated temporary environment outside the source tree. It verifies both console and module help entry points. The normal macOS/Linux regression and Windows checks remain in CI. Never upload local credentials, configuration, team state, reports, or private logs.

## One-time owner setup

The owner must sign in to PyPI, complete account/email/2FA requirements and any terms personally. Do not send API tokens in chat. Configure a pending GitHub Trusted Publisher at https://pypi.org/manage/account/publishing/ using:

- PyPI project: `ai-usage-bar-mac`
- GitHub owner: `noah-traderlab`
- Repository: `ai-usage-bar`
- Workflow filename: `publish-pypi.yml`
- Environment: `pypi`

Configure the GitHub `pypi` environment to allow releases from main and use required reviewers if supported by the account. Binding a trusted publisher grants that exact workflow release authority for this PyPI project: the owner must confirm this access grant. The workflow requests a short-lived identity token only in the publish job, has no API-key secret, and runs manually on main after package and regression checks. It does not run publication on pushes, PRs, or forks.

Once owner setup and release authorization are confirmed, dispatch `Publish Python package`. Check the run and live PyPI project; do not describe the package as published until the registry confirms it. Subsequent releases require a new version; PyPI does not permit replacing existing distribution files. No automatic version bump or publication loop is provided.

## Real adoption evidence

A registry listing makes installation possible; it is not itself adoption. Record actual monthly downloads from the registry's published statistics, the period, source URL, and any limitations. Test/CI/tool traffic can be included in raw counts and must not be presented as distinct users. Do not generate repeated installs, fake dependents, purchases of stars/downloads, or a repository whose only purpose is to appear dependent. Known users may choose to install a published version for real use; no one should be contacted or asked to endorse a verification application without authorization.

GitHub Dependents can record supported public package dependencies if real downstream projects adopt the package. A custom Homebrew tap is not Homebrew core; neither a package submission nor listing guarantees acceptance or any external verification outcome. Debian/Fedora/core inclusion and ecosyste.ms critical status are decisions by those projects, not self-designations.

The CVP form offers “Monthly downloads on a major package registry”, but its acceptance threshold has not been confirmed. Do not infer eligibility merely because a nonzero number is displayed. Submit only real, accurately described adoption figures for Anthropic's assessment.

## Official references (checked 2026-10-27)

- https://packaging.python.org/en/latest/tutorials/packaging-projects/
- https://docs.pypi.org/trusted-publishers/creating-a-project-through-oidc/
- https://docs.pypi.org/trusted-publishers/using-a-publisher/
- https://docs.github.com/en/code-security/how-tos/secure-your-supply-chain/secure-your-dependencies/explore-dependencies
- https://docs.brew.sh/Package-Acceptance-Policy
