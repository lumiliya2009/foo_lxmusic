param([ValidateSet('Win32', 'x64')][string]$Platform = 'x64')
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path $project "build\display-host-$Platform"
$hostSource = if ($Platform -eq 'Win32') { 'C:\Program Files (x86)\foobar2000' } else { 'C:\Program Files\foobar2000' }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vsRoot = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
& (Join-Path $vsRoot 'MSBuild\Current\Bin\MSBuild.exe') (Join-Path $project 'foo_lxmusic.vcxproj') /m /p:Configuration=Release "/p:Platform=$Platform" /p:LxDisplayTest=true /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw 'Display integration fixture build failed' }
$componentRoot = if ($Platform -eq 'Win32') { 'user-components' } else { 'user-components-x64' }
$component = Join-Path $testRoot "profile\$componentRoot\foo_lxmusic"
New-Item -ItemType Directory -Path $testRoot, $component, (Join-Path $testRoot 'components') -Force | Out-Null
Get-ChildItem -LiteralPath $hostSource -File | Where-Object { $_.Name -eq 'foobar2000.exe' -or $_.Extension -eq '.dll' } | Copy-Item -Destination $testRoot -Force
$runtime = Join-Path $hostSource 'runtime'
if (Test-Path -LiteralPath $runtime) { Copy-Item -LiteralPath $runtime -Destination $testRoot -Recurse -Force }
foreach ($name in @('foo_ui_std.dll', 'foo_input_std.dll')) {
    Copy-Item -LiteralPath (Join-Path $hostSource "components\$name") -Destination (Join-Path $testRoot 'components') -Force
}
[IO.File]::WriteAllText((Join-Path $testRoot 'portable_mode_enabled'), '')
Copy-Item -LiteralPath (Join-Path $project "build\display-test-$Platform\foo_lxmusic.dll") -Destination $component -Force
$report = Join-Path $testRoot 'profile\display-test.txt'
if (Test-Path -LiteralPath $report) { Remove-Item -LiteralPath $report }
# /quiet runs an isolated host fixture, which writes a report and exits itself.
# No UI automation or user profile changes are involved.
$process = Start-Process -FilePath (Join-Path $testRoot 'foobar2000.exe') -WorkingDirectory $testRoot -ArgumentList '/quiet' -WindowStyle Hidden -PassThru
if (-not $process.WaitForExit(30000)) { throw "Display fixture did not exit: $testRoot" }
if (-not (Test-Path -LiteralPath $report)) { throw "Display fixture produced no report (exit $($process.ExitCode))" }
$result = Get-Content -LiteralPath $report -Raw
Write-Output "$Platform $result"
if (-not $result.StartsWith('PASS ')) { throw 'Display integration failed' }
