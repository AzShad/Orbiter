<#
.SYNOPSIS
  Publie une nouvelle version d'Orbiter.

.DESCRIPTION
  1. Incrémente le fichier VERSION (patch, minor ou major — SemVer)
  2. Ajoute une section en tête de CHANGELOG.md avec les notes fournies
  3. Crée un commit "Release vX.Y.Z" et le tag vX.Y.Z
  4. Avec -Push : pousse la branche et le tag sur GitHub, ce qui déclenche
     la compilation et la publication de la Release (installeur + portable).

.EXAMPLE
  .\scripts\release.ps1 -Bump patch -Notes "Corrige l'affichage des dossiers vides" -Push
  .\scripts\release.ps1 -Bump minor -Notes "Ajout de l'onglet Telnet","Raccourci Ctrl+T" -Push
#>
param(
    [Parameter(Mandatory)][ValidateSet('patch', 'minor', 'major')][string]$Bump,
    [Parameter(Mandatory)][string[]]$Notes,
    [switch]$Push,
    # Ligne ajoutée en fin de message de commit (ex. Co-Authored-By: ...)
    [string]$Trailer
)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)

if (git status --porcelain) {
    git add -A
    Write-Host "Modifications en cours incluses dans la release." -ForegroundColor Yellow
}

$old = (Get-Content VERSION -Raw).Trim()
$p = $old.Split('.') | ForEach-Object { [int]$_ }
switch ($Bump) {
    'major' { $p = @(($p[0] + 1), 0, 0) }
    'minor' { $p = @($p[0], ($p[1] + 1), 0) }
    'patch' { $p = @($p[0], $p[1], ($p[2] + 1)) }
}
$new = $p -join '.'
if ($new -notmatch '^\d+\.\d+\.\d+$') { throw "Version calculée invalide : $new" }
if (git tag --list "v$new") { throw "Le tag v$new existe déjà." }

$utf8 = New-Object System.Text.UTF8Encoding $false
[IO.File]::WriteAllText("$PWD\VERSION", "$new`n", $utf8)

$date = Get-Date -Format 'yyyy-MM-dd'
$section = "## [$new] - $date`n" + (($Notes | ForEach-Object { "- $_" }) -join "`n") + "`n`n"
$log = [IO.File]::ReadAllText("$PWD\CHANGELOG.md")
$marker = $log.IndexOf('## [')
$log = if ($marker -ge 0) { $log.Insert($marker, $section) } else { $log + "`n" + $section }
[IO.File]::WriteAllText("$PWD\CHANGELOG.md", $log, $utf8)

git add -A
$msg = @("-m", "Release v$new", "-m", (($Notes | ForEach-Object { "- $_" }) -join "`n"))
if ($Trailer) { $msg += @("-m", $Trailer) }
git commit @msg
git tag -a "v$new" -m "Orbiter $new"
Write-Host "Version $old -> $new (tag v$new)" -ForegroundColor Green

if ($Push) {
    $branch = git rev-parse --abbrev-ref HEAD
    git push origin $branch
    git push origin "v$new"
    Write-Host "Poussé : la Release GitHub v$new sera construite automatiquement." -ForegroundColor Green
}
