# Bootstrap dependencies and support matrix

## Toolchain

- .NET SDK `10.0.402`, stable .NET 10 LTS servicing release. The repository pins it in `global.json`; it is the SDK used for the recorded Windows build.
- Official archive: `https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.402/dotnet-sdk-10.0.402-win-x64.zip`
- Published SHA512: `ae4fd528cb7e9f6af6aaf7de36715a69f761f183744f1bb3025d19f5a92447090ec561410e46266c1d10a63781ed6f3de771760bc76c876d87daabc367b898cd`
- Installed user-locally at `E:/corgi-blackboard-tools/dotnet10`; no machine files changed. The SDK archive checksum is Microsoft-published metadata, not a locally recomputed archive hash.
- Official release/support references: [.NET 10 release notes](https://github.com/dotnet/core/tree/main/release-notes/10.0), [.NET 10 supported OS versions](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md), and [Microsoft .NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core).

## Packages

Central package management uses exact versions in `Directory.Packages.props`; per-project `packages.lock.json` files are committed and `dotnet restore --locked-mode` verifies them. Restore uses NuGet's official v3 feed (`https://api.nuget.org/v3/index.json`). Each direct package's resolved version and `contentHash` below match the corresponding lock entry. NuGet generates these base64 SHA-512 content-integrity hashes from the package archive retrieved from that feed; they are not independently recomputed here.

| Package | Version | Official source | Locked contentHash (base64 SHA-512) |
| --- | --- | --- | --- |
| Microsoft.NET.Test.Sdk | 17.14.1 | https://www.nuget.org/packages/Microsoft.NET.Test.Sdk/17.14.1 | `HJKqKOE+vshXra2aEHpi2TlxYX7Z9VFYkr+E5rwEvHC8eIXiyO+K9kNm8vmNom3e2rA56WqxU+/N9NJlLGXsJQ==` |
| xunit | 2.9.3 | https://www.nuget.org/packages/xunit/2.9.3 | `TlXQBinK35LpOPKHAqbLY4xlEen9TBafjs0V5KnA4wZsoQLQJiirCR4CbIXvOH8NzkW4YeJKP5P/Bnrodm0h9Q==` |
| xunit.runner.visualstudio | 3.1.4 | https://www.nuget.org/packages/xunit.runner.visualstudio/3.1.4 | `5mj99LvCqrq3CNi06xYdyIAXOEh+5b33F2nErCzI5zWiDdLHXiPXEWFSUAF8zlIv0ZWqjZNCwHTQeAPYbF3pCg==` |
| FlaUI.Core | 5.0.0 | https://www.nuget.org/packages/FlaUI.Core/5.0.0 | `VWWYqwjsYkii2BrUsz5+bpXEp27NherKhLt19hTxHwJP4KrfyZoeo1FdBov4ZTh655wZAKDGIjR7NJLDesuzeg==` |
| FlaUI.UIA3 | 5.0.0 | https://www.nuget.org/packages/FlaUI.UIA3/5.0.0 | `s1Yy1RsRJ0OXvmtxQlNJd0cu4sfvN3PFFu6skBGK8VlwPl/b38E0V5kCbo2gvkuhRIu4Itfxjdo7DUMF7qJw4Q==` |

The UI-test project references FlaUI.Core and FlaUI.UIA3 5.0.0 for the planned Windows UIA3 harness. NuGet lists .NET 10 Windows as a computed compatible target for FlaUI.Core 5.0.0; this package metadata does not assert that this application or GUI harness ran on every Windows release. No package version floats.

## Licenses

Licenses were read from each restored package's own nuspec or packaged `LICENSE.txt`, not from index metadata. Microsoft.NET.Test.Sdk 17.14.1 is MIT; xunit 2.9.3 and xunit.runner.visualstudio 3.1.4 are Apache-2.0; FlaUI.Core 5.0.0 and FlaUI.UIA3 5.0.0 ship an MIT `LICENSE.txt`. All five are test-only. The application projects reference no packages. See `THIRD-PARTY-NOTICES.md`.

## Support matrix

This matrix distinguishes Microsoft's framework support baseline from this project's intended development targets and actual machine verification. A framework-supported OS is not evidence that this application was tested there.

| OS / architecture | .NET 10 baseline and project status |
| --- | --- |
| Windows 10 Enterprise/LTSC 21H2 x64 | Microsoft lists this as a .NET 10-supported Windows version; this project's runtime and GUI behavior are untested |
| Windows 10 22H2 x64 | Requested development target, but Microsoft does not list it as a .NET 10-supported Windows version; Windows 10 22H2 reached end of servicing on 2025-10-14; project runtime and GUI behavior are untested |
| Windows 11 x64 | Microsoft's .NET 10 baseline lists supported Windows 11 releases; this project's runtime and GUI behavior are untested |
| Other Windows versions/architectures | Not claimed |

Task-1 verification ran restore and Release build on the available Windows host only; it did not run the app or UI tests. Windows 10 22H2 x64 and Windows 11 x64 remain requested development targets, not verified support claims. Microsoft currently limits .NET 10 support on Windows 10 to listed Enterprise/LTSC releases. See [.NET 10 supported operating systems](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md), [Install .NET on Windows](https://learn.microsoft.com/en-us/dotnet/core/install/windows), [Windows 10 release information](https://learn.microsoft.com/en-us/windows/release-health/release-information), and [supported Windows client versions](https://learn.microsoft.com/en-us/windows/release-health/supported-versions-windows-client).

## Provenance

The repository is the approved public HTTPS remote. No reference snapshots, credentials, or classroom data are packaged. The .NET archive SHA512 above is the published Microsoft checksum; NuGet lock files record exact resolved package versions and content hashes for the assets used by each project.

### Offline package build dependencies

Task 14 pins Inno Setup **6.7.4** as a build tool. The official
[download page](https://jrsoftware.org/isdl.php) links the
[6.7.4 distribution](https://github.com/jrsoftware/issrc/releases/download/is-6_7_4/innosetup-6.7.4.exe).
The immutable official [release API](https://api.github.com/repos/jrsoftware/issrc/releases/tags/is-6_7_4)
publishes distribution SHA256
`a549929319f9120716677c685535ca4671e4b937b5fdc87e2585f32d8eb30e8c`.
The distribution must match this hash and its valid Pyrsys B.V. Authenticode
signature before compiler extraction. Its [source tag](https://github.com/jrsoftware/issrc/tree/is-6_7_4)
and [license](https://github.com/jrsoftware/issrc/blob/is-6_7_4/LICENSE.TXT)
identify the compiler release. The compiler distribution is never executed.
ISCC is extracted into a task-owned build cache and only its command-line
compilation is permitted. The exact extracted ISCC SHA256 and file version are
recorded in the task-14 compiler provenance receipt and package manifest.

Extraction uses build-only
[innounp 2.71.2](https://github.com/jrathlev/InnoUnpacker-Windows-GUI),
whose [Scoop manifest](https://raw.githubusercontent.com/ScoopInstaller/Main/master/bucket/innounp.json)
pins archive SHA256
`10fce27b6344b7674d5625cc34afca1c88c12183d957c6d93e401097fa2556cd`.
This GPL-3.0-only unpacker and its archive are not shipped in application assets.
The official Inno distribution's hash/signature independently bind the extracted
compiler; extraction does not establish publisher provenance by itself.

Self-contained `win-x64` publishing uses runtime packs
`Microsoft.NETCore.App.Runtime.win-x64`, `Microsoft.WindowsDesktop.App.Runtime.win-x64`
and SDK-restored `Microsoft.AspNetCore.App.Runtime.win-x64`, all exactly **10.0.13**,
from `https://api.nuget.org/v3/index.json`. Only publish-selected .NET/WPF files
ship. The task-14 runtime provenance receipt records each downloaded archive's
flat-container URL, measured SHA256, size and NuGet content hash. Downloaded build
dependencies are distinct from the generated application ZIP and setup hashes.
See [the package and readiness contract](../packaging.md).
