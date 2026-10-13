# AGENTS.md

MeetTape is an implemented `net10.0-windows` desktop app on Avalonia 12.

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

## Automatic Code Review Rules

Report only high-confidence P0/P1 issues that:
- Cause significant functional regressions.
- Introduce security vulnerabilities or data loss.
- Can cause serious production failures.

Do not report:
- Style or formatting issues.
- Minor improvements or refactoring suggestions.
- Speculative edge cases.
- Low-impact bugs.
- Missing tests unless they expose a serious risk.

Every finding must describe a concrete failure scenario.
If no qualifying issues exist, report no findings.

## Fast task execution

- Run one focused local validation pass. Broaden it only for a new failure, changed code or unresolved risk.
- Poll CI and review together, no more often than once per minute while unchanged. Act immediately on actionable feedback; avoid separate repeated status queries.
- After the same failure repeats, inspect its logs and diagnose locally before another full CI run. Scheduler diagnosis is limited to two repeats; never turn a confirmed application defect into an optional test.
- Use the risk-based CI and review rules in CONTRIBUTING.md. Do not introduce a watcher or self-hosted runner for this workflow.
