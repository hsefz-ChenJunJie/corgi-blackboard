# Offline Test package contract

Use the pinned .NET SDK 10.0.402 and .NET/WindowsDesktop runtime packs 10.0.13.
Acquire runtime packs once from `https://api.nuget.org/v3/index.json` with:

```powershell
dotnet restore src/CorgiBlackboard.App -r win-x64 --force-evaluate -p:NuGetLockFilePath=obj/release-win-x64.packages.lock.json -p:Version=0.1.0-test.1 -p:RuntimeFrameworkVersion=10.0.13 -p:SelfContained=true -p:RestoreSources=https://api.nuget.org/v3/index.json -p:NuGetAudit=false
```

The RID belongs to restore as well as publish. Platform-neutral committed locks
remain unchanged; RID/version-specific locks live under each project's ignored
`obj`. Build-Package validates the baseline locked restore, then performs a
cache-only RID restore and locked publish. No dependency version is upgraded.
Retain acquisition logs separately from generated release assets.

For the complete application and independent test package, use
`scripts/release/Stage-TestPackage.ps1` with `-Version`, a new `-ArtifactDir`,
`-DotnetPath`, `-CompilerPath`, `-CompilerProvenancePath` and
`-RuntimePackageRoot`. Acquire the pinned compiler and both application/runtime
and UI-driver dependency caches first; [stage.yml](../.github/workflows/stage.yml)
contains the hosted acquisition commands and passes these paths explicitly.
Staging invokes both builders before running `Test-Package.ps1 -StaticOnly`.
Validating the application builder's intermediate output alone is insufficient,
because the distribution allowlist also requires the independent test package.

The builder requires committed tracked source and records actual HEAD in the
manifest and Infrastructure assembly metadata. Build logs and staging payload
remain in the unique ignored `src/CorgiBlackboard.App/obj/package-*` directory,
whose path is printed as `BUILD_WORK`. It compiles ISCC only. Neither the
application nor generated setup is executed. The setup uses `lowest` privileges,
installs below the current user's local application data, downloads no runtime,
has no application launch action, and leaves diagnostic logs outside its
installation directory.

Compiler acceptance uses the pinned official distribution SHA256,
`a549929319f9120716677c685535ca4671e4b937b5fdc87e2585f32d8eb30e8c`, valid
Pyrsys B.V. Authenticode signature, extractor archive SHA256
`10fce27b6344b7674d5625cc34afca1c88c12183d957c6d93e401097fa2556cd`, and
extracted ISCC SHA256
`f02fa0e5f237b47d7ed1edae9b991866ec94fe02af61eb6a73e94f92fe8e0604`.
These values are read from and checked against the compiler provenance receipt.
The compiler's Windows file version is not used as provenance because official
ISCC metadata reports `0.0.0.0`; unknown or mismatched provenance is rejected.

The final distribution contains exactly ten files: the application ZIP, setup
EXE, independent `CorgiBlackboard-<version>-win-x64-tests.zip`,
TEST-CHECKLIST.md, RELEASE-NOTES.md, THIRD-PARTY-NOTICES.md, feedback-guide.md,
release-manifest.json, test-manifest.json and SHA256SUMS. Staging also generates
`readiness.json`, which is uploaded separately and excluded from distribution.
The application ZIP includes all untrimmed multifile
win-x64 self-contained application/runtime files and the exact license/notice
files from the resolved 10.0.13 runtime packs,
the 19-scenario checklist, payload manifest and recursive SHA256SUMS.

`release-manifest.json` schema 1 records `version`, `channel`, `commit`, `sdk`,
`runtimeIdentifier`, `selfContained`, `publishTrimmed`, `publishSingleFile`,
`exeSha256`, framework `dependencies`, lock hashes, exact runtime component
provenance and per-file `files[{path,size,sha256}]`. Runtime provenance records
each pinned package, version, NuGet source, content hash, archive SHA256,
resolved source path, destination name, size and SHA256 for every shipped
license or notice file. The .NET component ships its own
`THIRD-PARTY-NOTICES.TXT`; the Windows Desktop component ships only its own
`LICENSE`, because that pack has no third-party notice file. SDK-root or WPF SDK
notice substitution is rejected. `managedPayload.appSha256` and
`managedPayload.infrastructureSha256` bind the managed assemblies. Installer
metadata records `status=built`, `name`, `compilerVersion`, `compilerSha256` and
`execution=not-run`. The external SHA256SUMS binds the compiled setup, ZIP and
manifest; the manifest is identical inside ZIP and setup payload. A setup hash
cannot be embedded in its own input manifest without a circular dependency.

`version` is the release version, for example `0.1.0-test.1`.
`informationalBuildVersion` is exactly `version + "+" + commit`, where `commit`
is the full lowercase 40-character source SHA. The SDK appends that source
revision to the explicitly supplied informational base version. Infrastructure
`BuildVersion` must match this value, `GitHash` must match the full `commit`,
and `ReleaseChannel` must match `Test`. No short-prefix or arbitrary suffix is
accepted.
`Assert-PackageIdentity.ps1` is shared by the production ZIP inspection and
focused file-only identity checks. No arbitrary suffix is accepted or removed.

Integrity and runtime readiness are separate:

- Ordinary Test-Package exits 0 for valid ZIP/setup assets, including pending
  runtime evidence. Its JSON reports `packageValid=true`, `packageIntegrity=true`,
  `readinessChecked=false`, `releaseReadiness=false`, exact `version` and `commit`.
  `-StaticOnly` remains accepted with the same file-only behavior.
- The immutable build manifest has `packageIntegrity.status=requires-validation`
  and `receipt=external`; `tests.staticPackage=pending` describes build-time state.
  Downstream gates must use the successful external validation receipt, rather
  than modifying/repacking the already measured package to claim a test result.
- `releaseReadiness` is a boolean build-time claim, not a false-only schema.
  Integrity accepts either boolean and does not authorize publication.
- `-RequireReleaseReady -ReadinessReceipt <path>` additionally requires a true
  readiness receipt bound to `commit/version/channel` and three evidence bindings
  `evidence.desktop`, `evidence.offlineInstall`, `evidence.authorization`, each
  `{path,sha256}`. Evidence paths are repository-relative `.omo/evidence/*.json`.
  Each record binds `commit/version/channel/manifestSha256/exeSha256/zipSha256/
  installerSha256`. Desktop requires `status=passed` and
  `diagnosticsRuntime=passed`. Offline installation requires `status=passed`,
  `networkDisconnected=true`, `standardUser=true`, `runtimePreinstalled=false`,
  `chinesePathWithSpaces=true`, `uninstallPreservedLogs=true`. Authorization
  requires `authorized=true` and nonempty `userInstruction`.

GUI, native diagnostics, offline installation/uninstall, device/OS combinations
and classroom results remain pending until actual execution. Compilation and
file validation do not turn those results into passed. The runtime-required
`-RequireReleaseReady` mode above is not the Test publication gate. The hosted
Test path uses `Test-ReleaseReadiness.ps1 -RequirePublication`, which requires
package integrity and explicit annotated-tag authorization while retaining
pending runtime readiness. See [releases.md](releases.md) for the exact
authorization template, same-run artifacts and publication permissions.

The hosted staging workflow acquires the pinned SDK/compiler/runtime cache and
passes `-Version`, `-ReleaseChannel Test`, `-ArtifactDir`, `-DotnetPath`,
`-CompilerPath`, `-CompilerProvenancePath` and `-RuntimePackageRoot` to
`Build-Package.ps1`. It then invokes
`Build-TestPackage.ps1 -ArtifactDir <existing application package directory>
-DotnetPath <pinned dotnet.exe> -RuntimePackageRoot <cache>`. The resulting
independent `CorgiBlackboard-<version>-win-x64-tests.zip` is included in the
staged SHA set and is checked by the release readiness guard.

Official documentation:
[/dotnet/docs](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-restore)
and [/jrsoftware/issrc](https://jrsoftware.org/ishelp/topic_compilercmdline.htm).
See also [NuGet lock files](https://learn.microsoft.com/en-us/nuget/consume-packages/package-references-in-project-files#locking-dependencies),
[ISCC](https://jrsoftware.org/ishelp/topic_compilercmdline.htm) and
[lowest privileges](https://jrsoftware.org/ishelp/topic_setup_privilegesrequired.htm).
