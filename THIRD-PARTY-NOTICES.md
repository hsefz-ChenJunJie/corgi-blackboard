# Third-party notices

## Shipped with the application

The application projects (`CorgiBlackboard.App`, `CorgiBlackboard.Core`, `CorgiBlackboard.Infrastructure`) reference no NuGet packages. The self-contained Test package bundles the pinned .NET 10 runtime and Windows Desktop runtime (WPF) components from the resolved `10.0.13` runtime packs. Their own files are copied without SDK substitution:

| Component | Package version | Source files | Package destination |
| --- | --- | --- | --- |
| .NET runtime | `microsoft.netcore.app.runtime.win-x64` `10.0.13` | `LICENSE.TXT`, `THIRD-PARTY-NOTICES.TXT` | `DOTNET-RUNTIME-LICENSE.txt`, `DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt` |
| Windows Desktop runtime | `microsoft.windowsdesktop.app.runtime.win-x64` `10.0.13` | `LICENSE` | `WINDOWS-DESKTOP-RUNTIME-LICENSE.txt` |

The Windows Desktop runtime pack has no own third-party notice file. The package does not invent one from the SDK or WPF SDK.

## Shipped with the standalone offline test ZIP

| Package | Version | License | ZIP license text |
| --- | --- | --- | --- |
| Microsoft.NET.Test.Sdk | 17.14.1 | MIT | `Microsoft.NET.Test.Sdk-LICENSE.txt` or `NOTICE.txt` |
| xunit | 2.9.3 | Apache-2.0 | `xunit-LICENSE.txt` or `NOTICE.txt` |
| xunit.runner.visualstudio | 3.1.4 | Apache-2.0 | `xunit.runner.visualstudio-LICENSE.txt` or `NOTICE.txt` |
| FlaUI.Core | 5.0.0 | MIT | `FlaUI.Core-LICENSE.txt` |
| FlaUI.UIA3 | 5.0.0 | MIT | `FlaUI.UIA3-LICENSE.txt` |

These dependencies are shipped in the standalone test ZIP only. They are not part of the application ZIP or installer. The builder copies the cached package license and notice text verbatim and includes each copied file in the test ZIP inventory and checksum list.

## Reference sources

Repositories under `E:/corgi-blackboard/reference/reference` were read for orientation only. No source from them is copied into this repository or the application, so none of their licenses apply to shipped files. Their commits and licenses stay recorded in that snapshot's `MANIFEST.json`.
