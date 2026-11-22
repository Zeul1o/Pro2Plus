# Pro2+ build script: self-contained single exe -> dist\Pro2+.exe
# Usage:
#   powershell -ExecutionPolicy Bypass -File .\publish.ps1
#   powershell -ExecutionPolicy Bypass -File .\publish.ps1 -English
param(
    [switch]$English
)
$ErrorActionPreference = 'Stop'

$root  = Split-Path -Parent $MyInvocation.MyCommand.Path
$dist  = Join-Path $root 'dist'
$stage = Join-Path $env:TEMP ('pro2p-' + [guid]::NewGuid().ToString('N').Substring(0, 8))

$dotnet = 'dotnet'
if (Test-Path (Join-Path $env:USERPROFILE 'dotnet8\dotnet.exe')) {
    $dotnet = Join-Path $env:USERPROFILE 'dotnet8\dotnet.exe'
}

if ($English) {
    Write-Host '[0/3] Generating English UI source...' -ForegroundColor Cyan
    & (Join-Path $root 'tools\localize-en.ps1')
    $proj = Join-Path $root 'src\Pro2Plus.En\Pro2Plus.csproj'
    $name = 'Pro2+-en.exe'
} else {
    $proj = Join-Path $root 'src\Pro2Plus\Pro2Plus.csproj'
    $name = 'Pro2+.exe'
}

if (-not (Test-Path $proj)) { throw "Project not found: $proj" }

Write-Host "[1/4] Publishing $name ..." -ForegroundColor Cyan
& $dotnet publish $proj -c Release -v q --nologo -p:DebugType=None -p:DebugSymbols=false -o $stage
if ($LASTEXITCODE -ne 0) { throw "publish failed, exit $LASTEXITCODE" }

Write-Host '[2/4] Copying output...' -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $dist | Out-Null
Copy-Item (Join-Path $stage 'Pro2Plus.exe') (Join-Path $dist $name) -Force

Write-Host '[3/4] Copying license texts...' -ForegroundColor Cyan
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $dist 'LICENSE') -Force
Copy-Item (Join-Path $root 'NOTICE')  (Join-Path $dist 'NOTICE')  -Force
$legal = Join-Path $dist 'licenses'
New-Item -ItemType Directory -Force -Path $legal | Out-Null
Copy-Item (Join-Path $root 'licenses\*') $legal -Force

Write-Host '[4/4] Done:' -ForegroundColor Green
Get-ChildItem $dist | ForEach-Object {
    '{0,-16} {1:N1} MB' -f $_.Name, ($_.Length / 1MB)
}
