[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArtifactDir,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string]$ExpectedCommit,
    [string]$ReceiptPath,
    [switch]$RequirePublication
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $PSBoundParameters.ContainsKey('ReceiptPath')) {
    $ReceiptPath = Join-Path $PSScriptRoot 'readiness.json'
}
$reason = 'local-readiness'
try {
    $receipt = Get-Content -LiteralPath $ReceiptPath -Raw | ConvertFrom-Json
    if ($receipt.schemaVersion -ne 2 -or $receipt.channel -cne 'Test' -or
        $receipt.commit -cne $ExpectedCommit -or $receipt.version -cnotmatch '^0\.1\.0-test\.[1-9][0-9]*$') {
        throw 'Receipt must be schema 2 and bound to this exact Test source.'
    }
    if ($receipt.packageIntegrity.status -cne 'passed' -or
        $receipt.packageIntegrity.packageValid -ne $true -or
        $receipt.packageIntegrity.commit -cne $ExpectedCommit -or
        $receipt.packageIntegrity.version -cne $receipt.version -or
        $receipt.packageIntegrity.channel -cne 'Test') {
        throw 'A successful external package-integrity receipt is required.'
    }
    $reason = 'manifest'
    $directory = (Resolve-Path -LiteralPath $ArtifactDir).ProviderPath
    $manifestPath = Join-Path $directory 'release-manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.commit -cne $ExpectedCommit -or $manifest.version -cne $receipt.version -or
        $manifest.channel -cne 'Test' -or $manifest.sdk -cne '10.0.402' -or
        $manifest.installer.status -cne 'built') {
        throw 'Package manifest does not describe a valid Test package.'
    }
    $reason = 'sha256'
    $required = @("CorgiBlackboard-$($receipt.version)-win-x64.zip",
        "CorgiBlackboard-$($receipt.version)-win-x64-setup.exe",
        "CorgiBlackboard-$($receipt.version)-win-x64-tests.zip",
        'test-manifest.json',
        'TEST-CHECKLIST.md', 'RELEASE-NOTES.md', 'THIRD-PARTY-NOTICES.md',
        'feedback-guide.md', 'release-manifest.json')
    $sums = @{}
    foreach ($line in [IO.File]::ReadAllLines((Join-Path $directory 'SHA256SUMS'))) {
        if ($line -cnotmatch '^([a-f0-9]{64})  ([A-Za-z0-9._-]+)$' -or $sums.ContainsKey($Matches[2])) {
            throw 'Invalid or duplicate asset checksum'
        }
        $sums.Add($Matches[2], $Matches[1])
    }
    if ($sums.Count -ne $required.Count) { throw 'Checksum asset set differs' }
    foreach ($name in $required) {
        if (-not $sums.ContainsKey($name) -or
            (Get-FileHash -LiteralPath (Join-Path $directory $name) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $sums[$name]) {
            throw "Missing or changed release asset: $name"
        }
    }
    foreach ($name in @('manifestSha256', 'zipSha256', 'installerSha256', 'testZipSha256', 'testManifestSha256')) {
        $asset = switch ($name) {
            'manifestSha256' { 'release-manifest.json' }
            'zipSha256' { $required[0] }
            'installerSha256' { $required[1] }
            'testZipSha256' { $required[2] }
            'testManifestSha256' { 'test-manifest.json' }
            default { throw 'Unsupported package identity' }
        }
        if ($receipt.packageIntegrity.$name -cne $sums[$asset]) {
            throw "Package-integrity receipt does not bind $asset"
        }
    }
    $actual = @(Get-ChildItem -LiteralPath $directory -Force)
    if ($actual.Count -ne ($required.Count + 1)) { throw 'Unexpected release assets' }
    foreach ($item in $actual) {
        if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
            $item.Name -cnotin ($required + @('SHA256SUMS'))) { throw 'Forbidden release asset' }
    }
    $reason = 'local-evidence'
    $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
    $evidenceRoot = $repoRoot
    if ($RequirePublication) {
        if ($receipt.publicationReadiness.status -cne 'passed') {
            throw 'Explicit Test publication authorization is required for the public write.'
        }
        $binding = $receipt.publicationReadiness.evidence.authorization
        if ($binding.path -notmatch '^\.omo/evidence/[A-Za-z0-9/_-]+\.json$' -or
            $binding.sha256 -notmatch '^[a-f0-9]{64}$') { throw 'Invalid evidence binding: authorization' }
        $path = Join-Path $evidenceRoot $binding.path
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $binding.sha256) {
            throw 'Changed evidence: authorization'
        }
        $record = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        if ($record.commit -cne $ExpectedCommit -or $record.version -cne $receipt.version -or
            $record.channel -cne 'Test' -or $record.manifestSha256 -cne $sums['release-manifest.json'] -or
            $record.exeSha256 -cne $manifest.exeSha256 -or
            $record.zipSha256 -cne $sums[$required[0]] -or
            $record.installerSha256 -cne $sums[$required[1]] -or
            $record.testZipSha256 -cne $sums[$required[2]] -or
            $record.testManifestSha256 -cne $sums['test-manifest.json']) {
            throw 'Evidence identity mismatch: authorization'
        }
        if ($record.authorized -isnot [bool] -or $record.authorized -ne $true -or
            [string]::IsNullOrWhiteSpace($record.userInstruction)) { throw 'Exact user publication authorization required' }
    }
    [ordered]@{ packageIntegrity = $true; runtimeReadiness = [bool]($receipt.runtimeReadiness.status -eq 'passed'); publicationReadiness = [bool]($receipt.publicationReadiness.status -eq 'passed') } | ConvertTo-Json
    exit 0
} catch {
    [ordered]@{ releaseReadiness = $false; reason = $reason; error = $_.Exception.Message } | ConvertTo-Json
    exit 1
}
