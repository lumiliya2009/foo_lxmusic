param([ValidateSet('Win32', 'x64')][string]$Platform = 'Win32')
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$testBuild = Join-Path $project "build\navigation-regression-$Platform"
New-Item -ItemType Directory -Path $testBuild -Force | Out-Null
$source = [IO.File]::ReadAllText((Join-Path $project 'playlist_actions.cpp'))
$prefixEnd = $source.IndexOf('bool is_collection(')
$functionsStart = $source.IndexOf('std::shared_ptr<NavigationOrigin> capture_navigation_origin(')
$functionsEnd = $source.IndexOf('void install_collection_actions(', $functionsStart)
if ($prefixEnd -lt 0 -or $functionsStart -lt 0 -or $functionsEnd -lt 0) { throw 'Unexpected navigation source layout' }
# Compile production navigation and the SDK's actual index tracker, replacing only host/UI boundaries.
$prefix = $source.Substring($source.IndexOf("`n") + 1, $prefixEnd - $source.IndexOf("`n") - 1)
$functions = $source.Substring($functionsStart, $functionsEnd - $functionsStart)
$harness = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'NavigationHostDouble.h'))
$tracker = [IO.File]::ReadAllText((Join-Path $project '..\helpers\playlist_position_reference_tracker.h')).Replace('#pragma once', '')
$checks = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'NavigationRegression.cpp'))
[IO.File]::WriteAllText((Join-Path $testBuild 'regression.cpp'), $harness + "`n" + $tracker + "`n" + $prefix + "`n}`n" + $functions + "`n}`n" + $checks)
$testProject = @'
<Project DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <ItemGroup Label="ProjectConfigurations"><ProjectConfiguration Include="Release|Win32"><Configuration>Release</Configuration><Platform>Win32</Platform></ProjectConfiguration><ProjectConfiguration Include="Release|x64"><Configuration>Release</Configuration><Platform>x64</Platform></ProjectConfiguration></ItemGroup>
  <PropertyGroup Label="Globals"><WindowsTargetPlatformVersion>10.0</WindowsTargetPlatformVersion></PropertyGroup>
  <Import Project="$(VCTargetsPath)\Microsoft.Cpp.Default.props" />
  <PropertyGroup Label="Configuration"><ConfigurationType>Application</ConfigurationType><PlatformToolset>v145</PlatformToolset></PropertyGroup>
  <Import Project="$(VCTargetsPath)\Microsoft.Cpp.props" />
  <PropertyGroup><OutDir>$(ProjectDir)</OutDir><IntDir>$(ProjectDir)obj\</IntDir><TargetName>regression</TargetName></PropertyGroup>
  <ItemDefinitionGroup><ClCompile><LanguageStandard>stdcpp17</LanguageStandard><AdditionalOptions>/utf-8 %(AdditionalOptions)</AdditionalOptions><ExceptionHandling>Sync</ExceptionHandling></ClCompile><Link><SubSystem>Console</SubSystem></Link></ItemDefinitionGroup>
  <ItemGroup><ClCompile Include="regression.cpp" /></ItemGroup>
  <Import Project="$(VCTargetsPath)\Microsoft.Cpp.targets" />
</Project>
'@
[IO.File]::WriteAllText((Join-Path $testBuild 'regression.vcxproj'), $testProject)
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vsRoot = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
& (Join-Path $vsRoot 'MSBuild\Current\Bin\MSBuild.exe') (Join-Path $testBuild 'regression.vcxproj') /p:Configuration=Release "/p:Platform=$Platform" /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw 'Navigation harness build failed' }
& (Join-Path $testBuild 'regression.exe')
if ($LASTEXITCODE -ne 0) { throw 'Navigation regression failed' }
