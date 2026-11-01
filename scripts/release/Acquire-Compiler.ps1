[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Destination,
    [string]$ProvenancePath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$expectedCompilerSha256 = 'f02fa0e5f237b47d7ed1edae9b991866ec94fe02af61eb6a73e94f92fe8e0604'
$Destination = [IO.Path]::GetFullPath($Destination)
$ProvenancePath = if ($ProvenancePath) { [IO.Path]::GetFullPath($ProvenancePath) } else { Join-Path $Destination 'compiler-provenance.json' }
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
$downloads = @(
    [ordered]@{ name = 'innosetup-6.7.4.exe'; url = 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_4/innosetup-6.7.4.exe'; sha256 = 'a549929319f9120716677c685535ca4671e4b937b5fdc87e2585f32d8eb30e8c' },
    [ordered]@{ name = 'innounp-2.zip'; url = 'https://raw.githubusercontent.com/jrathlev/InnoUnpacker-Windows-GUI/032d64c782efc6b36766d8f9c3b1b2f042144a58/innounp-2/bin/innounp-2.zip'; sha256 = '10fce27b6344b7674d5625cc34afca1c88c12183d957c6d93e401097fa2556cd' }
)
foreach ($download in $downloads) {
    $path = Join-Path $Destination $download.name
    Invoke-WebRequest -UseBasicParsing -Uri $download.url -OutFile $path
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $download.sha256) {
        throw "Download checksum differs: $($download.name)"
    }
}
$installer = Join-Path $Destination $downloads[0].name
$extractorArchive = Join-Path $Destination $downloads[1].name
$signature = Get-AuthenticodeSignature -LiteralPath $installer
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Pyrsys B\.V\.') {
    throw 'Inno Setup distribution signature is not a valid Pyrsys B.V. signature.'
}
$unpackerRoot = Join-Path $Destination 'unpacker'
Expand-Archive -LiteralPath $extractorArchive -DestinationPath $unpackerRoot
$unpacker = @(Get-ChildItem -LiteralPath $unpackerRoot -Recurse -Filter 'innounp.exe')
if ($unpacker.Count -ne 1) { throw 'Expected exactly one innounp.exe.' }
$versionOutput = (& $unpacker[0].FullName -v 2>&1 | Out-String)
if ($versionOutput -notmatch '2\.71\.2') { throw 'innounp version is not the pinned 2.71.2 release.' }
$compilerRoot = Join-Path $Destination 'compiler'
& $unpacker[0].FullName -x "-d$compilerRoot" $installer
if ($LASTEXITCODE -ne 0) { throw "Compiler extraction exited $LASTEXITCODE." }
$compiler = Join-Path $compilerRoot '{app}\ISCC.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Pinned Inno Setup extraction did not produce ISCC.exe.' }
$compilerHash = (Get-FileHash -LiteralPath $compiler -Algorithm SHA256).Hash.ToLowerInvariant()
if ($compilerHash -cne $expectedCompilerSha256) { throw 'Extracted ISCC hash does not match the pinned compiler.' }
$provenance = [ordered]@{
    assetName = $downloads[0].name
    assetSize = (Get-Item -LiteralPath $installer).Length
    assetSha256 = $downloads[0].sha256
    authenticodeStatus = [string]$signature.Status
    signer = [string]$signature.SignerCertificate.Subject
    extractorName = $downloads[1].name
    extractorVersion = '2.71.2'
    extractorSha256 = $downloads[1].sha256
    compilerPath = [IO.Path]::GetFullPath($compiler)
    compilerSha256 = $compilerHash
    installerExecuted = $false
}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $ProvenancePath) | Out-Null
[IO.File]::WriteAllText($ProvenancePath, ($provenance | ConvertTo-Json -Depth 6))
Write-Output "COMPILER_PATH=$compiler"
Write-Output "PROVENANCE_PATH=$ProvenancePath"
