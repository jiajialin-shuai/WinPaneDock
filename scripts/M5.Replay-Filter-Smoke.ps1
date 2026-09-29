param([string]$AssemblyPath = (Join-Path $PSScriptRoot '../spikes/M0.Terminal.Wpf/bin/Release/net8.0-windows/Cmux.Spike.Terminal.dll'))
$ErrorActionPreference = 'Stop'

$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path $AssemblyPath).Path)
$method = $assembly.GetType('Cmux.Spike.Terminal.ConptyConnection').GetMethod(
    'FilterReplay', [Reflection.BindingFlags]'NonPublic,Static')
$esc = [char]27
$bel = [char]7
$input = "start${esc}[?u${esc}]10;?${esc}\${esc}]11;?${bel}${esc}]4;3;?${esc}\" +
    "]10;rgb:cccc/cccc/cccc\]11;rgb:0c0c/0c0c/0c0c\" +
    "${esc}]10;rgb:ffff/ffff/ffff${bel}${esc}]2;title${bel}end"
$expected = "start${esc}]10;rgb:ffff/ffff/ffff${bel}${esc}]2;title${bel}end"
$actual = $method.Invoke($null, @($input))
if ($actual -cne $expected) { throw 'Replay filter changed text or left terminal query responses behind.' }
Write-Host 'M5 replay filter PASS: probes and echoed replies removed; color and title settings preserved.'
