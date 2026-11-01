function Assert-PackageIdentity {
    param(
        [Parameter(Mandatory)][byte[]]$AssemblyBytes,
        [Parameter(Mandatory)][psobject]$Identity
    )
    $prefix = "$($Identity.version)+"
    if ($Identity.version -cnotmatch '^0\.1\.0-test\.[1-9][0-9]*$' -or
        $Identity.commit -cnotmatch '^[a-f0-9]{40}$' -or $Identity.channel -cne 'Test' -or
        $Identity.informationalBuildVersion -cne "$prefix$($Identity.commit)") {
        throw 'Invalid release or informational build identity.'
    }
    # ECMA-335 attribute blob: prolog, two short UTF-8 SerStrings, no named arguments.
    $hex = [BitConverter]::ToString($AssemblyBytes)
    foreach ($attribute in @(@('ReleaseChannel', $Identity.channel),
        @('BuildVersion', $Identity.informationalBuildVersion), @('GitHash', $Identity.commit))) {
        $key = [Text.Encoding]::UTF8.GetBytes($attribute[0])
        $value = [Text.Encoding]::UTF8.GetBytes($attribute[1])
        $blob = [byte[]](@(1, 0, $key.Length) + $key + @($value.Length) + $value + @(0, 0))
        if (-not $hex.Contains([BitConverter]::ToString($blob))) { throw "Missing assembly identity: $($attribute[0])" }
    }
}
