# Guarded Test delivery

Task 15 supplies CI and release guards. Nothing has been published. A real
local Test candidate currently exists as a ZIP, setup executable and package
manifest under task14 artifact directory `task14-notices-1ea8d85`. Its source is
`1ea8d8576c7fd16be53d39ac7314c89b90e00823`. The build receipt reports exit 0
with app and setup execution false. The production receipt reports package
integrity passed, one positive case and 23 structured negative cases passed,
with empty stderr captured and cleanup exit 0. The current candidate does not
establish runtime, offline installation or classroom results. Those remain
explicitly untested. Package integrity is independent from those deferred
results, and a Test prerelease may proceed after package, source, asset and
explicit Test-authorization checks pass.

The execution amendment dated 2026-10-25 forbids desktop execution in the current
environment. GUI, diagnostic runtime, installation, Windows 10/11 and classroom
results remain pending. Desktop checks are **pending in the actual environment
after release**; this checkpoint may close configuration work without running
them. That amendment does not turn pending runtime evidence into a runtime
PASS. Package integrity is passed for the measured candidate; Test publication
may proceed with explicit authorization while runtime/offline/classroom results
remain pending. Stable and actual runtime claims remain separately gated.

## Read-only CI

`verify.yml` handles PRs, manual dispatch and reusable workflow calls with
`contents: read` and `actions: read`. Checkout does not persist credentials. SDK
10.0.402 is installed and checked exactly. Restore uses committed locks; Release
build treats warnings as errors and compiles the desktop drivers. Only Core and
pure harness contract tests run, with nonempty TRX results and no skipped tests
accepted. Infrastructure diagnostic tests and desktop fixtures are not executed.
No headless result is described as desktop PASS.

Package validation is a separate job. Ordinary pull requests with no
`package_run_id` now complete source/non-GUI verification and explicitly skip
package validation; they do not fail because a staging run is absent. A
positive `package_run_id` selects a same-repository Actions run containing
`test-package-<full source commit>` and enables the package job. The Test
prerelease dispatch supplies that staged run explicitly. Static validation
checks the task 14 allowlist, runtime inventory, checksums and embedded identity,
then requires the manifest commit to equal the checked source. It uploads only
validated distribution assets for the current run, excluding payload
directories, restore logs and feeds. This is an explicit division: source PRs
are package-independent, while prerelease publication requires a separately
staged package bound to the exact tag commit.

The task 14 build script has a machine-specific SDK path and offline cache
requirements. Hosted CI does not run that script or change product locks.
The runtime-notice correction is production-validated for package integrity.
Runtime, offline installation and classroom evidence remain separate and
pending. They are not Test-prerelease prerequisites in this follow-up; they
remain required for any claim that those surfaces passed and for Stable.

## Publication gate

`prerelease.yml` accepts only manual dispatch or `v0.1.0-test.*` tag events. Exact
numeric Test version syntax is checked at runtime. Dispatch must select an
existing Test tag and set `authorize_publication=true`; tag pushes alone fail
before verification and cannot authorize a write. Branch dispatches fail.
`Release` compilation never selects Stable. Stable without explicit user channel
direction fails; even with that direction this workflow rejects Stable and
requires a separately reviewed delivery path.

The source/package checks and local readiness job must all succeed before the
publish job obtains `contents: write`. That job also names the `test-prerelease`
environment; configure its protection rules when publication is authorized.
No environment, runner registration or credentials have been provisioned here.
Only the publishing step receives the built-in token as an environment variable;
no credential file or local GH_TOKEN file is read or created.

The local guard is
`scripts/release/Test-ReleaseReadiness.ps1`. It takes an asset
directory, expected full source commit and optional receipt path. The committed
`readiness.json` uses schema 2 with three independent sections:
`packageIntegrity`, `runtimeReadiness`, and `publicationReadiness`. A valid
package requires an external `packageIntegrity.status=passed` receipt bound to
the exact commit, version, Test channel and measured manifest, ZIP and setup
hashes. It does not require `releaseReadiness=true`, runtime test results or a
rewritten task 14 manifest. The guard rejects missing setup, wrong channel or
version, changed SHA256 assets, extra release files and package identity
mismatches.

Before a future authorized release, collect actual results and review a separate
readiness artifact. The same staging run must upload `test-readiness-<full source
commit>`, containing `readiness.json` and its supporting JSON receipts. Both
release jobs download it under `.omo/evidence/phase-one/task-15/staged` and pass
that receipt explicitly to the guard. Evidence paths refer to that subtree.
This lets evidence bind the already built source commit without changing its
Git tree. For a Test prerelease, the committed receipt may truthfully keep
runtime readiness pending while package integrity and explicit authorization
are checked:

- Set its commit/version/channel to the exact tested Test package. The package
  receipt may pass while the task 14 manifest retains
  `packageIntegrity.status=requires-validation`, `receipt=external`, pending
  runtime results and `releaseReadiness=false`.
- Keep `desktop` and `offlineInstall` evidence `null` or `pending` for this
  Test prerelease. Do not fabricate runtime receipts or mark those surfaces
  passed. A later actual-environment run may add bindings such as
  `{ "path": ".omo/evidence/.../receipt.json", "sha256": "<measured SHA256>" }`.
- The `authorization` binding must contain `authorized=true` for the explicitly
  requested Test prerelease and bind the exact source, version, Test channel,
  manifestSha256, exeSha256, zipSha256 and installerSha256. A dispatch checkbox
  supplements that record.
- Set `runtimeReadiness.status=passed` only after actual desktop and offline
  receipts pass. For this Test prerelease it remains `pending`. Set
  `publicationReadiness.status=passed` only after the explicit Test
  authorization record passes; Stable remains separately rejected.
  Do not generate a purported user instruction or mark a missing run passed.

The assets are the versioned win-x64 ZIP and matching `-setup.exe`,
`TEST-CHECKLIST.md`, `RELEASE-NOTES.md`, `THIRD-PARTY-NOTICES.md`,
`feedback-guide.md`, `release-manifest.json`, and `SHA256SUMS`. The guard checks
the exact set and rehashes it again immediately before the public write. The
package-integrity guard runs in the readiness job. The publication job calls
the same guard with `-RequirePublication`, which requires package integrity and
explicit Test authorization while preserving pending runtime evidence for this
Test channel. A later Stable or runtime claim must use a separate guard mode
that requires true runtime receipts. The
existing tag must match the manifest version and checked source. Releases use
`--verify-tag --prerelease`, never automatic tag creation or Stable promotion.

The provisional external integrity binding is recorded in
`.omo/evidence/phase-one/task-15/artifact-binding.json` and consumed through
`.omo/evidence/phase-one/task-15/readiness.json`. It points to task 14's
immutable directory
`E:/corgi-blackboard-worktrees/phase-one-task-14-package/artifacts/release/task14-notices-1ea8d85`
for source `1ea8d8576c7fd16be53d39ac7314c89b90e00823`. The ZIP is
`63322586` bytes with SHA256
`d9f7fe405179f5a60864fd0e6aa34d736f051646ebb7eecd048a5eaf89ce722b`, the setup
is `46625831` bytes with SHA256
`8bdc8b372346d1f44cfbdf09b94e072add9b81eb209af7f363ebeda503882a81`, and the
manifest SHA256 is
`0836f135af6808129779ff805493aa53258daa0789e24037107a2b163a870fe3`.
These are measured candidate values, not a package-integrity pass. The task 14
manifest remains immutable and accurately reports pending runtime and
publication readiness. The old `03418023...` hashes and positive/negative guard
proofs are retained in the binding receipt as historical evidence.

## Historical checkpoint

The earlier packaging attempts recorded NU1004 (missing RID lock target) and
NU1101 (missing offline .NET 10.0.13 runtime packs). Before the local candidate
was produced, the historical checkpoint also stated that no ZIP, setup
executable, manifest or measured final hash existed. Those statements describe
that earlier checkpoint only. The old `03418023...` binding and its measured
hashes remain preserved for audit while the runtime-notice rebuild and
production validation produce the replacement receipt.

Release notes must identify the interaction prototype, untested classroom
devices/OS combinations, known limitations, and local log/export feedback steps.
After a separately authorized publication, verify unauthenticated asset
downloads and hashes and write a real `release-receipt.json` recording status,
safe response headers and measured hashes. Do not record authorization headers.
This task contains no publication or anonymous download receipt.

## Current CI staging contract

The `Stage Test package` workflow is the producer for prerelease assets. It
checks out the exact source commit, installs SDK `10.0.402`, acquires the
pinned Inno Setup compiler and runtime packs, then runs
`scripts/release/Stage-TestPackage.ps1`. The staging artifact is named
`test-package-<full-commit>` and includes the application ZIP, setup executable,
independent `-tests.zip`, release documents, manifest and `SHA256SUMS`.
`test-readiness-<full-commit>` is uploaded separately so the package allowlist
cannot be confused with its receipt.

Ordinary source verification runs without a staging run. A Test prerelease
dispatch must supply the staging run ID, select an existing numeric Test tag,
and set the explicit Test authorization checkbox. The prerelease workflow binds
that authorization to the downloaded manifest and all three package hashes.
Desktop, offline installation and classroom evidence remain pending and do not
block a Test prerelease; Stable is rejected by this workflow.

## Checkpoint evidence

Only static YAML, PowerShell, manifest rejection and workflow guard checks are
authorized for this delivery. No workflow, build, product test, package publish,
installer, app, VM, desktop, UiTests or Verify invocation is part of task 15's
local validation. Its evidence records those limits separately from runtime
readiness. Task 15 can be `[x]` for guarded configuration only; acceptance that
requires actual release/download/desktop/offline evidence remains `[~]`.

Official references:

- [Workflow syntax, permissions, inputs and job dependencies](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax)
- [Reusable workflows](https://docs.github.com/en/actions/how-tos/reuse-automations/reuse-workflows)
- [Workflow artifact storage and cross-run download](https://docs.github.com/en/actions/how-tos/writing-workflows/choosing-what-your-workflow-does/storing-and-sharing-data-from-a-workflow)
