[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [ValidateSet('Test','Stable')][string]$ReleaseChannel = 'Test',
    [string]$DotnetPath = 'dotnet',
    [switch]$PostReleaseDesktop,
    [string]$ExePath,
    [string]$ExpectedSourceCommit,
    [string]$EvidenceRoot = '.omo/evidence/phase-one/verify'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    function Invoke-Dotnet([string[]]$Arguments) {
        & $DotnetPath @Arguments
        if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments -join ' ') exited $LASTEXITCODE" }
    }
    function Invoke-Tests([string]$Project, [string]$Name, [string]$Filter = '', [switch]$Build) {
        $results = Join-Path ([IO.Path]::GetFullPath($EvidenceRoot)) ("non-desktop/" + $Name + '-' + [Guid]::NewGuid().ToString('N'))
        $arguments = @('test', $Project, '-c', $Configuration, '--logger', 'trx;LogFileName=tests.trx', '--results-directory', $results)
        if (-not $Build) { $arguments += '--no-build' }
        if ($Filter) { $arguments += @('--filter', $Filter) }
        Invoke-Dotnet -Arguments $arguments
        [xml]$trx = Get-Content -LiteralPath (Join-Path $results 'tests.trx') -Raw -Encoding UTF8
        $counters = $trx.TestRun.ResultSummary.Counters
        if ([int]$counters.total -le 0 -or [int]$counters.passed -ne [int]$counters.total) {
            throw "No tests or non-passing tests in $Name; this is not a verification pass."
        }
    }
    Invoke-Dotnet -Arguments @('restore','CorgiBlackboard.sln','--locked-mode')
    Invoke-Dotnet -Arguments @('build','CorgiBlackboard.sln','--no-restore','-c',$Configuration,'-warnaserror',"-p:ReleaseChannel=$ReleaseChannel",'-m:1')
    # Default execution excludes fixtures whose runtime identity queries desktop DPI,
    # but includes the file/ZIP/retention/queue/recovery diagnostics gate.
    Invoke-Tests 'tests/CorgiBlackboard.Core.Tests' 'core'
    Invoke-Tests 'tests/CorgiBlackboard.UiTests/PureHarnessTests' 'qa-contracts' -Build
    Invoke-Tests 'tests/CorgiBlackboard.Infrastructure.Tests' 'diagnostics' `
        'FullyQualifiedName~DiagnosticSessionTests|FullyQualifiedName~FeedbackBoundaryTests|FullyQualifiedName~FeedbackConcurrencyTests|FullyQualifiedName~DiagnosticLifecycleTests|FullyQualifiedName~WorkspaceDiagnosticsTests'
    if ($PostReleaseDesktop) {
        if ($ReleaseChannel -ne 'Test' -or -not $ExePath -or $ExpectedSourceCommit -notmatch '^[a-fA-F0-9]{40}$') {
            throw 'Post-release desktop validation requires Test build, ExePath and ExpectedSourceCommit.'
        }
        Invoke-Tests 'tests/CorgiBlackboard.Infrastructure.Tests' 'files' `
            'FullyQualifiedName~DiagnosticSessionTests|FullyQualifiedName~FeedbackBoundaryTests|FullyQualifiedName~FeedbackConcurrencyTests'
        foreach ($scenario in @('ShellLayout','BoardInkErase','TouchCancel','ImageWindows','ImageFitRestore','DiagnosticsExport','HarnessFailure','ReleaseSmoke')) {
            # A separate PowerShell process preserves the driver's exit code without exiting this verifier.
            & (Get-Process -Id $PID).Path -NoProfile -File (Join-Path $PSScriptRoot 'qa/Invoke-DesktopQa.ps1') -Scenario $scenario -ExePath $ExePath `
                -ExpectedSourceCommit $ExpectedSourceCommit -EvidenceDir (Join-Path $EvidenceRoot $scenario) `
                -DotnetPath $DotnetPath -PostReleaseDesktop
            if ($LASTEXITCODE -ne 0) { throw "Desktop scenario failed: $scenario" }
        }
    } else {
        Write-Output 'Desktop scenarios, UIA, touch, DPI and installation: 发布后实际环境待测. Non-desktop diagnostics checks do not establish desktop PASS.'
    }
} finally { Pop-Location }
