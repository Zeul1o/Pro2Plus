# Generate English UI source: src\Pro2Plus -> src\Pro2Plus.En
# Dictionary: tools\en-strings.json
# Replacement is exact-literal only (whole XAML attribute value / whole C# string),
# so longer Chinese sentences never get half-translated.
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Split-Path -Parent $root
$src  = Join-Path $repo 'src\Pro2Plus'
$dst  = Join-Path $repo 'src\Pro2Plus.En'
$map  = Get-Content (Join-Path $root 'en-strings.json') -Raw -Encoding UTF8 | ConvertFrom-Json

$dict = @{}
foreach ($p in $map.PSObject.Properties) { $dict[$p.Name] = $p.Value }

# /MIR keeps the generated tree in sync with the source: plain /E would leave
# renamed/deleted .cs files behind and cause duplicate type definitions.
robocopy $src $dst /MIR /XD bin obj .vs artifacts /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "copy failed: $LASTEXITCODE" }

$enc = New-Object System.Text.UTF8Encoding($true)
$hit = 0
$files = Get-ChildItem $dst -Recurse -Include *.xaml,*.cs -File

foreach ($f in $files) {
    $text = Get-Content $f.FullName -Raw -Encoding UTF8
    if ($null -eq $text) { continue }
    $orig = $text

    if ($f.Extension -eq '.xaml') {
        $evaluator = {
            param($m)
            $v = $m.Groups[1].Value
            if ($script:dict.ContainsKey($v)) { $script:hit++; return '="' + $script:dict[$v] + '"' }
            return $m.Value
        }
        $text = [regex]::Replace($text, '="([^"]*)"', $evaluator)
    }
    else {
        $evaluator = {
            param($m)
            $v = $m.Groups[1].Value
            if ($script:dict.ContainsKey($v)) { $script:hit++; return '"' + $script:dict[$v] + '"' }
            return $m.Value
        }
        $text = [regex]::Replace($text, '"((?:[^"\\]|\\.)*)"', $evaluator)
    }

    if ($text -ne $orig) { [System.IO.File]::WriteAllText($f.FullName, $text, $enc) }
}

Write-Host ("[en] {0} literals translated -> {1}" -f $hit, $dst)

$csproj = Join-Path $dst 'Pro2Plus.csproj'
if (Test-Path $csproj) {
    $text = Get-Content $csproj -Raw -Encoding UTF8
    $text = $text.Replace('<AssemblyTitle>Pro2+</AssemblyTitle>', '<AssemblyTitle>Pro2+ (English)</AssemblyTitle>')
    $text = $text.Replace('<Product>Pro2+</Product>', '<Product>Pro2+ (English)</Product>')
    [System.IO.File]::WriteAllText($csproj, $text, $enc)
}
exit 0
