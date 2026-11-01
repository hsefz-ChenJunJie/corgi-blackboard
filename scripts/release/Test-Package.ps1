[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArtifactDir,
    [switch]$StaticOnly,
    [switch]$RequireReleaseReady,
    [string]$ReadinessReceipt
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Assert-PackageIdentity.ps1')
$failure = 'manifest-load'
try {
    $directory = (Resolve-Path -LiteralPath $ArtifactDir).ProviderPath
    $manifestPath = Join-Path $directory 'release-manifest.json'
    $manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
    $failure = 'metadata'
    if ($manifest.schemaVersion -ne 1 -or $manifest.channel -cne 'Test' -or
        $manifest.version -notmatch '^0\.1\.0-test\.[1-9][0-9]*$' -or
        $manifest.commit -notmatch '^[a-f0-9]{40}$' -or $manifest.sdk -cne '10.0.402' -or
        $manifest.runtimeIdentifier -cne 'win-x64' -or $manifest.selfContained -ne $true -or
        $manifest.publishTrimmed -ne $false -or $manifest.publishSingleFile -ne $false) {
        throw 'Invalid package version, Test channel, commit, SDK or publish settings.'
    }
    if ($manifest.releaseReadiness -isnot [bool] -or
        $manifest.informationalBuildVersion -cne "$($manifest.version)+$($manifest.commit)") {
        throw 'Release and informational build identities differ.'
    }
    $failure = 'runtime-provenance'
    $expectedRuntimeComponents = @(& (Join-Path $PSScriptRoot 'Assert-RuntimeComponents.ps1') -Manifest $manifest)
    $zipName = "CorgiBlackboard-$($manifest.version)-win-x64.zip"
    $setupName = "CorgiBlackboard-$($manifest.version)-win-x64-setup.exe"
    if ($manifest.installer.status -cne 'built' -or $manifest.installer.name -cne $setupName) {
        throw 'Compiled installer metadata required.'
    }
    $documents = @('TEST-CHECKLIST.md', 'RELEASE-NOTES.md', 'THIRD-PARTY-NOTICES.md', 'feedback-guide.md')
    $testZipName = "CorgiBlackboard-$($manifest.version)-win-x64-tests.zip"
    $assets = @($zipName, $setupName, $testZipName, 'test-manifest.json', 'release-manifest.json') + $documents
    $failure = 'artifact-allowlist'
    foreach ($item in (Get-ChildItem -LiteralPath $directory -Force)) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Artifact reparse point: $($item.Name)" }
        if ($item.PSIsContainer) {
            if ($item.Name -cnotin @('payload', 'offline-feed')) { throw "Unexpected artifact directory: $($item.Name)" }
        } elseif ($item.Name -cnotin ($assets + @('SHA256SUMS', 'publish.log', 'restore.log'))) {
            throw "Unexpected artifact file: $($item.Name)"
        }
    }
    $required = $documents + @('CorgiBlackboard.App.exe', 'CorgiBlackboard.App.dll',
        'CorgiBlackboard.Core.dll', 'CorgiBlackboard.Infrastructure.dll',
        'CorgiBlackboard.App.deps.json', 'CorgiBlackboard.App.runtimeconfig.json',
        'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll',
        'PresentationFramework.dll', 'PresentationCore.dll', 'WindowsBase.dll',
        'DOTNET-RUNTIME-LICENSE.txt', 'DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt',
        'WINDOWS-DESKTOP-RUNTIME-LICENSE.txt')
    $inventory = @{}
    $failure = 'invalid-payload'
    foreach ($file in $manifest.files) {
        $name = [string]$file.path
        # SDK publish files and localized framework resources only; no arbitrary source/scripts.
        $allowed = $documents -ccontains $name -or
            $name -cin @('DOTNET-RUNTIME-LICENSE.txt', 'DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt', 'WINDOWS-DESKTOP-RUNTIME-LICENSE.txt') -or
            $name -cmatch '^CorgiBlackboard\.(App|Core|Infrastructure)\.dll$' -or
            $name -ceq 'CorgiBlackboard.App.exe' -or
            $name -ceq 'createdump.exe' -or
            $name -cmatch '^CorgiBlackboard\.App\.(deps|runtimeconfig)\.json$' -or
            $name -cmatch '^(cs|de|es|fr|it|ja|ko|pl|pt-BR|ru|tr|zh-Hans|zh-Hant)/(Microsoft|Presentation|ReachFramework|System|UIAutomation|Windows)[A-Za-z0-9._-]*\.resources\.dll$' -or
            $name -cmatch '^(System|Microsoft|Presentation|Windows|Accessibility|DirectWriteForwarder|PenImc|ReachFramework|UIAutomation)[A-Za-z0-9._-]*\.dll$' -or
            $name -cmatch '^(coreclr|clrjit|clrgc|clrgcexp|clretwrc|dbgshim|hostfxr|hostpolicy|mscordaccore(_amd64_amd64_[0-9.]+)?|mscordbi|mscorrc|mscorrc\.debug|msvcp140_cor3|vcruntime140(_1)?_cor3|wpfgfx_cor3|D3DCompiler_47_cor3|msquic|netstandard|mscorlib)\.dll$'
        if (-not $allowed -or $name -match '(?i)(token|reference|private.?key|id_rsa|id_ed25519|\.pfx|\.pem|\.key)' -or
            $file.sha256 -cnotmatch '^[a-f0-9]{64}$' -or [long]$file.size -lt 0 -or $inventory.ContainsKey($name)) {
            throw "Forbidden or invalid payload entry: $name"
        }
        $inventory.Add($name, $file)
    }
    $failure = 'required-files'
    foreach ($name in $required) {
        if (-not $inventory.ContainsKey($name)) { throw "Missing runtime or document: $name" }
    }
    foreach ($expected in $expectedRuntimeComponents) {
        foreach ($expectedFile in $expected.files) {
            $payloadFile = $inventory[$expectedFile.destination]
            if ([long]$payloadFile.size -ne $expectedFile.size -or
                $payloadFile.sha256 -cne $expectedFile.sha256) {
                throw "Runtime notice payload differs: $($expectedFile.destination)"
            }
        }
    }
    $sums = @{}
    $failure = 'asset-hashes'
    foreach ($line in [IO.File]::ReadAllLines((Join-Path $directory 'SHA256SUMS'))) {
        if ($line -notmatch '^([a-f0-9]{64})  ([^/\\]+)$' -or $sums.ContainsKey($Matches[2])) {
            throw 'Invalid or duplicate asset checksum.'
        }
        $sums.Add($Matches[2], $Matches[1])
    }
    if ($sums.Count -ne $assets.Count) { throw 'Asset checksum set differs from the package contract.' }
    $rootTestManifest = Get-Content -LiteralPath (Join-Path $directory 'test-manifest.json') -Raw | ConvertFrom-Json
    if ($rootTestManifest.packageType -cne 'offline-gui-test-driver' -or
        $rootTestManifest.version -cne $manifest.version -or $rootTestManifest.commit -cne $manifest.commit -or
        $rootTestManifest.driverSourceCommit -cne $manifest.commit -or
        $rootTestManifest.appExeSha256 -cne $manifest.exeSha256) {
        throw 'Root test manifest identity differs from application manifest.'
    }
    foreach ($name in $assets) {
        if (-not $sums.ContainsKey($name) -or
            (Get-FileHash -LiteralPath (Join-Path $directory $name) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $sums[$name]) {
            throw "Asset hash mismatch: $name"
        }
    }
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $failure = 'archive-content'
    $zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $directory $zipName))
    try {
        $seen = @{}
        $innerSums = @{}
        $innerSumsHash = $null
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            if ($seen.ContainsKey($name) -or ($name -cne 'release-manifest.json' -and $name -cne 'SHA256SUMS' -and -not $inventory.ContainsKey($name))) {
                throw "Unexpected or duplicate ZIP entry: $name"
            }
            $seen.Add($name, $true)
            $stream = $entry.Open()
            try {
                $sha = [Security.Cryptography.SHA256]::Create()
                try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
                finally { $sha.Dispose() }
            } finally { $stream.Dispose() }
            if ($name -ceq 'release-manifest.json') {
                if ($hash -cne $sums[$name]) { throw 'ZIP manifest differs from the external manifest.' }
            } elseif ($name -cne 'SHA256SUMS' -and
                ($hash -cne $inventory[$name].sha256 -or $entry.Length -ne $inventory[$name].size)) {
                throw "Payload hash or size mismatch: $name"
            }
            if ($documents -ccontains $name -and $hash -cne $sums[$name]) { throw "Document copy differs: $name" }
            # Scan bytes as text without executing or extracting application assemblies.
            $stream = $entry.Open()
            $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8, $true)
            try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
            if ($name -ceq 'SHA256SUMS') {
                $innerSumsHash = $hash
                foreach ($line in ($text -split '\r?\n' | Where-Object { $_ })) {
                    if ($line -notmatch '^([a-f0-9]{64})  ([A-Za-z0-9._/-]+)$' -or $innerSums.ContainsKey($Matches[2])) {
                        throw 'Invalid ZIP checksum list.'
                    }
                    $innerSums.Add($Matches[2], $Matches[1])
                }
            }
            if ($text -match '-----BEGIN ([A-Z0-9 ]+ )?PRIVATE KEY-----|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}') {
                throw "Credential material detected in: $name"
            }
            if ($name -ceq 'CorgiBlackboard.App.runtimeconfig.json') {
                $runtime = ($text | ConvertFrom-Json).runtimeOptions
                $frameworks = @($runtime.includedFrameworks | ForEach-Object name)
                if ($runtime.tfm -cne 'net10.0' -or $frameworks -cnotcontains 'Microsoft.NETCore.App' -or
                    $frameworks -cnotcontains 'Microsoft.WindowsDesktop.App') { throw 'Runtime is not self-contained .NET 10 WPF.' }
                foreach ($dependency in $runtime.includedFrameworks) {
                    $declared = @($manifest.dependencies | Where-Object { $_.name -ceq $dependency.name -and $_.version -ceq $dependency.version })
                    if ($declared.Count -ne 1) { throw 'Runtime dependency version differs from manifest.' }
                }
            }
            if ($name -ceq 'CorgiBlackboard.Infrastructure.dll') {
                $stream = $entry.Open()
                $memory = [IO.MemoryStream]::new()
                try { $stream.CopyTo($memory); Assert-PackageIdentity -AssemblyBytes $memory.ToArray() -Identity $manifest }
                finally { $memory.Dispose(); $stream.Dispose() }
            }
            if ($name -ceq 'CorgiBlackboard.App.dll') {
                $stream = $entry.Open()
                $memory = [IO.MemoryStream]::new()
                try {
                    $stream.CopyTo($memory)
                    $assemblyText = [Text.Encoding]::UTF8.GetString($memory.ToArray())
                    if (-not $assemblyText.Contains($manifest.version)) { throw 'Managed application version differs.' }
                } finally { $memory.Dispose(); $stream.Dispose() }
            }
            if ($name -ceq 'TEST-CHECKLIST.md' -and
                (-not $text.Contains($manifest.version) -or -not $text.Contains($manifest.commit) -or
                 -not $text.Contains($manifest.exeSha256) -or $text.Contains('<PENDING-PACKAGE-BINDING>') -or
                 [regex]::Matches($text, '(?m)^\| S[0-9]{2} \|').Count -ne 19)) {
                throw 'Checklist lacks measured package identity.'
            }
        }
        if ($seen.Count -ne $inventory.Count + 2 -or $innerSums.Count -ne $inventory.Count + 1) {
            throw 'ZIP, checksum and manifest inventories differ.'
        }
        foreach ($name in $inventory.Keys) {
            if ($innerSums[$name] -cne $inventory[$name].sha256) { throw "ZIP checksum differs: $name" }
        }
        if ($innerSums['release-manifest.json'] -cne $sums['release-manifest.json']) { throw 'ZIP manifest checksum differs.' }
        if ($manifest.exeSha256 -cne $inventory['CorgiBlackboard.App.exe'].sha256) { throw 'Executable hash differs from manifest identity.' }
        if ($manifest.managedPayload.appSha256 -cne $inventory['CorgiBlackboard.App.dll'].sha256 -or
            $manifest.managedPayload.infrastructureSha256 -cne $inventory['CorgiBlackboard.Infrastructure.dll'].sha256) {
            throw 'Managed payload bindings differ.'
        }
    } finally { $zip.Dispose() }
    $testZip = [IO.Compression.ZipFile]::OpenRead((Join-Path $directory $testZipName))
    try {
        $testEntries = @{}
        foreach ($entry in $testZip.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            if ($entry.Length -eq 0 -and $name -notmatch '/$') { throw "Empty test ZIP entry: $name" }
            if ($testEntries.ContainsKey($name)) { throw "Duplicate test ZIP entry: $name" }
            $testEntries.Add($name, $entry)
        }
        foreach ($requiredTestFile in @('release-manifest.json', 'test-manifest.json', 'SHA256SUMS',
                'Run-Scenario.ps1', 'Run-All.ps1', 'CorgiBlackboard.UiTests.exe', 'README-test-package.md')) {
            if (-not $testEntries.ContainsKey($requiredTestFile)) { throw "Test ZIP missing: $requiredTestFile" }
        }
        $testManifestEntry = $testEntries['test-manifest.json']
        $reader = [IO.StreamReader]::new($testManifestEntry.Open(), [Text.Encoding]::UTF8, $true)
        try { $testManifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        if ($testManifest.packageType -cne 'offline-gui-test-driver' -or
            $testManifest.version -cne $manifest.version -or $testManifest.channel -cne 'Test' -or
            $testManifest.commit -cne $manifest.commit -or $testManifest.appExeSha256 -cne $manifest.exeSha256 -or
            $testManifest.guiExecution -cne 'untested' -or @($testManifest.scenarios).Count -ne 8) {
            throw 'Test ZIP identity or scenario manifest differs.'
        }
        $testSums = @{}
        $sumReader = [IO.StreamReader]::new($testEntries['SHA256SUMS'].Open(), [Text.Encoding]::UTF8, $true)
        try { foreach ($line in ($sumReader.ReadToEnd() -split '\r?\n' | Where-Object { $_ })) {
            if ($line -notmatch '^([a-f0-9]{64})  ([A-Za-z0-9._/-]+)$' -or $testSums.ContainsKey($Matches[2])) {
                throw 'Invalid test ZIP checksum list.'
            }
            $testSums.Add($Matches[2], $Matches[1])
        }} finally { $sumReader.Dispose() }
        foreach ($name in $testSums.Keys) {
            if (-not $testEntries.ContainsKey($name)) { throw "Test checksum entry missing: $name" }
            $stream = $testEntries[$name].Open()
            try { $sha = [Security.Cryptography.SHA256]::Create(); try {
                $actual = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant()
            } finally { $sha.Dispose() } } finally { $stream.Dispose() }
            if ($actual -cne $testSums[$name]) { throw "Test ZIP hash mismatch: $name" }
        }
    } finally { $testZip.Dispose() }
    # Installer compilation consumes this directory, so validate it as well when present.
    $payload = Join-Path $directory 'payload'
    if (Test-Path -LiteralPath $payload) {
        $payloadFiles = @(Get-ChildItem -LiteralPath $payload -Recurse -File)
        if ($payloadFiles.Count -ne $inventory.Count + 2) { throw 'Installer payload inventory differs.' }
        foreach ($file in $payloadFiles) {
            if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Installer payload contains a reparse point.' }
            $name = $file.FullName.Substring($payload.Length + 1).Replace('\', '/')
            $expected = $innerSums[$name]
            if ($name -ceq 'SHA256SUMS') { $expected = $innerSumsHash }
            if (-not $expected -or (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expected) {
                throw "Installer payload hash differs: $name"
            }
        }
    }
    $failure = 'release-readiness'
    if ($RequireReleaseReady) {
        if (-not $ReadinessReceipt) { throw 'Actual hash-bound runtime evidence receipt required.' }
        $receipt = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $ReadinessReceipt).ProviderPath) | ConvertFrom-Json
        if ($receipt.releaseReadiness -ne $true -or $receipt.commit -cne $manifest.commit -or
            $receipt.version -cne $manifest.version -or $receipt.channel -cne 'Test') { throw 'Readiness identity differs.' }
        $root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
        foreach ($kind in @('desktop', 'offlineInstall', 'authorization')) {
            $binding = $receipt.evidence.$kind
            if ($binding.path -cnotmatch '^\.omo/evidence/[A-Za-z0-9/_-]+\.json$' -or
                $binding.sha256 -cnotmatch '^[a-f0-9]{64}$') { throw "Invalid evidence binding: $kind" }
            $path = Join-Path $root $binding.path
            if ((Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $binding.sha256) { throw "Changed evidence: $kind" }
            $record = [IO.File]::ReadAllText($path) | ConvertFrom-Json
            if ($record.commit -cne $manifest.commit -or $record.version -cne $manifest.version -or
                $record.channel -cne 'Test' -or $record.manifestSha256 -cne $sums['release-manifest.json'] -or
                $record.exeSha256 -cne $manifest.exeSha256 -or $record.zipSha256 -cne $sums[$zipName] -or
                $record.installerSha256 -cne $sums[$setupName]) { throw "Evidence identity differs: $kind" }
            switch ($kind) {
                'desktop' {
                    if ($record.status -cne 'passed' -or $record.diagnosticsRuntime -cne 'passed') { throw 'Desktop runtime evidence pending.' }
                }
                'offlineInstall' {
                    if ($record.status -cne 'passed' -or $record.networkDisconnected -ne $true -or
                        $record.standardUser -ne $true -or $record.runtimePreinstalled -ne $false -or
                        $record.chinesePathWithSpaces -ne $true -or $record.uninstallPreservedLogs -ne $true) { throw 'Offline installation evidence pending.' }
                }
                'authorization' {
                    if ($record.authorized -ne $true -or [string]::IsNullOrWhiteSpace($record.userInstruction)) { throw 'Publication authorization absent.' }
                }
                default { throw 'Unsupported evidence kind.' }
            }
        }
    }
    [ordered]@{ packageValid = $true; packageIntegrity = $true; releaseReadiness = [bool]$RequireReleaseReady
        readinessChecked = [bool]$RequireReleaseReady; mode = 'file-integrity'
        version = $manifest.version; commit = $manifest.commit
        gui = 'pending'; offlineInstall = 'pending'; installer = $manifest.installer.status } | ConvertTo-Json
    exit 0
} catch {
    [ordered]@{ packageValid = $false; releaseReadiness = $false; reason = $failure; error = $_.Exception.Message } | ConvertTo-Json
    exit 1
}
