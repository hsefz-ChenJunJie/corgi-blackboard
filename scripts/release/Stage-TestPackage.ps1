[CmdletBinding()]
param(
    [ValidatePattern('^0\.1\.0-test\.[1-9][0-9]*$')][string]$Version = '0.1.0-test.1',
    [Parameter(Mandatory)][string]$ArtifactDir,
    [Parameter(Mandatory)][string]$DotnetPath,
    [Parameter(Mandatory)][string]$CompilerPath,
    [Parameter(Mandatory)][string]$CompilerProvenancePath,
    [string]$RuntimePackageRoot = $env:NUGET_PACKAGES
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not [IO.Path]::IsPathRooted($ArtifactDir)) { $ArtifactDir = Join-Path $root $ArtifactDir }
$ArtifactDir = [IO.Path]::GetFullPath($ArtifactDir)
$previousGitMaster = $env:GIT_MASTER
$env:GIT_MASTER = '1'
$build = Join-Path $PSScriptRoot 'Build-Package.ps1'
try {
    & pwsh -NoProfile -File $build -Version $Version -ReleaseChannel Test -ArtifactDir $ArtifactDir `
        -DotnetPath $DotnetPath -CompilerPath $CompilerPath -CompilerProvenancePath $CompilerProvenancePath `
        -RuntimePackageRoot $RuntimePackageRoot
    if ($LASTEXITCODE -ne 0) { throw 'Application package build failed.' }
    $testBuild = Join-Path $PSScriptRoot 'Build-TestPackage.ps1'
    if (-not (Test-Path -LiteralPath $testBuild)) { throw 'Build-TestPackage.ps1 is required for staging.' }
    & pwsh -NoProfile -File $testBuild -ArtifactDir $ArtifactDir -DotnetPath $DotnetPath `
        -RuntimePackageRoot $RuntimePackageRoot
    if ($LASTEXITCODE -ne 0) { throw 'Independent test package build failed.' }
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Test-Package.ps1') -ArtifactDir $ArtifactDir -StaticOnly
    if ($LASTEXITCODE -ne 0) { throw 'Static package validation failed.' }
} finally {
    $env:GIT_MASTER = $previousGitMaster
}
$manifest = Get-Content -LiteralPath (Join-Path $ArtifactDir 'release-manifest.json') -Raw | ConvertFrom-Json
$commit = (& git -C $root rev-parse --verify HEAD).Trim()
$assets = @(Get-ChildItem -LiteralPath $ArtifactDir -File | Where-Object Name -ne 'readiness.json')
$sums = @{}
foreach ($line in [IO.File]::ReadAllLines((Join-Path $ArtifactDir 'SHA256SUMS'))) {
    if ($line -match '^([a-f0-9]{64})  (.+)$') { $sums[$Matches[2]] = $Matches[1] }
}
$zipName = "CorgiBlackboard-$Version-win-x64.zip"
$installerName = "CorgiBlackboard-$Version-win-x64-setup.exe"
$testZip = @($assets | Where-Object Name -match 'tests\.zip$' | Select-Object -First 1).Name
if (-not $testZip) { throw 'Independent test ZIP is missing from staged assets.' }
$integrity = [ordered]@{
    status = 'passed'; packageValid = $true; commit = $commit; version = $manifest.version; channel = 'Test'
    receipt = 'staging-run'; manifestSha256 = $sums['release-manifest.json']; zipSha256 = $sums[$zipName]
    installerSha256 = $sums[$installerName]; testZipSha256 = $sums[$testZip]
    testManifestSha256 = $sums['test-manifest.json']
}
$readiness = [ordered]@{
    schemaVersion = 2; commit = $commit; version = $manifest.version; channel = 'Test'
    packageIntegrity = $integrity
    runtimeReadiness = [ordered]@{ status = 'pending'; evidence = [ordered]@{ desktop = $null; offlineInstall = $null } }
    publicationReadiness = [ordered]@{ status = 'pending'; evidence = [ordered]@{ authorization = $null } }
}
[IO.File]::WriteAllText((Join-Path $ArtifactDir 'readiness.json'), ($readiness | ConvertTo-Json -Depth 12))
Write-Output "STAGED_PACKAGE=$ArtifactDir"
