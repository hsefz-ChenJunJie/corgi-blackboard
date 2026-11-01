[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('ShellLayout','BoardInkErase','TouchCancel','ImageWindows','ImageFitRestore','DiagnosticsExport','HarnessFailure','ReleaseSmoke')][string]$Scenario,
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$EvidenceDir,
    [Parameter(Mandatory)][ValidatePattern('^(?:[a-fA-F0-9]{40}|[a-fA-F0-9]{64})$')][string]$ExpectedSourceCommit,
    [string]$DotnetPath = 'dotnet',
    [string]$DriverPath = '',
    [switch]$PostReleaseDesktop
)
$ErrorActionPreference = 'Stop'
# Never probe for a desktop: explicit release-environment authorization precedes all native work.
if (-not $PostReleaseDesktop) { throw 'Desktop QA: 发布后实际环境待测. Supply -PostReleaseDesktop only on the actual release desktop.' }
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $DriverPath) { $DriverPath = Join-Path $root 'tests/CorgiBlackboard.UiTests/bin/Release/net10.0-windows/CorgiBlackboard.UiTests.dll' }
if (-not (Test-Path -LiteralPath $DriverPath -PathType Leaf)) { throw 'Compile the QA fixture first. This entry point never builds or installs GUI components.' }
if (-not (Test-Path -LiteralPath $ExePath -PathType Leaf)) { throw 'ExePath must identify the actual unpacked/published executable.' }
if (Test-Path -LiteralPath $EvidenceDir) { throw 'EvidenceDir must be new; stale evidence cannot satisfy this run.' }
& $DotnetPath $DriverPath '--post-release-desktop' $Scenario ([IO.Path]::GetFullPath($ExePath)) ([IO.Path]::GetFullPath($EvidenceDir)) $ExpectedSourceCommit
$driverExit = $LASTEXITCODE
$resultPath = Join-Path $EvidenceDir 'result.json'
$cleanupPath = Join-Path $EvidenceDir 'cleanup.json'
if (-not (Test-Path -LiteralPath $resultPath) -or -not (Test-Path -LiteralPath $cleanupPath)) { throw "Driver exit $driverExit lacks result/cleanup evidence." }
$result = Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8 | ConvertFrom-Json
$cleanup = Get-Content -LiteralPath $cleanupPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($result.schemaVersion -ne 1 -or $result.scenario -ne $Scenario -or
    $result.expectedSourceCommit -ne $ExpectedSourceCommit -or
    $result.exeSha256 -ne (Get-FileHash -LiteralPath $ExePath -Algorithm SHA256).Hash) { throw 'Evidence identity mismatch.' }
if ($Scenario -eq 'HarnessFailure') {
    $observedFailure = $driverExit -ne 0 -and -not $result.pass -and -not $result.productPass -and
        (($result.errors -join "`n") -match 'Missing AutomationId=IntentionallyMissingControl') -and
        $result.cleanupPassed -and $cleanup.exited -and $cleanup.releasedContacts -and $cleanup.temporaryRemoved -and
        (Test-Path -LiteralPath (Join-Path $EvidenceDir 'screen.png')) -and
        (Test-Path -LiteralPath (Join-Path $EvidenceDir 'uia.json'))
    [ordered]@{ schemaVersion = 1; harnessSelfTestPassed = [bool]$observedFailure; observedDriverExitCode = $driverExit;
        productPass = $false; exeSha256 = $result.exeSha256; expectedSourceCommit = $ExpectedSourceCommit } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $EvidenceDir 'harness-self-test.json') -Encoding utf8
    if (-not $observedFailure) { throw 'HarnessFailure did not produce the intentional failure plus complete capture and cleanup.' }
    exit 0
}
if ($driverExit -ne 0 -or -not $result.pass -or -not $result.productPass -or -not $result.cleanupPassed) {
    throw "Desktop scenario failed (driver exit $driverExit). Inspect result.json; no replay fallback exists."
}
exit 0
