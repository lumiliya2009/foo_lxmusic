param([ValidateSet('Win32', 'x64')][string]$Platform = 'Win32')
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$hostSource = if ($Platform -eq 'Win32') { 'C:\Program Files (x86)\foobar2000' } else { 'C:\Program Files\foobar2000' }
$testRoot = Join-Path $project "build\test-host-$Platform"
$component = Join-Path $testRoot 'profile\user-components\foo_lxmusic'
if ($Platform -eq 'x64') { $component = Join-Path $testRoot 'profile\user-components-x64\foo_lxmusic' }
New-Item -ItemType Directory -Path $testRoot, $component, (Join-Path $testRoot 'components') -Force | Out-Null
Get-ChildItem -LiteralPath $hostSource -File | Where-Object { $_.Name -eq 'foobar2000.exe' -or $_.Extension -eq '.dll' } | Copy-Item -Destination $testRoot -Force
foreach ($name in @('foo_ui_std.dll', 'foo_input_std.dll')) {
    Copy-Item -LiteralPath (Join-Path $hostSource "components\$name") -Destination (Join-Path $testRoot 'components') -Force
}
[IO.File]::WriteAllText((Join-Path $testRoot 'portable_mode_enabled'), '')
Copy-Item -LiteralPath (Join-Path $project "build\$Platform\Release\foo_lxmusic.dll") -Destination $component -Force
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
& $compiler /nologo /target:exe "/out:$component\foo_lx_bridge.exe" (Join-Path $PSScriptRoot 'BridgeFixture.cs')
if ($LASTEXITCODE -ne 0) { throw 'Fixture compilation failed' }
[IO.File]::WriteAllText((Join-Path $component 'mode.txt'), 'fail')
[IO.File]::WriteAllText((Join-Path $component 'invalid.wav'), 'This is deliberately not an audio file.')
$audio = [IO.BinaryWriter]::new([IO.File]::Create((Join-Path $component 'fixture.wav')))
try {
    $size = 44100 * 2 * 20
    $audio.Write([Text.Encoding]::ASCII.GetBytes('RIFF'))
    $audio.Write([int]($size + 36))
    $audio.Write([Text.Encoding]::ASCII.GetBytes('WAVEfmt '))
    $audio.Write([int]16)
    $audio.Write([int16]1)
    $audio.Write([int16]1)
    $audio.Write([int]44100)
    $audio.Write([int]88200)
    $audio.Write([int16]2)
    $audio.Write([int16]16)
    $audio.Write([Text.Encoding]::ASCII.GetBytes('data'))
    $audio.Write([int]$size)
    $audio.Write([byte[]]::new($size))
} finally { $audio.Dispose() }
Write-Output (Join-Path $testRoot 'foobar2000.exe')
