param([Parameter(Mandatory)]$Manifest)
Set-StrictMode -Version Latest
if (@($Manifest.dependencies).Count -ne 2) { throw 'Exactly two pinned runtime frameworks are required.' }
foreach ($framework in @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')) {
    $dependency = @($Manifest.dependencies | Where-Object { $_.name -ceq $framework })
    if ($dependency.Count -ne 1 -or $dependency[0].version -cne '10.0.13') {
        throw "Runtime framework version differs from notice provenance: $framework"
    }
}
$expectedRuntimeComponents = @(
    [ordered]@{
        package = 'microsoft.netcore.app.runtime.win-x64'
        version = '10.0.13'
        source = 'https://api.nuget.org/v3/index.json'
        contentHash = 'AtNgFBRln5IDZonHHqMN5M/AigeXGWWOf3TB2nDE415hqiYiTPNyaFObfZBC6jXx8JlGZCIpQ6CEM0G+zf6NHQ=='
        packageSha256 = 'c66885d464697be75676b4d1fbca38b8806f3af01d65d27205b3f06add094444'
        sourcePath = 'C:/Users/Minecraft/.nuget/packages/microsoft.netcore.app.runtime.win-x64/10.0.13'
        files = @(
            [ordered]@{ sourcePath = 'LICENSE.TXT'; destination = 'DOTNET-RUNTIME-LICENSE.txt'; size = 1139; sha256 = 'd7a68596ab69b06f51ca278a6545148e4269a9381c26d597c13df5d88e08cf5b' }
            [ordered]@{ sourcePath = 'THIRD-PARTY-NOTICES.TXT'; destination = 'DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt'; size = 78041; sha256 = '6d15e10a101c6bfff2ab4429ed061bf76c456fc4b23ad6b03e0d0f8377148a21' }
        )
    }
    [ordered]@{
        package = 'microsoft.windowsdesktop.app.runtime.win-x64'
        version = '10.0.13'
        source = 'https://api.nuget.org/v3/index.json'
        contentHash = 'xouaR618eD6SeEht2zW16YFBGtRGF6+qTs8nNSLqmBzrBl5di/tf9A1WwDYjGxqQln27Gds0I+WZ8DR2IUePCg=='
        packageSha256 = 'af044758d6bc23eca96b60c9ce13b38f211c3e736eef9458de3cadbbd3e20ff2'
        sourcePath = 'C:/Users/Minecraft/.nuget/packages/microsoft.windowsdesktop.app.runtime.win-x64/10.0.13'
        files = @(
            [ordered]@{ sourcePath = 'LICENSE'; destination = 'WINDOWS-DESKTOP-RUNTIME-LICENSE.txt'; size = 1137; sha256 = 'a89886665765362eb77e0f8e26602c924520041d1711b2eedc136434fe4d01ab' }
        )
    }
)
$actualRuntimeComponents = @($manifest.runtimeComponents)
if ($actualRuntimeComponents.Count -ne $expectedRuntimeComponents.Count) { throw 'Runtime component provenance count differs.' }
foreach ($expected in $expectedRuntimeComponents) {
    $actual = @($actualRuntimeComponents | Where-Object { $_.package -ceq $expected.package })
    if ($actual.Count -ne 1 -or $actual[0].version -cne $expected.version -or
        $actual[0].source -cne $expected.source -or $actual[0].contentHash -cne $expected.contentHash -or
        $actual[0].packageSha256 -cne $expected.packageSha256 -or
        [IO.Path]::GetFullPath($actual[0].sourcePath) -cne [IO.Path]::GetFullPath($expected.sourcePath)) {
        throw "Runtime component provenance differs: $($expected.package)"
    }
    $actualFiles = @($actual[0].files)
    if ($actualFiles.Count -ne $expected.files.Count) { throw "Runtime notice provenance count differs: $($expected.package)" }
    foreach ($expectedFile in $expected.files) {
        $actualFile = @($actualFiles | Where-Object { $_.destination -ceq $expectedFile.destination })
        if ($actualFile.Count -ne 1 -or $actualFile[0].sourcePath -cne $expectedFile.sourcePath -or
            [long]$actualFile[0].size -ne $expectedFile.size -or $actualFile[0].sha256 -cne $expectedFile.sha256) {
            throw "Runtime notice provenance differs: $($expected.package)/$($expectedFile.sourcePath)"
        }
    }
}
$expectedRuntimeComponents
