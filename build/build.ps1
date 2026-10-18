# Builds a release: self-contained publish, portable ZIP and the installer.
# Usage: pwsh build/build.ps1 [-Version 1.0.0]
param([string]$Version = "1.0.0")
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$publish = Join-Path $root 'publish'
$dist = Join-Path $root 'dist'

Remove-Item -Recurse -Force $publish, $dist -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $dist | Out-Null

dotnet publish (Join-Path $root 'src\Satsuma\Satsuma.csproj') -c Release -r win-x64 `
    --self-contained true -p:PublishReadyToRun=true -p:Version=$Version -o $publish
if ($LASTEXITCODE) { throw "publish failed" }

Compress-Archive -Path (Join-Path $publish '*') -DestinationPath (Join-Path $dist "Satsuma-$Version-portable-win-x64.zip") -CompressionLevel Optimal

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found (winget install JRSoftware.InnoSetup)" }
& $iscc "/DAppVersion=$Version" "/DSourceDir=$publish" (Join-Path $root 'installer\Satsuma.iss')
if ($LASTEXITCODE) { throw "installer build failed" }

Get-ChildItem $dist | ForEach-Object { '{0}  {1:N1} MB' -f $_.Name, ($_.Length / 1MB) }
