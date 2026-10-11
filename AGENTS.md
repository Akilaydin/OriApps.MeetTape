# AGENTS.md

MeetTape is an implemented `net10.0-windows` CLI.

## Context

Read the relevant code and task before changing behavior. Use [README.md](README.md) for orientation, [PRODUCT.md](PRODUCT.md) for the user contract and [ARCHITECTURE.md](ARCHITECTURE.md) for the implemented system. Current task requirements take precedence over stale documentation; flag material conflicts.

## Changes

- Make the smallest focused change; reuse existing patterns, .NET and current dependencies.
- Preserve public APIs, configuration contracts, stored session compatibility and user data unless the task explicitly changes them.
- Avoid unrelated cleanup, speculative features, new layers and unnecessary files/dependencies.
- Keep each document within its purpose; do not copy implementation reports or historical decisions into project documentation.

## Verification

Run relevant software tests and `git diff --check`; inspect the diff for secrets and unrelated changes. Keep test artifacts isolated inside the repository. Hardware and long-running tests require explicit opt-in. Report unavailable checks and do not claim validation that was not performed.

## Delivery

Follow [CONTRIBUTING.md](CONTRIBUTING.md) for branches, PRs, checks and Codex review. Never push directly to `main`; push to `release` only when publication is explicitly requested.
