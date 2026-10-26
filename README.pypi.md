# AI Usage Bar — macOS companion

An early-stage Python companion to the Windows AI Usage Bar application. It joins an existing team and reports Claude Code/Codex usage to a shared folder. The administrator dashboard is part of the separate Windows application, not this package.

Requires Python 3.11 or newer. The optional GUI needs a Python installation with Tk support. macOS GUI behavior on end-user machines is not yet verified. Linux and Windows are used for automated checks; their native desktop integration is not supported by this companion.

## Install and inspect

After this package is published on PyPI:

```sh
python3 -m venv .venv
.venv/bin/python -m pip install ai-usage-bar-mac
.venv/bin/aiusagebar --help
```

Until publication, build/install the wheel from the source repository instead. This document does not claim that a registry release already exists.

## Privacy and use

Joining a team is optional and requires explicit consent. Team reports can include machine/user identifiers, email, token usage, session titles, working directories, activity times, and prompt text from recent Claude Code/Codex sessions. Read the repository's macOS guide and the join consent before using `join --yes`; never run that option without understanding and agreeing to the sharing scope. Reports are encrypted for the team administrator. This does not remove the need to trust the administrator and shared-folder permissions.

Use `aiusagebar status` to inspect participation and `aiusagebar leave` to stop team participation. The optional friends feature shares a more limited status and is off by default. See the source repository for full usage, limitations, and security reporting instructions. Do not include private logs, keys, or personal information in public issues.

## Maintenance and license

Maintained by [noah-traderlab](https://github.com/noah-traderlab). MIT licensed; see the bundled license and third-party notices. Investigation and implementation can use AI assistance with recorded checks. This is an independent project and is not affiliated with Anthropic or OpenAI. No CVE, independent security audit, adoption threshold, or external program eligibility is claimed.
