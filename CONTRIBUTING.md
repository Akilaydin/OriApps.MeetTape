# Contributing

## Pull requests

Make every change on a separate branch and open a focused PR; never push directly to `main`.

Use the Problem/Solution template with a self-contained title and a concise description (typically 50–100 words). Explain the problem, resulting behavior, validation and limitations. Do not invent impact or metrics.

Contributions are submitted under `GPL-3.0-only`. Contributors retain their own copyrights unless agreed otherwise. Permission to use a contribution in a differently licensed (including proprietary) edition is not implied by this contribution process; it requires appropriate rights or separate permission. No contributor license agreement or copyright assignment is currently required.

## Checks and Codex review

Wait for Codex review and assess findings against the code and current requirements. Fix valid findings in the same PR; explain rejected findings in its discussion.

Evaluate new reviews after updates. Merge only with successful checks and no unaddressed substantive findings. If review fails or is unavailable, report the limitation; silence is not approval.

Promote reviewed `main` to `release` via a pull request with passing CI. Each push to `release` publishes a new GitHub Release; do not push to `release` directly.
