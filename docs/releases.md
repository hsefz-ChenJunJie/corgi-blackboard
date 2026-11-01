# Guarded Test delivery

The release path builds and verifies one exact source commit, then publishes its
complete asset set as a GitHub Test prerelease. Publication requires an explicitly
authorized annotated Test tag. Source checks, package integrity and publication
authorization are separate from GUI and runtime readiness.

Desktop/UIA/touch/DPI, actual offline installation, device/OS combinations and
classroom execution remain pending until tested in a real environment. CI builds
the desktop drivers but does not execute the application, installer or GUI tests.
Pending runtime results do not block this Test prerelease and must not be reported
as passed. This workflow does not publish Stable.

## Source verification

[verify.yml](../.github/workflows/verify.yml) runs on pull requests, pushes to
`main`, manual dispatch and reusable workflow calls. Its `source` job installs
and checks SDK `10.0.402`, restores committed locks, and builds the solution and
pure harness in Release with warnings treated as errors. It runs:

- `CorgiBlackboard.Core.Tests`.
- `CorgiBlackboard.UiTests/PureHarnessTests`.
- The non-desktop Infrastructure suites `DiagnosticSessionTests`,
  `FeedbackBoundaryTests`, `FeedbackConcurrencyTests`, `DiagnosticLifecycleTests`
  and `WorkspaceDiagnosticsTests`.

Each TRX result must contain tests, with every reported test passed. Empty,
failed or skipped sets fail verification. The filtered Infrastructure tests
cover non-GUI file, ZIP, diagnostics and lifecycle behavior; they do not establish
native desktop or DPI results.

Ordinary PR and `main` verification has no staging dependency. The optional
numeric `package_run_id` input defaults to `0`, which skips the `package` job.
A positive value selects the same-repository run containing
`test-package-<full-source-SHA>`. After `source` succeeds, `package` downloads that
artifact, runs `scripts/release/Test-Package.ps1 -StaticOnly`, checks that the
manifest commit equals the checked source, and uploads
`validated-test-package-<full-source-SHA>`.

## Authorized tag and source identity

[prerelease.yml](../.github/workflows/prerelease.yml) accepts pushes matching
`v0.1.0-test.*`. Its `request` job further requires the exact numeric form
`v0.1.0-test.N`, where `N` is a positive integer without leading zeros. There is
no publication dispatch or authorization checkbox.

Create the tag only after all intended source and workflow changes are committed
and integrated. Its target, the checked-out source, the annotation's source SHA,
and the package manifests must agree. For `v0.1.0-test.1`, the annotation must be
exactly the following six lines, replacing `<full-source-SHA>` with that final
lowercase 40-character commit:

```text
Authorize CorgiBlackboard Test prerelease
repository=hsefz-ChenJunJie/corgi-blackboard
tag=v0.1.0-test.1
source=<full-source-SHA>
channel=Test
assets=validated-same-run
```

For another supported Test version, change the tag name in both the ref and the
annotation. `scripts/release/Test-TagAuthorization.ps1` requires an annotated tag
and this exact message; lightweight tags, missing markers, wrong repository,
source, version or channel are rejected. Extra message content also fails.
The annotation authorizes the complete validated asset set produced from this
source in the tag-triggered run. The readiness jobs subsequently bind the actual
asset hashes to that authorization.

An authorized SSH Git push of this annotated tag triggers the workflow without
an Actions dispatch API call. Pushing `main` only starts source verification.
The `request` job records the tag object ID; `publish` fetches and checks it again
so a changed authorization tag fails before publication.

## Same-run producer and verification

The prerelease jobs run in this dependency order:

```text
request -> stage -> checks -> readiness -> publish
```

`stage` calls [stage.yml](../.github/workflows/stage.yml) with the validated
`version`. It checks out the event's source SHA, installs SDK `10.0.402`, acquires
the pinned Inno compiler and runtime/UI-driver dependency caches, then invokes
`scripts/release/Stage-TestPackage.ps1`. That script runs `Build-Package.ps1`,
`Build-TestPackage.ps1`, and positive file-only validation with `Test-Package.ps1`.
It builds the application ZIP, installer and independent offline test ZIP without
launching them. See [packaging.md](packaging.md) for the package contract.

The producer writes `artifacts/staged/` and uploads two separate artifacts:

| Artifact | Contents |
| --- | --- |
| `test-package-<full-source-SHA>` | Complete distribution asset set listed below |
| `test-readiness-<full-source-SHA>` | Generated `readiness.json` |

`checks` calls `verify.yml` with `package_run_id` equal to the current
`github.run_id`. Both the non-GUI source checks and package checks must pass.
The validated distribution artifact is uploaded in this same run, then downloaded
by `readiness` and `publish` to `artifacts/staged-package/`. Both jobs also download
the same-run readiness artifact. No external staging run or second producer is
required by the publication path.

`stage.yml` also retains a manual `workflow_dispatch` with a `version` input for
staging alone. `verify.yml` can inspect such an artifact through its optional
`package_run_id`. Neither standalone operation authorizes publication.

## Readiness and publication permissions

The producer generates a schema-2 `readiness.json` with `packageIntegrity`,
`runtimeReadiness`, and `publicationReadiness` sections. This receipt is an
Actions artifact, not a committed source file or a release asset. Package
integrity binds the exact commit, version, Test channel, manifests and package
hashes. Runtime readiness remains `pending`.

`readiness` and `publish` download this receipt under
`.omo/evidence/phase-one/task-15/staged/` and generate `authorization.json` there.
The authorization record binds the validated tag request, source, version,
channel, application executable, application ZIP, installer, independent test ZIP
and both manifests. It also records the tag object and workflow run ID.
The receipt binds that authorization file by SHA256.

`scripts/release/Test-ReleaseReadiness.ps1` receives `-ArtifactDir`,
`-ExpectedCommit` and `-ReceiptPath`. With `-RequirePublication`, it additionally
requires the exact Test authorization. It checks manifest/receipt identities,
the asset allowlist and every checksum. The manifest may retain its immutable
build-time `packageIntegrity.status=requires-validation` and `receipt=external`;
the generated external receipt supplies the validation result. Runtime evidence
is not fabricated or promoted to passed.

All three workflows default to `contents: read` and `actions: read`, and checkout
does not persist credentials. Only `publish` requests `contents: write`, after
the preceding jobs and readiness outputs succeed. That job uses the
`test-prerelease` environment and remains subject to its configured protection
rules. The publishing step receives the built-in `GITHUB_TOKEN` as `GH_TOKEN`.

Immediately before the public write, the publish job revalidates the downloaded
assets and authorization. `gh release create` uses the existing matching tag with
`--verify-tag --prerelease`; it neither creates a missing tag nor promotes Stable.

## Distribution assets and post-publication checks

The exact release asset set is:

- `CorgiBlackboard-<version>-win-x64.zip`
- `CorgiBlackboard-<version>-win-x64-setup.exe`
- `CorgiBlackboard-<version>-win-x64-tests.zip`
- `TEST-CHECKLIST.md`
- `RELEASE-NOTES.md`
- `THIRD-PARTY-NOTICES.md`
- `feedback-guide.md`
- `release-manifest.json`
- `test-manifest.json`
- `SHA256SUMS`

No test logs, TRX files, readiness receipts, authorization records, screenshots,
local diagnostics or other test evidence are release assets. Release notes must
state known limitations, pending GUI/installation/classroom work, and the manual
test and feedback instructions.

After publication, verify the actual prerelease and its full asset inventory,
download the assets without credentials, and compare sizes, SHA256 values and
version/source identities. Record actual hosted CI and download results
separately; this guide describes the workflow contract and is not a claim that a
particular run, release or runtime test passed.

Official references:

- [Workflow syntax and permissions](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax)
- [Reusable workflows](https://docs.github.com/en/actions/how-tos/reuse-automations/reuse-workflows)
- [Workflow artifacts](https://docs.github.com/en/actions/how-tos/writing-workflows/choosing-what-your-workflow-does/storing-and-sharing-data-from-a-workflow)
