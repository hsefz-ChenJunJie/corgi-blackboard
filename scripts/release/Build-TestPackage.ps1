[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArtifactDir,
    [Parameter(Mandatory)][string]$DotnetPath,
    [string]$RuntimePackageRoot = $env:NUGET_PACKAGES,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$')][string]$DriverRevision
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$artifact = (Resolve-Path -LiteralPath $ArtifactDir).ProviderPath
$manifestPath = Join-Path $artifact 'release-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.channel -cne 'Test' -or $manifest.runtimeIdentifier -cne 'win-x64' -or
    $manifest.version -notmatch '^0\.1\.0-test\.[1-9][0-9]*$' -or
    $manifest.commit -notmatch '^[a-f0-9]{40}$' -or
    $manifest.exeSha256 -notmatch '^[a-f0-9]{64}$') {
    throw 'The application artifact is not a valid Test win-x64 package.'
}
$dotnetItem = Get-Item -LiteralPath $DotnetPath
if (-not $dotnetItem.Exists) { throw "dotnet executable not found: $DotnetPath" }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$version = [string]$manifest.version
$previousGitMaster = $env:GIT_MASTER
$previousNugetPackages = $env:NUGET_PACKAGES
Push-Location $root
try {
$env:GIT_MASTER = '1'
if ($RuntimePackageRoot) {
    if (-not (Test-Path -LiteralPath $RuntimePackageRoot)) { throw "Runtime package cache not found: $RuntimePackageRoot" }
    $env:NUGET_PACKAGES = [IO.Path]::GetFullPath($RuntimePackageRoot)
}
$head = (& git rev-parse --verify HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $head -notmatch '^[a-f0-9]{40}$' -or $head -cne $manifest.commit) {
    throw 'Application manifest commit differs from the current source HEAD.'
}
$trackedChanges = @(& git diff --name-only --no-renames --)
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect tracked source changes.' }
$trackedChanges += @(& git diff --cached --name-only --no-renames --)
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect staged source changes.' }
if ($DriverRevision) {
    $forbiddenChanges = @($trackedChanges | Where-Object {
        $_ -cnotmatch '^tests/CorgiBlackboard\.UiTests/' -and $_ -cne 'scripts/release/Build-TestPackage.ps1'
    })
    if ($forbiddenChanges.Count -ne 0) {
        throw "DriverRevision only permits test-driver and package-script changes: $($forbiddenChanges -join ', ')"
    }
} elseif ($trackedChanges.Count -ne 0) {
    throw 'Tracked source changes require a clean committed build, or an explicit DriverRevision for test-driver-only repairs.'
}
$driverInputPaths = @(& git ls-files --cached --others --exclude-standard -- `
    tests/CorgiBlackboard.UiTests tests/Fixtures src Directory.Build.props Directory.Build.targets `
    Directory.Packages.props global.json NuGet.Config scripts/release/Build-TestPackage.ps1)
if ($LASTEXITCODE -ne 0) { throw 'Cannot inventory driver source inputs.' }
if (-not $DriverRevision) {
    $untrackedDriverInputs = @(& git ls-files --others --exclude-standard -- tests/CorgiBlackboard.UiTests)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect untracked driver inputs.' }
    if ($untrackedDriverInputs.Count -ne 0) { throw 'Uncommitted driver inputs require an explicit DriverRevision.' }
}
$driverInputs = @($driverInputPaths | Sort-Object -Unique | Where-Object {
    $_ -notmatch '(^|/)(bin|obj)/' -and (Test-Path -LiteralPath (Join-Path $root $_) -PathType Leaf)
} | ForEach-Object {
    [ordered]@{ path = $_; sha256 = (Get-FileHash -LiteralPath (Join-Path $root $_) -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$zipName = "CorgiBlackboard-$version-win-x64-tests.zip"
$zipPath = Join-Path $artifact $zipName
if (Test-Path -LiteralPath $zipPath) { throw "Refusing to replace existing test package: $zipName" }
$work = Join-Path ([IO.Path]::GetTempPath()) ("corgi-test-package-" + [guid]::NewGuid().ToString('N'))
$stage = Join-Path $work 'package'
New-Item -ItemType Directory -Path $stage | Out-Null
try {
    $appPayload = Join-Path $artifact 'payload'
    if (-not (Test-Path -LiteralPath (Join-Path $appPayload 'CorgiBlackboard.App.exe'))) {
        $appZip = Join-Path $artifact "CorgiBlackboard-$version-win-x64.zip"
        if (-not (Test-Path -LiteralPath $appZip)) { throw 'Application ZIP is missing.' }
        $appPayload = Join-Path $work 'app-payload'
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::ExtractToDirectory($appZip, $appPayload)
    }
    $appExe = Join-Path $appPayload 'CorgiBlackboard.App.exe'
    if (-not (Test-Path -LiteralPath $appExe)) { throw 'Application payload does not contain CorgiBlackboard.App.exe.' }
    if ((Get-FileHash -LiteralPath $appExe -Algorithm SHA256).Hash.ToLowerInvariant() -cne $manifest.exeSha256) {
        throw 'Application executable hash differs from release-manifest.json.'
    }
    $publish = Join-Path $work 'publish'
    $project = Join-Path $root 'tests/CorgiBlackboard.UiTests/CorgiBlackboard.UiTests.csproj'
    $feed = Join-Path $work 'offline-feed'
    $ridLock = Join-Path $work 'ui-tests-win-x64.packages.lock.json'
    New-Item -ItemType Directory -Path $feed | Out-Null
    & $dotnetItem.FullName 'restore' $project '-r' 'win-x64' '--force-evaluate' `
        "-p:NuGetLockFilePath=$ridLock" "-p:RestoreSources=$feed" '-p:NuGetAudit=false' 2>&1 |
        Tee-Object -FilePath (Join-Path $work 'restore.log')
    if ($LASTEXITCODE -ne 0) { throw "Test driver offline restore exited $LASTEXITCODE." }
    $publishArgs = @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '--no-restore', '-p:PublishTrimmed=false', '-p:PublishSingleFile=false', '-p:DebugType=None',
        "-p:NuGetLockFilePath=$ridLock", "-p:Version=$version", "-p:InformationalVersion=$version",
        "-p:DiagnosticGitHash=$head", '-o', $publish)
    & $dotnetItem.FullName @publishArgs 2>&1 | Tee-Object -FilePath (Join-Path $work 'publish.log')
    if ($LASTEXITCODE -ne 0) { throw "Test driver publish exited $LASTEXITCODE." }

    $driverNames = @('CorgiBlackboard.UiTests.exe', 'CorgiBlackboard.UiTests.dll',
        'CorgiBlackboard.UiTests.deps.json', 'CorgiBlackboard.UiTests.runtimeconfig.json')
    foreach ($name in $driverNames) {
        $source = Join-Path $publish $name
        if (-not (Test-Path -LiteralPath $source)) { throw "Published driver is missing: $name" }
    }
    Get-ChildItem -LiteralPath $publish -File -Recurse | ForEach-Object {
        $relative = $_.FullName.Substring($publish.Length + 1)
        $destination = Join-Path $stage $relative
        New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($destination)) | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $destination
    }
    $fixtureSource = Join-Path $root 'tests/Fixtures'
    if (Test-Path -LiteralPath $fixtureSource) {
        Copy-Item -LiteralPath $fixtureSource -Destination (Join-Path $stage 'fixtures') -Recurse
    }
    Copy-Item -LiteralPath (Join-Path $artifact 'TEST-CHECKLIST.md') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $artifact 'feedback-guide.md') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $artifact 'THIRD-PARTY-NOTICES.md') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $artifact 'release-manifest.json') -Destination $stage
    foreach ($notice in @('DOTNET-RUNTIME-LICENSE.txt', 'DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt',
            'WINDOWS-DESKTOP-RUNTIME-LICENSE.txt')) {
        $noticePath = Join-Path $appPayload $notice
        if (-not (Test-Path -LiteralPath $noticePath)) { throw "Application runtime notice is missing: $notice" }
        Copy-Item -LiteralPath $noticePath -Destination (Join-Path $stage $notice)
    }
    $licensePackages = [ordered]@{
        'FlaUI.Core' = [ordered]@{ cache = 'flaui.core/5.0.0'; type = 'file'; expression = 'LICENSE.txt'; source = $null }
        'FlaUI.UIA3' = [ordered]@{ cache = 'flaui.uia3/5.0.0'; type = 'file'; expression = 'LICENSE.txt'; source = $null }
        'Microsoft.NET.Test.Sdk' = [ordered]@{ cache = 'microsoft.net.test.sdk/17.14.1'; type = 'expression'; expression = 'MIT'; source = (Join-Path $root 'docs/licenses/MIT.txt') }
        'xunit' = [ordered]@{ cache = 'xunit/2.9.3'; type = 'expression'; expression = 'Apache-2.0'; source = (Join-Path $root 'docs/licenses/Apache-2.0.txt') }
        'xunit.runner.visualstudio' = [ordered]@{ cache = 'xunit.runner.visualstudio/3.1.4'; type = 'expression'; expression = 'Apache-2.0'; source = (Join-Path $root 'docs/licenses/Apache-2.0.txt') }
    }
    foreach ($package in $licensePackages.Keys) {
        $spec = $licensePackages[$package]
        $packageRoot = Join-Path $env:NUGET_PACKAGES $spec.cache
        $nuspec = @(Get-ChildItem -LiteralPath $packageRoot -Filter '*.nuspec' -File -ErrorAction SilentlyContinue)
        $nuspecText = $null
        $nuspecName = "$package.nuspec"
        $nupkg = $null
        $nuspecEntry = $null
        $nuspecArchive = $null
        if ($nuspec.Count -eq 1) {
            $nuspecText = Get-Content -LiteralPath $nuspec[0].FullName -Raw
        } elseif ($nuspec.Count -eq 0) {
            $nupkg = @(Get-ChildItem -LiteralPath $packageRoot -Filter '*.nupkg' -File -ErrorAction SilentlyContinue)
            if ($nupkg.Count -ne 1) { throw "Pinned nuspec is missing or ambiguous: $package" }
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            $nuspecArchive = [IO.Compression.ZipFile]::OpenRead($nupkg[0].FullName)
            $nuspecEntry = @($nuspecArchive.Entries | Where-Object { $_.FullName -match '(^|/)[^/]+\.nuspec$' })
            if ($nuspecEntry.Count -ne 1) { throw "Pinned nupkg nuspec is missing or ambiguous: $package" }
            $reader = [IO.StreamReader]::new($nuspecEntry[0].Open(), [Text.Encoding]::UTF8, $true)
            try { $nuspecText = $reader.ReadToEnd() } finally { $reader.Dispose() }
        } else {
            throw "Pinned nuspec is missing or ambiguous: $package"
        }
        try { [xml]$nuspecXml = $nuspecText }
        finally { if ($nuspecArchive) { $nuspecArchive.Dispose() } }
        $licenseNode = $nuspecXml.package.metadata.license
        if ($licenseNode.type -cne $spec.type -or [string]$licenseNode.'#text' -cne $spec.expression) {
            throw "Pinned nuspec license expression differs: $package"
        }
        [IO.File]::WriteAllText((Join-Path $stage $nuspecName), $nuspecText, [Text.UTF8Encoding]::new($false))
        if ($spec.source) {
            if (-not (Test-Path -LiteralPath $spec.source)) { throw "Checked-in license source is missing: $($spec.expression)" }
            Copy-Item -LiteralPath $spec.source -Destination (Join-Path $stage ("$package-$($spec.expression).txt"))
        } else {
            $licenseFiles = @(Get-ChildItem -LiteralPath $packageRoot -File -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -match '(?i)^(license|notice|third.party)' })
            if ($licenseFiles.Count -eq 0) { throw "License text is missing from cached package: $package" }
            foreach ($licenseFile in $licenseFiles) {
                Copy-Item -LiteralPath $licenseFile.FullName -Destination (Join-Path $stage ("$package-" + $licenseFile.Name))
            }
        }
        $packageNotices = @(Get-ChildItem -LiteralPath $packageRoot -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '(?i)^(notice|copyright)' })
        foreach ($noticeFile in $packageNotices) {
            Copy-Item -LiteralPath $noticeFile.FullName -Destination (Join-Path $stage ("$package-" + $noticeFile.Name))
        }
    }
    @'
# 独立离线桌面测试包

本包对应 CorgiBlackboard `Test` 版本。无需源码、SDK、NuGet 恢复或联网即可在 Windows x64 上运行。先解压到本地目录，再使用 Windows PowerShell 5.1：

```powershell
.\Run-Scenario.ps1 -Scenario ReleaseSmoke -AppPath C:\path\CorgiBlackboard.App.exe -EvidenceDir .\results\smoke -ConfirmDesktop
.\Run-All.ps1 -AppPath C:\path\CorgiBlackboard.App.exe -EvidenceRoot .\results -ConfirmDesktop
```

启动前会核对包内 `SHA256SUMS`、`Test` 通道、版本、应用源提交、应用 exe 和 App/Infrastructure/Core 三个托管程序集的 SHA256；桌面操作必须显式传入 `-ConfirmDesktop`。`EvidenceRoot` 和单场景结果目录都必须是尚不存在的新目录。测试不会安装软件或修改系统配置。结果目录保留 `result.json`、`actions.jsonl`、截图、UIA 信息和 `cleanup.json`；失败与不支持原因必须保留。

`test-manifest.json` 的 `commit` 和 `driverSourceCommit` 标明原始已发布应用及驱动基线提交。可选 `driverRevision` 标明本地驱动修订；此时不能只用源提交声称驱动与原版相同，`driverInputs` 逐项记录实际源码、项目文件和打包脚本的 SHA256。应用身份仍绑定原始发布提交，不随驱动修订改变。

八个入口：`ShellLayout`、`BoardInkErase`、`TouchCancel`、`ImageWindows`、`ImageFitRestore`、`DiagnosticsExport`、`HarnessFailure`、`ReleaseSmoke`。`Run-All` 运行除 `HarnessFailure` 外的七项，即使某项失败也继续独立场景，最后报告所有失败并以非零状态退出。`HarnessFailure` 是故意失败的驱动自检，只能单独显式运行，预期非零退出，不能作为产品通过：

```powershell
.\Run-Scenario.ps1 -Scenario HarnessFailure -AppPath C:\path\CorgiBlackboard.App.exe -EvidenceDir .\selftest-new -ConfirmDesktop
```

阅读 `result.json` 时分别检查 `scenarioPassed`（场景断言是否通过）、`evidenceComplete`（证据是否齐全）以及驱动错误字段（harness errors）。断言通过不代表证据完整；驱动错误也不能当作产品通过。启动校验失败可能尚未生成结果文件，须同时保留控制台错误。合成触控不替代实体笔、真实屏幕、投屏或整节课人工测试。GUI 实际执行在本构建环境为“未测”，等待 Windows 交互桌面验证。

请把结果目录和 `feedback-guide.md` 一起人工反馈；本包不会自动上传日志、截图或证据。`TEST-CHECKLIST.md` 包含 19 项课堂检查和隐私边界。
'@ | Set-Content -LiteralPath (Join-Path $stage 'README-test-package.md') -Encoding UTF8
    @'
[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('ShellLayout','BoardInkErase','TouchCancel','ImageWindows','ImageFitRestore','DiagnosticsExport','HarnessFailure','ReleaseSmoke')][string]$Scenario,
    [Parameter(Mandatory)][string]$AppPath, [Parameter(Mandatory)][string]$EvidenceDir, [switch]$ConfirmDesktop)
$ErrorActionPreference = 'Stop'
if (-not $ConfirmDesktop) { throw 'Desktop automation is opt-in. Add -ConfirmDesktop after reviewing the package instructions.' }
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$packageRoot = [IO.Path]::GetFullPath($here).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
$checked = @{}
foreach ($line in Get-Content -LiteralPath (Join-Path $here 'SHA256SUMS')) {
    if ($line -notmatch '^([a-f0-9]{64})  (.+)$') { throw 'Invalid package checksum entry.' }
    $expected = $Matches[1]
    $relative = $Matches[2]
    if ([IO.Path]::IsPathRooted($relative) -or $relative -match '(^|[\\/])\.\.?([\\/]|$)|[:*?"<>|]' -or
        $checked.ContainsKey($relative)) { throw "Unsafe or duplicate package checksum path: $relative" }
    $file = [IO.Path]::GetFullPath((Join-Path $here $relative))
    if (-not $file.StartsWith($packageRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Package file is missing or outside package: $relative" }
    $item = Get-Item -LiteralPath $file
    while ($item.FullName -ne $here) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Package links are not permitted: $relative" }
        $item = Get-Item -LiteralPath (Split-Path -Parent $item.FullName)
        if ($null -eq $item) { throw "Package path has no package parent: $relative" }
    }
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expected) {
        throw "Package checksum differs: $relative"
    }
    $checked[$relative] = $true
}
foreach ($required in @('CorgiBlackboard.UiTests.exe','CorgiBlackboard.UiTests.dll',
        'CorgiBlackboard.UiTests.deps.json','CorgiBlackboard.UiTests.runtimeconfig.json',
        'test-manifest.json','release-manifest.json','Run-Scenario.ps1','Run-All.ps1')) {
    if (-not $checked.ContainsKey($required)) { throw "Package checksum is missing: $required" }
}
$app = [IO.Path]::GetFullPath($AppPath)
$manifest = Get-Content (Join-Path $here 'test-manifest.json') -Raw | ConvertFrom-Json
foreach ($file in $manifest.files) {
    if (-not $checked.ContainsKey([string]$file.path)) { throw "Package checksum is missing: $($file.path)" }
    if ((Get-FileHash -LiteralPath (Join-Path $here $file.path) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $file.sha256) {
        throw "Package file differs from test-manifest.json: $($file.path)"
    }
}
$release = Get-Content (Join-Path $here 'release-manifest.json') -Raw | ConvertFrom-Json
if ($release.channel -cne 'Test' -or $release.version -cne $manifest.version -or $release.commit -cne $manifest.commit) { throw 'Test and application identities differ.' }
if (-not (Test-Path -LiteralPath $app)) { throw "Application executable not found: $app" }
$hash = (Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash.ToLowerInvariant()
if ($hash -cne $manifest.appExeSha256) { throw 'Application executable SHA256 differs from the bound test package.' }
$appRoot = Split-Path -Parent $app
$managedPayload = @{
    'CorgiBlackboard.App.dll' = [string]$release.managedPayload.appSha256
    'CorgiBlackboard.Infrastructure.dll' = [string]$release.managedPayload.infrastructureSha256
}
$coreFile = @($release.files | Where-Object { $_.path -match '(^|/)CorgiBlackboard\.Core\.dll$' })
if ($coreFile.Count -ne 1) { throw 'Release manifest must contain exactly one Core payload hash.' }
$managedPayload['CorgiBlackboard.Core.dll'] = [string]$coreFile[0].sha256
foreach ($name in $managedPayload.Keys) {
    if ($managedPayload[$name] -notmatch '^[a-f0-9]{64}$') {
        throw "Release manifest has no valid managed payload hash: $name"
    }
    $path = Join-Path $appRoot $name
    if (-not (Test-Path -LiteralPath $path)) { throw "Application payload is missing: $name" }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $managedPayload[$name]) {
        throw "Application payload hash differs from release-manifest.json: $name"
    }
}
if (Test-Path -LiteralPath $EvidenceDir) { throw 'Evidence directory already exists; choose a new directory.' }
# DesktopQaProgram owns creation of the leaf so it can reject pre-created evidence.
& (Join-Path $here 'CorgiBlackboard.UiTests.exe') '--post-release-desktop' $Scenario $app ([IO.Path]::GetFullPath($EvidenceDir)) $manifest.commit
exit $LASTEXITCODE
'@ | Set-Content -LiteralPath (Join-Path $stage 'Run-Scenario.ps1') -Encoding UTF8
    @'
[CmdletBinding()]
param([Parameter(Mandatory)][string]$AppPath, [Parameter(Mandatory)][string]$EvidenceRoot, [switch]$ConfirmDesktop)
$ErrorActionPreference = 'Stop'
if (-not $ConfirmDesktop) { throw 'Desktop automation is opt-in. Add -ConfirmDesktop after reviewing the package instructions.' }
if (Test-Path -LiteralPath $EvidenceRoot) { throw 'Evidence root already exists; choose a new directory.' }
New-Item -ItemType Directory -Path $EvidenceRoot | Out-Null
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$scenarios = 'ShellLayout','BoardInkErase','TouchCancel','ImageWindows','ImageFitRestore','DiagnosticsExport','ReleaseSmoke'
$failures = @()
foreach ($scenario in $scenarios) {
    try {
        $LASTEXITCODE = 0
        & (Join-Path $here 'Run-Scenario.ps1') -Scenario $scenario -AppPath $AppPath -EvidenceDir (Join-Path $EvidenceRoot $scenario) -ConfirmDesktop
        if ($LASTEXITCODE -ne 0) { $failures += "${scenario}: exit $LASTEXITCODE" }
    } catch {
        $failures += "${scenario}: $($_.Exception.Message)"
    }
}
if ($failures.Count -ne 0) {
    foreach ($failure in $failures) { [Console]::Error.WriteLine($failure) }
    exit 1
}
exit 0
'@ | Set-Content -LiteralPath (Join-Path $stage 'Run-All.ps1') -Encoding UTF8
    $testFiles = @(Get-ChildItem -LiteralPath $stage -File -Recurse | ForEach-Object {
        [ordered]@{ path = $_.FullName.Substring($stage.Length + 1).Replace('\','/'); size = $_.Length
            sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    $driverAssembly = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $publish 'CorgiBlackboard.UiTests.dll'))
    if ($driverAssembly.Name -cne 'CorgiBlackboard.UiTests') { throw 'Published driver assembly identity differs.' }
    $testManifest = [ordered]@{ schemaVersion = 1; packageType = 'offline-gui-test-driver'; version = $version
        channel = 'Test'; commit = $manifest.commit; driverSourceCommit = $head; driverRevision = $DriverRevision
        driverInputs = $driverInputs; appExeSha256 = $manifest.exeSha256
        scenarios = @('ShellLayout','BoardInkErase','TouchCancel','ImageWindows','ImageFitRestore','DiagnosticsExport','HarnessFailure','ReleaseSmoke')
        selfContained = $true; runtimeIdentifier = 'win-x64'; guiExecution = 'untested'; files = $testFiles }
    $testManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $stage 'test-manifest.json') -Encoding UTF8
    Copy-Item -LiteralPath (Join-Path $stage 'test-manifest.json') -Destination (Join-Path $artifact 'test-manifest.json')
    $sums = @(Get-ChildItem -LiteralPath $stage -File -Recurse | Sort-Object FullName | ForEach-Object {
        (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.FullName.Substring($stage.Length + 1).Replace('\','/')
    })
    [IO.File]::WriteAllLines((Join-Path $stage 'SHA256SUMS'), $sums)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zipPath)
    $assetHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Add-Content -LiteralPath (Join-Path $artifact 'SHA256SUMS') -Value "$assetHash  $zipName"
    $rootManifestHash = (Get-FileHash -LiteralPath (Join-Path $artifact 'test-manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    Add-Content -LiteralPath (Join-Path $artifact 'SHA256SUMS') -Value "$rootManifestHash  test-manifest.json"
    Write-Output "TEST_PACKAGE=$zipPath"
    Write-Output "TEST_PACKAGE_SHA256=$assetHash"
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    $env:GIT_MASTER = $previousGitMaster
    $env:NUGET_PACKAGES = $previousNugetPackages
    Pop-Location
}
} finally {
    $env:GIT_MASTER = $previousGitMaster
    $env:NUGET_PACKAGES = $previousNugetPackages
    if ((Get-Location).Path -eq $root) { Pop-Location }
}
