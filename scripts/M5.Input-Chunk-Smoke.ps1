$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$core = Join-Path $root 'src/Cmux.Core/bin/Release/net8.0/Cmux.Core.dll'
$terminal = Join-Path $root 'src/Cmux.Terminal/bin/Release/net8.0-windows/Cmux.Terminal.dll'
Add-Type -Path $core
Add-Type -Path $terminal
$source = ([string]'plain-' * 200000) + '😀' + ("`0`r`n" * 1000)
$builder = [Text.StringBuilder]::new()
$offset = 0
$count = 0
while ($offset -lt $source.Length) {
    $length = [Cmux.Terminal.InputChunker]::GetChunkLength($source, $offset)
    if ($length -le 0 -or $length -gt [Cmux.Terminal.InputChunker]::MaxChunkChars) { throw "Invalid chunk length $length at $offset." }
    $chunk = $source.Substring($offset, $length)
    if ([char]::IsHighSurrogate($chunk[$chunk.Length - 1]) -and $offset + $length -lt $source.Length) { throw 'Surrogate pair was split.' }
    [void]$builder.Append($chunk)
    $offset += $length
    $count++
}
if ($builder.ToString() -cne $source) { throw 'Input chunking changed the payload.' }
Write-Host "Input chunking PASS: $($source.Length) chars in $count ordered chunks (max $([Cmux.Terminal.InputChunker]::MaxChunkChars))."
