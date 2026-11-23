param(
    [switch]$SkipBuild,
    [switch]$InstallX86,
    [string]$PlatformToolset = 'v145'
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$distDir = Join-Path $projectRoot 'dist'
$bridgeProject = Join-Path $projectRoot 'bridge\foo_lx_bridge.csproj'
$bridgeOutput = Join-Path $projectRoot 'bridge\bin\Release\net48'
$privateEndpointSource = Join-Path $projectRoot 'bridge\PrivateUpdateEndpoint.g.cs'
$privateEndpointFile = Join-Path $projectRoot 'build\private-update-endpoint.txt'
$bridgePackageFiles = @(
    'foo_lx_bridge.exe',
    'foo_lx_bridge.exe.config'
)
$version = '0.1.18'

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw '找不到 Visual Studio Installer (vswhere.exe)' }
$vsRoot = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
if ([string]::IsNullOrWhiteSpace($vsRoot)) { throw '找不到带 C++ 工具链的 Visual Studio' }
$msbuild = Join-Path $vsRoot 'MSBuild\Current\Bin\MSBuild.exe'

if (-not $SkipBuild) {
    $privateEndpoint = $env:LX_UPDATE_ENDPOINT
    if ([string]::IsNullOrWhiteSpace($privateEndpoint) -and (Test-Path -LiteralPath $privateEndpointFile)) {
        $privateEndpoint = (Get-Content -LiteralPath $privateEndpointFile -Raw).Trim()
    }
    $privateEndpoint = ([string]$privateEndpoint).Trim().TrimEnd('/') + $(if ([string]::IsNullOrWhiteSpace($privateEndpoint)) { '' } else { '/' })
    if (-not [string]::IsNullOrWhiteSpace($privateEndpoint)) {
        $uri = $null
        if (-not [Uri]::TryCreate($privateEndpoint, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -ne 'https') {
            throw 'LX_UPDATE_ENDPOINT 必须是 HTTPS 地址'
        }
    }
    $privateBytes = [Text.Encoding]::UTF8.GetBytes($privateEndpoint)
    for ($i = 0; $i -lt $privateBytes.Length; $i++) { $privateBytes[$i] = $privateBytes[$i] -bxor 0xA7 }
    $privateLiteral = ($privateBytes -join ',')
    $privateDirectory = Split-Path -Parent $privateEndpointSource
    New-Item -ItemType Directory -Path $privateDirectory -Force | Out-Null
    $generated = @"
using System.Text;
namespace LxMusic.Bridge.Online
{
    internal static class PrivateUpdateEndpoint
    {
        internal static string Value
        {
            get
            {
                var bytes = new byte[] { $privateLiteral };
                for (var i = 0; i < bytes.Length; i++) bytes[i] ^= 0xA7;
                return Encoding.UTF8.GetString(bytes);
            }
        }
    }
}
"@
    [IO.File]::WriteAllText($privateEndpointSource, $generated, (New-Object Text.UTF8Encoding($false)))
    try {
        & dotnet build $bridgeProject -c Release -p:RestoreLockedMode=true
        if ($LASTEXITCODE -ne 0) { throw 'LX bridge 编译失败' }
    }
    finally {
        if (Test-Path -LiteralPath $privateEndpointSource) { Remove-Item -LiteralPath $privateEndpointSource -Force }
    }

    & $msbuild (Join-Path $projectRoot 'foo_lxmusic.vcxproj') /m /t:Build /p:Configuration=Release /p:Platform=Win32 "/p:PlatformToolset=$PlatformToolset" /v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'Win32 插件编译失败' }
    & $msbuild (Join-Path $projectRoot 'foo_lxmusic.vcxproj') /m /t:Build /p:Configuration=Release /p:Platform=x64 "/p:PlatformToolset=$PlatformToolset" /v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'x64 插件编译失败' }
}

New-Item -ItemType Directory -Path $distDir -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression

function New-ComponentPackage {
    param(
        [Parameter(Mandatory=$true)][string]$Architecture,
        [Parameter(Mandatory=$true)][string]$HostLabel,
        [Parameter(Mandatory=$true)][string]$DllPath
    )

    $stageDir = Join-Path $distDir "stage-$HostLabel-$Architecture"
    $resolvedDist = [IO.Path]::GetFullPath($distDir).TrimEnd('\') + '\'
    $resolvedStage = [IO.Path]::GetFullPath($stageDir)
    if (-not $resolvedStage.StartsWith($resolvedDist, [StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝清理意外路径：$resolvedStage"
    }
    if (Test-Path -LiteralPath $stageDir) { Remove-Item -LiteralPath $stageDir -Recurse -Force }
    New-Item -ItemType Directory -Path $stageDir -Force | Out-Null

    Copy-Item -LiteralPath $DllPath -Destination (Join-Path $stageDir 'foo_lxmusic.dll')
    foreach ($name in $bridgePackageFiles) {
        $source = Join-Path $bridgeOutput $name
        if (-not (Test-Path -LiteralPath $source)) { throw "桥接发布文件缺失：$name" }
        Copy-Item -LiteralPath $source -Destination $stageDir
    }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $stageDir 'README.md')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE'), (Join-Path $projectRoot 'THIRD_PARTY_NOTICES.md') -Destination $stageDir
    Copy-Item -LiteralPath (Join-Path $projectRoot 'bridge\LICENSE') -Destination (Join-Path $stageDir 'BRIDGE-LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses') -Destination (Join-Path $stageDir 'licenses') -Recurse

    $packagePath = Join-Path $distDir "foo_lxmusic-$version-$HostLabel-$Architecture.fb2k-component"
    if (Test-Path -LiteralPath $packagePath) { Remove-Item -LiteralPath $packagePath -Force }
    $archive = [System.IO.Compression.ZipFile]::Open($packagePath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $stageRoot = [IO.Path]::GetFullPath($stageDir).TrimEnd('\') + '\'
        foreach ($file in Get-ChildItem -LiteralPath $stageDir -File -Recurse | Sort-Object FullName) {
            $relative = [IO.Path]::GetFullPath($file.FullName).Substring($stageRoot.Length).Replace('\', '/')
            if ($relative.Contains('\')) { throw "安装包路径未标准化：$relative" }
            $entry = $archive.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $file.LastWriteTime
            $input = [IO.File]::OpenRead($file.FullName)
            try {
                $output = $entry.Open()
                try { $input.CopyTo($output) } finally { $output.Dispose() }
            }
            finally { $input.Dispose() }
        }
    }
    finally { $archive.Dispose() }
    Remove-Item -LiteralPath $stageDir -Recurse -Force
    Get-Item -LiteralPath $packagePath
}

$x86Package = New-ComponentPackage -Architecture 'x86' -HostLabel 'fb1' -DllPath (Join-Path $projectRoot 'build\Win32\Release\foo_lxmusic.dll')
$x64Package = New-ComponentPackage -Architecture 'x64' -HostLabel 'fb2' -DllPath (Join-Path $projectRoot 'build\x64\Release\foo_lxmusic.dll')

if ($InstallX86) {
    $targetDir = 'C:\Program Files (x86)\foobar2000\profile\user-components\foo_lxmusic'
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $projectRoot 'build\Win32\Release\foo_lxmusic.dll') -Destination $targetDir -Force
    foreach ($name in $bridgePackageFiles) {
        Copy-Item -LiteralPath (Join-Path $bridgeOutput $name) -Destination $targetDir -Force
    }
}

$x86Package, $x64Package | Select-Object FullName, Length, LastWriteTime
