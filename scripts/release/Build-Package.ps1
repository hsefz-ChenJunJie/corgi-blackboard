[CmdletBinding()]
param(
    [ValidatePattern('^0\.1\.0-test\.[1-9][0-9]*$')][string]$Version = '0.1.0-test.1',
    [ValidateSet('Test')][string]$ReleaseChannel = 'Test',
    [string]$ArtifactDir,
    [string]$DotnetPath = 'E:/corgi-blackboard-tools/dotnet10/dotnet.exe',
    [string]$RuntimePackageRoot = $(if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { 'C:/Users/Minecraft/.nuget/packages' }),
    [Parameter(Mandatory)][string]$CompilerPath,
    [string]$CompilerProvenancePath = 'E:/corgi-blackboard-tools/task14-compiler-recovery/compiler-provenance.json'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$dotnet = $DotnetPath
$sdk = '10.0.402'
if (-not $ArtifactDir) { $ArtifactDir = Join-Path $root "artifacts/release/$Version" }
if (-not [IO.Path]::IsPathRooted($ArtifactDir)) { $ArtifactDir = Join-Path $root $ArtifactDir }
$ArtifactDir = [IO.Path]::GetFullPath($ArtifactDir)
if (Test-Path -LiteralPath $ArtifactDir) { throw 'Use a new artifact directory; existing output is preserved.' }
Push-Location $root
$previousGitMaster = $env:GIT_MASTER
try {
    $env:GIT_MASTER = '1'
    if ((& $dotnet --version).Trim() -ne $sdk -or $LASTEXITCODE -ne 0) { throw "Required SDK: $sdk" }
    $commit = (& git rev-parse --verify HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[a-f0-9]{40}$') { throw 'Cannot resolve source commit.' }
    if (@(& git status --porcelain --untracked-files=no).Count -ne 0) { throw 'Commit tracked source changes before packaging.' }
    $compiler = Get-Item -LiteralPath $CompilerPath
    $provenance = Get-Content -LiteralPath $CompilerProvenancePath -Raw | ConvertFrom-Json
    if ($provenance.assetSize -ne 10604576 -or
        $provenance.assetSha256 -cne 'a549929319f9120716677c685535ca4671e4b937b5fdc87e2585f32d8eb30e8c' -or
        $provenance.authenticodeStatus -cne 'Valid' -or $provenance.signer -notmatch 'Pyrsys B\.V\.' -or
        $provenance.extractorVersion -cne '2.71.2' -or
        $provenance.extractorSha256 -cne '10fce27b6344b7674d5625cc34afca1c88c12183d957c6d93e401097fa2556cd' -or
        $provenance.compilerSha256 -cne 'f02fa0e5f237b47d7ed1edae9b991866ec94fe02af61eb6a73e94f92fe8e0604' -or
        [IO.Path]::GetFullPath($provenance.compilerPath) -cne [IO.Path]::GetFullPath($compiler.FullName)) {
        throw 'Compiler provenance does not match the pinned official Inno Setup 6.7.4 distribution.'
    }
    $compilerHash = (Get-FileHash -LiteralPath $compiler.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($compilerHash -cne $provenance.compilerSha256) { throw 'Extracted ISCC SHA256 differs from provenance.' }
    New-Item -ItemType Directory -Path $ArtifactDir | Out-Null
    $work = Join-Path $root ("src/CorgiBlackboard.App/obj/package-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $work | Out-Null
    $payload = Join-Path $work 'payload'
    New-Item -ItemType Directory -Path $payload | Out-Null
    if (-not (Test-Path -LiteralPath $RuntimePackageRoot)) { throw "Runtime package cache not found: $RuntimePackageRoot" }
    $runtimeComponents = @(
        [ordered]@{
            package = 'microsoft.netcore.app.runtime.win-x64'
            version = '10.0.13'
            source = 'https://api.nuget.org/v3/index.json'
            contentHash = 'AtNgFBRln5IDZonHHqMN5M/AigeXGWWOf3TB2nDE415hqiYiTPNyaFObfZBC6jXx8JlGZCIpQ6CEM0G+zf6NHQ=='
            packageSha256 = 'c66885d464697be75676b4d1fbca38b8806f3af01d65d27205b3f06add094444'
            sourcePath = $null
            files = @(
                [ordered]@{ sourcePath = 'LICENSE.TXT'; destination = 'DOTNET-RUNTIME-LICENSE.txt'; size = $null; sha256 = $null }
                [ordered]@{ sourcePath = 'THIRD-PARTY-NOTICES.TXT'; destination = 'DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt'; size = $null; sha256 = $null }
            )
        }
        [ordered]@{
            package = 'microsoft.windowsdesktop.app.runtime.win-x64'
            version = '10.0.13'
            source = 'https://api.nuget.org/v3/index.json'
            contentHash = 'xouaR618eD6SeEht2zW16YFBGtRGF6+qTs8nNSLqmBzrBl5di/tf9A1WwDYjGxqQln27Gds0I+WZ8DR2IUePCg=='
            packageSha256 = 'af044758d6bc23eca96b60c9ce13b38f211c3e736eef9458de3cadbbd3e20ff2'
            sourcePath = $null
            files = @(
                [ordered]@{ sourcePath = 'LICENSE'; destination = 'WINDOWS-DESKTOP-RUNTIME-LICENSE.txt'; size = $null; sha256 = $null }
            )
        }
    )
    foreach ($component in $runtimeComponents) {
        $componentRoot = Join-Path $runtimePackageRoot "$($component.package)/$($component.version)"
        $metadataPath = Join-Path $componentRoot '.nupkg.metadata'
        $archivePath = Join-Path $componentRoot "$($component.package).$($component.version).nupkg"
        if (-not (Test-Path -LiteralPath $metadataPath) -or -not (Test-Path -LiteralPath $archivePath)) {
            throw "Pinned runtime pack is missing: $($component.package) $($component.version)"
        }
        $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
        if ($metadata.source -cne $component.source -or $metadata.contentHash -cne $component.contentHash) {
            throw "Pinned runtime pack provenance differs: $($component.package)"
        }
        if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $component.packageSha256) {
            throw "Pinned runtime pack archive hash differs: $($component.package)"
        }
        $component.sourcePath = [IO.Path]::GetFullPath($componentRoot)
        foreach ($file in $component.files) {
            $sourcePath = Join-Path $componentRoot $file.sourcePath
            $destinationPath = Join-Path $payload $file.destination
            if (-not (Test-Path -LiteralPath $sourcePath)) { throw "Pinned runtime notice is missing: $($component.package)/$($file.sourcePath)" }
            Copy-Item -LiteralPath $sourcePath -Destination $destinationPath
            $file.size = (Get-Item -LiteralPath $sourcePath).Length
            $file.sha256 = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    # Acquisition is separate. Build uses only the prepopulated NuGet cache.
    $feed = Join-Path $work 'offline-feed'
    New-Item -ItemType Directory -Path $feed | Out-Null
    # Preserve committed platform-neutral locks; RID/Version-specific locks stay under ignored obj.
    & $dotnet restore src/CorgiBlackboard.App --locked-mode "-p:RestoreSources=$feed" -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw "Baseline locked restore exited $LASTEXITCODE." }
    $ridLock = 'obj/release-win-x64.packages.lock.json'
    & $dotnet restore src/CorgiBlackboard.App -r win-x64 --force-evaluate "-p:NuGetLockFilePath=$ridLock" `
        "-p:Version=$Version" -p:RuntimeFrameworkVersion=10.0.13 "-p:RestoreSources=$feed" -p:NuGetAudit=false `
        -p:SelfContained=true 2>&1 | Tee-Object -FilePath (Join-Path $work 'restore.log')
    if ($LASTEXITCODE -ne 0) { throw "Offline RID restore exited $LASTEXITCODE; runtime packs must already be cached." }
    $arguments = @('publish', 'src/CorgiBlackboard.App', '-c', 'Release', '-r', 'win-x64',
        '--self-contained', 'true', '-p:PublishTrimmed=false', '-p:PublishSingleFile=false',
        '-p:ReleaseChannel=Test', "-p:Version=$Version", "-p:InformationalVersion=$Version",
        '-p:IncludeSourceRevisionInInformationalVersion=true', "-p:DiagnosticGitHash=$commit",
        '-p:RestoreLockedMode=true', "-p:NuGetLockFilePath=$ridLock", '-p:RuntimeFrameworkVersion=10.0.13',
        "-p:RestoreSources=$feed", '-p:NuGetAudit=false',
        '-p:DebugType=None', '-o', $payload)
    & $dotnet @arguments 2>&1 | Tee-Object -FilePath (Join-Path $work 'publish.log')
    $publishExit = $LASTEXITCODE
    if ($publishExit -ne 0) { throw "Offline publish exited $publishExit; no package success is claimed." }
    $exeHash = (Get-FileHash -LiteralPath (Join-Path $payload 'CorgiBlackboard.App.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    $checklistPath = Join-Path $root "docs/testing/$Version-classroom-checklist.md"
    $checklist = [IO.File]::ReadAllText($checklistPath)
    $checklist = $checklist -replace '(?m)(\| [^|\r\n]*commit hash \| `)<PENDING-PACKAGE-BINDING>(` \|)', ('${1}' + $commit + '${2}')
    $checklist = $checklist.Replace('| exe SHA256 | `<PENDING-PACKAGE-BINDING>` |', "| exe SHA256 | ``$exeHash`` |")
    $checklist = $checklist -replace '(?m)^[^|#\r\n][^\r\n]*hash[^\r\n]*\r?$', 'Hashes above are measured package bindings; runtime validation remains pending.'
    $checklist += "`n`nPackage binding: $Version / $ReleaseChannel / $commit / exe SHA256 $exeHash.`n"
    $example = -join (@(0x6559, 0x5b66, 32, 0x6d4b, 0x8bd5, 92, 0x9ed1, 0x677f) | ForEach-Object { [char]$_ })
    $checklist += "Offline install, Chinese paths with spaces ($example), Windows 10/11, GUI and classroom results: pending.`n"
    [IO.File]::WriteAllText((Join-Path $payload 'TEST-CHECKLIST.md'), $checklist)
    Copy-Item -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.md') -Destination $payload
    Copy-Item -LiteralPath (Join-Path $root 'docs/testing/feedback-guide.md') -Destination $payload
    $notes = "# $Version`n`nTest interaction prototype; classroom verification pending.`n`n" +
        "Commit: $commit`nSDK: $sdk`nChannel: Test`nSelf-contained: win-x64`n`n" +
        "GUI, diagnostics runtime, offline installation and Windows 10/11 execution: pending.`n" +
        "Installer: user-level offline setup compiled with Inno Setup 6.7.4; installation untested.`n" +
        "Logs: %LOCALAPPDATA%\CorgiBlackboard\Logs; uninstall must retain logs.`n" +
        "Open TEST-CHECKLIST.md and feedback-guide.md beside the executable.`n" +
        "Board saving/recovery, PDF/PPT, selection and global undo are not implemented.`n"
    [IO.File]::WriteAllText((Join-Path $payload 'RELEASE-NOTES.md'), $notes)
    $files = @(Get-ChildItem -LiteralPath $payload -File -Recurse | Sort-Object FullName | ForEach-Object {
        [ordered]@{
            path = $_.FullName.Substring($payload.Length + 1).Replace('\', '/')
            size = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    })
    $runtimeConfig = [IO.File]::ReadAllText((Join-Path $payload 'CorgiBlackboard.App.runtimeconfig.json')) | ConvertFrom-Json
    $lockfiles = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*packages.lock.json' -Recurse | Sort-Object FullName | ForEach-Object {
        [ordered]@{ path = $_.FullName.Substring($root.Length + 1).Replace('\', '/')
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    $manifest = [ordered]@{
        schemaVersion = 1; version = $Version; channel = $ReleaseChannel; commit = $commit
        informationalBuildVersion = "$Version+$commit"
        sdk = $sdk; runtimeIdentifier = 'win-x64'; selfContained = $true
        publishTrimmed = $false; publishSingleFile = $false; exeSha256 = $exeHash
        dependencies = @($runtimeConfig.runtimeOptions.includedFrameworks); lockfiles = $lockfiles
        runtimeComponents = $runtimeComponents
        tests = [ordered]@{ publishExit = $publishExit; staticPackage = 'pending'; diagnosticsRuntime = 'pending'
            gui = 'pending'; offlineInstall = 'pending'; classroom = 'pending'; windows10 = 'pending'; windows11 = 'pending' }
        installer = [ordered]@{ status = 'built'; name = "CorgiBlackboard-$Version-win-x64-setup.exe"
            compilerVersion = '6.7.4'; compilerSha256 = $compilerHash; execution = 'not-run' }
        managedPayload = [ordered]@{
            appSha256 = (Get-FileHash (Join-Path $payload 'CorgiBlackboard.App.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
            infrastructureSha256 = (Get-FileHash (Join-Path $payload 'CorgiBlackboard.Infrastructure.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        packageIntegrity = [ordered]@{ status = 'requires-validation'; receipt = 'external' }
        releaseReadiness = $false; files = $files
    }
    $manifestPath = Join-Path $payload 'release-manifest.json'
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 12))
    $payloadSums = @(Get-ChildItem -LiteralPath $payload -File -Recurse | Sort-Object FullName | ForEach-Object {
        (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + "  " +
            $_.FullName.Substring($payload.Length + 1).Replace('\', '/')
    })
    [IO.File]::WriteAllLines((Join-Path $payload 'SHA256SUMS'), $payloadSums)
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zipName = "CorgiBlackboard-$Version-win-x64.zip"
    $zipPath = Join-Path $ArtifactDir $zipName
    $zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in (Get-ChildItem -LiteralPath $payload -File -Recurse | Sort-Object FullName)) {
            $name = $file.FullName.Substring($payload.Length + 1).Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $name) | Out-Null
        }
    } finally { $zip.Dispose() }
    & $compiler.FullName "/DPackageRoot=$payload" "/DArtifactDir=$ArtifactDir" "/DPackageVersion=$Version" `
        (Join-Path $PSScriptRoot 'installer.iss') 2>&1 | Tee-Object -FilePath (Join-Path $work 'compiler.log')
    if ($LASTEXITCODE -ne 0) { throw "Installer compilation exited $LASTEXITCODE." }
    foreach ($name in @('TEST-CHECKLIST.md', 'RELEASE-NOTES.md', 'THIRD-PARTY-NOTICES.md', 'feedback-guide.md', 'release-manifest.json')) {
        Copy-Item -LiteralPath (Join-Path $payload $name) -Destination $ArtifactDir
    }
    $assets = @($zipName, $manifest.installer.name, 'TEST-CHECKLIST.md', 'RELEASE-NOTES.md', 'THIRD-PARTY-NOTICES.md', 'feedback-guide.md', 'release-manifest.json')
    $sums = @($assets | Sort-Object | ForEach-Object {
        (Get-FileHash -LiteralPath (Join-Path $ArtifactDir $_) -Algorithm SHA256).Hash.ToLowerInvariant() + "  $_"
    })
    [IO.File]::WriteAllLines((Join-Path $ArtifactDir 'SHA256SUMS'), $sums)
    Write-Output "BUILD_WORK=$work"
    Write-Output "ARTIFACTS_CREATED=$ArtifactDir"
    Write-Output 'RELEASE_READINESS=false; GUI/offline installation/runtime evidence pending.'
} finally {
    $env:GIT_MASTER = $previousGitMaster
    Pop-Location
}
