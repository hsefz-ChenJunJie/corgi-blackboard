param(
    [ValidateSet('Red', 'Tests', 'Build')][string]$Mode = 'Tests',
    [string]$TestFilter = '',
    [string]$Receipt = ''
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Set-Location $root
$env:DOTNET_ROOT = 'E:/corgi-blackboard-tools/dotnet10'
$env:PATH = "$env:DOTNET_ROOT;C:/Program Files/Git/cmd;$env:PATH"
$env:GIT_MASTER = '1'
$sdk = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
$evidence = Join-Path $root '.omo/evidence/phase-one/task-10/attempt3'
if ($Receipt -eq '') { $Receipt = $Mode }
[IO.Directory]::CreateDirectory($evidence) | Out-Null
if ((Test-Path (Join-Path $evidence "$Receipt.log")) -or (Test-Path (Join-Path $evidence "$Receipt.trx"))) {
    throw "Receipt already exists. Supply a new -Receipt name; existing evidence is immutable."
}
$ErrorActionPreference = 'Continue'
if ($Mode -eq 'Build') {
    & $sdk build CorgiBlackboard.sln -c Release --no-restore -warnaserror 2>&1 |
        Tee-Object -FilePath (Join-Path $evidence "$Receipt.log")
} else {
    $filter = if ($TestFilter -ne '') { $TestFilter } elseif ($Mode -eq 'Red') { 'FullyQualifiedName~FeedbackBoundaryTests' } else { 'FullyQualifiedName~Diagnostic|FullyQualifiedName~Feedback' }
    & $sdk test tests/CorgiBlackboard.Infrastructure.Tests -c Release --no-restore --filter $filter --results-directory $evidence --logger "trx;LogFileName=$Receipt.trx" 2>&1 |
        Tee-Object -FilePath (Join-Path $evidence "$Receipt.log")
}
$code = $LASTEXITCODE
"TASK10_ATTEMPT3_${Mode}_EXIT=$code" | Tee-Object -FilePath (Join-Path $evidence "$Receipt.log") -Append
exit $code
