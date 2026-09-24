$ErrorActionPreference = 'Stop'
$root = Join-Path $env:TEMP ('cmux-git-' + [guid]::NewGuid().ToString('N'))
$resolved = [IO.Path]::GetFullPath($root)
$tempRoot = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\'
if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Test path escaped TEMP.' }
New-Item -ItemType Directory -Path $root | Out-Null
try {
    git -C $root init -q -b main
    Set-Content -LiteralPath (Join-Path $root 'readme.txt') -Value 'initial'
    git -C $root add readme.txt
    git -C $root -c user.name=cmux-test -c user.email=cmux@example.invalid commit -qm initial
    Add-Type -Path (Join-Path $PSScriptRoot '..\src\Cmux.Core\bin\Debug\net8.0\Cmux.Core.dll')
    $clean = [Cmux.Core.GitProjectContext]::ReadAsync($root).GetAwaiter().GetResult()
    if ($clean.Branch -ne 'main' -or $clean.IsDirty) { throw "Clean status was $clean" }
    Add-Content -LiteralPath (Join-Path $root 'readme.txt') -Value 'changed'
    $dirty = [Cmux.Core.GitProjectContext]::ReadAsync($root).GetAwaiter().GetResult()
    if ($dirty.Branch -ne 'main' -or -not $dirty.IsDirty) { throw "Dirty status was $dirty" }
    Write-Host 'M7 Git context PASS: main clean -> main *.'
}
finally { Remove-Item -LiteralPath $resolved -Recurse -Force }
