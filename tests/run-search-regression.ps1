param([ValidateSet('Win32', 'x64')][string]$Platform = 'Win32')
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$testBuild = Join-Path $project "build\search-regression-$Platform"
New-Item -ItemType Directory -Path $testBuild -Force | Out-Null
$header = [IO.File]::ReadAllText((Join-Path $project 'lx_common.h'))
$header = $header.Substring($header.IndexOf('namespace lxmusic {'), $header.IndexOf('struct SourceSite') - $header.IndexOf('namespace lxmusic {'))
$common = [IO.File]::ReadAllText((Join-Path $project 'lx_common.cpp'))
$start = $common.IndexOf('const std::string& Payload::get(')
$common = $common.Substring($start, $common.IndexOf('void payload_to_info(') - $start)
$source = [IO.File]::ReadAllText((Join-Path $project 'search_state.cpp')).Replace('#include "stdafx.h"', '')
$checks = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'SearchRegression.cpp'))
$preamble = @'
#include <algorithm>
#include <charconv>
#include <cstring>
#include <functional>
#include <iostream>
#include <stdexcept>
#include <string>
#include <vector>
using DWORD = unsigned long;
'@
[IO.File]::WriteAllText((Join-Path $testBuild 'regression.cpp'), $preamble + "`n" + $header + $common + "`n}`n" + $source + "`n" + $checks)
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
if ($LASTEXITCODE -ne 0) { throw 'Search harness build failed' }
& (Join-Path $testBuild 'regression.exe')
if ($LASTEXITCODE -ne 0) { throw 'Search regression failed' }
