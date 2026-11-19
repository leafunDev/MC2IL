[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9A-Za-z][0-9A-Za-z.-]*$')]
    [string] $version
)

$ErrorActionPreference = 'Stop'

$projectRoot = $PSScriptRoot
$appProject = Join-Path $projectRoot 'MC2IL/MC2IL.csproj'
$baseVersion = (Select-Xml -XPath '//Project/PropertyGroup/VersionPrefix' -Path $appProject).Node.InnerText
$releaseVersion = "$baseVersion-$version"
$artifactRoot = Join-Path $projectRoot "artifacts/$releaseVersion"

if (Test-Path -LiteralPath $artifactRoot) {
    throw "Output already exists: $artifactRoot. Move it aside before rebuilding this version."
}

$null = New-Item -ItemType Directory -Path $artifactRoot -Force

function Invoke-DotNet {
    param([Parameter(Mandatory = $true)][string[]] $Arguments)

    Write-Host ("> dotnet " + ($Arguments -join ' '))
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet exited with code $LASTEXITCODE"
    }
}

$plugins = @(
    @{ Project = 'Plugins/BuildReport/MC2IL.Plugin.BuildReport.csproj'; Framework = 'net10.0'; Assembly = 'MC2IL.Plugin.BuildReport.dll' },
    @{ Project = 'Plugins/ControlFlowGraph/MC2IL.Plugin.ControlFlowGraph.csproj'; Framework = 'net10.0'; Assembly = 'MC2IL.Plugin.ControlFlowGraph.dll' },
    @{ Project = 'Plugins/Mfuscator/MC2IL.Plugin.Mfuscator.csproj'; Framework = 'net10.0'; Assembly = 'MC2IL.Plugin.Mfuscator.dll' },
    @{ Project = 'Plugins/OrbisPkg/MC2IL.Plugin.OrbisPkg.csproj'; Framework = 'netstandard2.0'; Assembly = 'MC2IL.Plugin.OrbisPkg.dll' },
    @{ Project = 'Plugins/Pdb/MC2IL.Plugin.Pdb.csproj'; Framework = 'net10.0'; Assembly = 'MC2IL.Plugin.Pdb.dll' },
    @{ Project = 'Plugins/StrippedCodeRegSupport/MC2IL.Plugin.StrippedCodeRegSupport.csproj'; Framework = 'net10.0'; Assembly = 'MC2IL.Plugin.StrippedCodeRegSupport.dll' }
)

Write-Host "Building MC2IL plugins for $releaseVersion"
foreach ($plugin in $plugins) {
    $projectPath = Join-Path $projectRoot $plugin.Project
    Invoke-DotNet -Arguments @('build', $projectPath, '--configuration', 'Release', "-p:Version=$releaseVersion")

    $pluginFolder = Split-Path -Parent $projectPath
    $pluginAssembly = Join-Path $pluginFolder "bin/Release/$($plugin.Framework)/$($plugin.Assembly)"
    if (-not (Test-Path -LiteralPath $pluginAssembly)) {
        throw "Plugin assembly was not produced: $pluginAssembly"
    }
    $plugin['BuiltAssembly'] = $pluginAssembly
}

$orbisDependency = Join-Path $projectRoot 'Plugins/OrbisPkg/Deps/LibOrbisPkg.Core.dll'
if (-not (Test-Path -LiteralPath $orbisDependency)) {
    throw "Required OrbisPkg dependency was not found: $orbisDependency"
}

$runtimeIds = @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')
foreach ($runtimeId in $runtimeIds) {
    Write-Host "Publishing self-contained build for $runtimeId"

    $packageRoot = Join-Path $artifactRoot "staging/$runtimeId"
    $pluginOutput = Join-Path $packageRoot 'Plugins'
    $null = New-Item -ItemType Directory -Path $pluginOutput -Force

    Invoke-DotNet -Arguments @(
        'publish', $appProject,
        '--configuration', 'Release',
        '--framework', 'net10.0',
        '--runtime', $runtimeId,
        '--self-contained', 'true',
        "-p:Version=$releaseVersion",
        '-p:PublishSingleFile=true',
        '-p:PublishTrimmed=false',
        '--output', $packageRoot
    )

    foreach ($plugin in $plugins) {
        Copy-Item -LiteralPath $plugin.BuiltAssembly -Destination $pluginOutput
    }
    Copy-Item -LiteralPath $orbisDependency -Destination $pluginOutput
    Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $packageRoot
    Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $packageRoot
    Copy-Item -LiteralPath (Join-Path $projectRoot 'NOTICE') -Destination $packageRoot
    Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSES') -Destination $packageRoot -Recurse

    $archive = Join-Path $artifactRoot "MC2IL-$releaseVersion-$runtimeId.zip"
    $packageFiles = Get-ChildItem -LiteralPath $packageRoot -Force | Select-Object -ExpandProperty FullName
    Compress-Archive -Path $packageFiles -DestinationPath $archive -CompressionLevel Optimal
}

$checksums = foreach ($archive in Get-ChildItem -LiteralPath $artifactRoot -Filter '*.zip') {
    $hash = (Get-FileHash -LiteralPath $archive.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($archive.Name)"
}
Set-Content -LiteralPath (Join-Path $artifactRoot 'SHA256SUMS.txt') -Value $checksums -Encoding utf8

Write-Host "Release assets are ready in $artifactRoot"
