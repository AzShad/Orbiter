# Compile Orbiter (exe autonome) puis génère l'installeur dans .\dist
# La version vient du fichier VERSION (voir scripts\release.ps1 pour en publier une nouvelle).
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
if (Test-Path "C:\Program Files\dotnet") { $env:PATH = "C:\Program Files\dotnet;$env:PATH" }

$version = (Get-Content VERSION -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "VERSION invalide : '$version' (format attendu : X.Y.Z)" }
Write-Host "== Orbiter $version ==" -ForegroundColor Cyan

Remove-Item publish -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish Orbiter\Orbiter.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish
if ($LASTEXITCODE) { throw "dotnet publish a échoué" }

New-Item -ItemType Directory -Force dist | Out-Null
Copy-Item publish\Orbiter.exe "dist\Orbiter-Portable-$version.exe" -Force

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 introuvable (winget install JRSoftware.InnoSetup)" }
& $iscc /Q "/DAppVersion=$version" installer\Orbiter.iss
if ($LASTEXITCODE) { throw "Inno Setup a échoué" }

Get-ChildItem dist -Filter "*$version*" | Select-Object Name, @{n='Taille (Mo)'; e={[math]::Round($_.Length / 1MB, 1)}}
