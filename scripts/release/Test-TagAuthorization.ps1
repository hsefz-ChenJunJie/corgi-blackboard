[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$TagRef,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string]$ExpectedCommit,
    [Parameter(Mandatory)][string]$Repository,
    [string]$ExpectedTagObject
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($TagRef -cnotmatch '^refs/tags/v0\.1\.0-test\.[1-9][0-9]*$') {
    throw 'Publication requires an exact numeric Test tag.'
}
if ($Repository -cne 'hsefz-ChenJunJie/corgi-blackboard') {
    throw 'Publication repository mismatch.'
}
$previousGitMaster = $env:GIT_MASTER
$env:GIT_MASTER = '1'
try {
    $type = & git cat-file -t $TagRef
    if ($LASTEXITCODE -ne 0 -or $type -cne 'tag') { throw 'An annotated authorization tag is required.' }
    $tagObject = & git rev-parse --verify $TagRef
    if ($LASTEXITCODE -ne 0 -or $tagObject -cnotmatch '^[a-f0-9]{40}$') { throw 'Cannot resolve tag object.' }
    if ($ExpectedTagObject -and $tagObject -cne $ExpectedTagObject) { throw 'Authorization tag changed during delivery.' }
    $commit = & git rev-parse --verify "$TagRef^{commit}"
    if ($LASTEXITCODE -ne 0 -or $commit -cne $ExpectedCommit) { throw 'Authorization tag/source mismatch.' }
    $headCommit = & git rev-parse --verify HEAD
    if ($LASTEXITCODE -ne 0 -or $headCommit -cne $ExpectedCommit) { throw 'Checked-out source mismatch.' }
    $contents = & git for-each-ref '--format=%(contents)' $TagRef
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read tag authorization.' }
    $tag = $TagRef.Substring('refs/tags/'.Length)
    # This exact annotation authorizes only the complete validated asset set built
    # from this source in the tag-triggered run. Readiness later binds its hashes.
    $required = @(
        'Authorize CorgiBlackboard Test prerelease'
        "repository=$Repository"
        "tag=$tag"
        "source=$ExpectedCommit"
        'channel=Test'
        'assets=validated-same-run'
    ) -join "`n"
    if (($contents -join "`n").TrimEnd([char[]]"`r`n") -cne $required) {
        throw 'Exact Test publication authorization annotation is missing or differs.'
    }
    [pscustomobject]@{ version = $tag.Substring(1); tagObject = $tagObject }
} finally {
    $env:GIT_MASTER = $previousGitMaster
}
