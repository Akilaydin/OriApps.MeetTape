# Contributing

## Pull requests

Make every change on a separate branch and open a focused PR; never push directly to `main`.

Use the Problem/Solution template with a self-contained title and a concise description (typically 50–100 words). Explain the problem, resulting behavior, validation and limitations. Do not invent impact or metrics.

Contributions are submitted under `GPL-3.0-only`. Contributors retain their own copyrights unless agreed otherwise. Permission to use a contribution in a differently licensed (including proprietary) edition is not implied by this contribution process; it requires appropriate rights or separate permission. No contributor license agreement or copyright assignment is currently required.

## Checks and Codex review

The required checks remain non-hardware-tests and package-smoke. Both jobs always run path classification; documentation-only changes skip builds. Managed logic and test changes run software/headless tests. Packaging, dependencies, assets, updater and native startup/integration changes run full package/install/update/uninstall smoke. Unknown paths fail safe to full checks. Every PR targeting release runs full checks before publication.

Documentation-only changes need self-review and successful reduced checks. Substantive logic, recording, processing, storage/deletion, security and updater changes require a completed Codex review. Fix valid findings in the same PR; explain rejected findings in its discussion. Reevaluate review after code updates. A documentation-only follow-up after satisfactory code review does not require a new full review; record the reviewed code SHA and verify that all later changes are documentation-only. Existing substantive findings must still be addressed.

Merge only with successful required checks and no unaddressed substantive findings. If required review fails or is unavailable, report the limitation; silence is not approval. Automatic review configured outside this repository may still run on documentation changes; do not request duplicate reviews.

The scheduler regression runs separately from the other software tests. Its first failure remains a failure, even if two bounded diagnostic repeats pass; inspect the saved TRX before rerunning full CI. Do not quarantine it without evidence separating test instability from a product defect.

Promote reviewed `main` to `release` via a pull request with passing CI. Each push to `release` publishes a new GitHub Release; do not push to `release` directly.

Use **Create a merge commit** for promotions between these long-lived branches. Squash or rebase merging loses the shared ancestry needed for later promotions and can cause conflicts over already released changes.
