<#
.SYNOPSIS
    Baut ImageConverter fuer Windows und Linux nach .\published\.

.DESCRIPTION
    Legt neben der Solution einen Ordner "published" an, darin je Zielplattform
    einen Unterordner:

        published\windows\   ImageConverter.exe   (portabel, self-contained)
        published\linux\     ImageConverter       (self-contained)
                             einrichten.sh        legt .desktop an -> Rechtsklick
                             deinstallieren.sh
                             imageconverter.png
                             LIESMICH.txt

.PARAMETER Target
    windows, linux oder all (Standard).

.PARAMETER Arch
    Nur fuer Linux: x64 (Standard) oder arm64.

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -Target linux -Arch arm64
#>
[CmdletBinding()]
param(
    [ValidateSet('all', 'windows', 'linux')]
    [string]$Target = 'all',

    [ValidateSet('x64', 'arm64')]
    [string]$Arch = 'x64',

    [switch]$KeepOld
)

$ErrorActionPreference = 'Stop'

$root      = $PSScriptRoot
$project   = Join-Path $root 'ImageConverter.Desktop\ImageConverter.Desktop.csproj'
$solution  = Join-Path $root 'ImageConverter.slnx'
$publishIn = Join-Path $root 'published'
$iconSrc   = Join-Path $root 'ImageConverter.Ui\Assets\Icon.png'

if (-not (Test-Path $project)) {
    throw "Projekt nicht gefunden: $project"
}

function Write-Step([string]$text) {
    Write-Host ''
    Write-Host "==> $text" -ForegroundColor Cyan
}

# --- Version aus Directory.Build.props (zentrale Stelle für alle Projekte) ---
$version = '0.0.0'
$propsXml = [xml](Get-Content (Join-Path $root 'Directory.Build.props'))
$versionNode = $propsXml.Project.PropertyGroup.Version | Where-Object { $_ }
if ($versionNode) { $version = ([string]$versionNode).Trim() }

Write-Step "ImageConverter $version"

# --- Tests zuerst: ein kaputter Build soll nicht im published-Ordner landen ---
Write-Step 'Tests'
dotnet test $solution -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Tests fehlgeschlagen - Publish abgebrochen.' }

New-Item -ItemType Directory -Force -Path $publishIn | Out-Null

# Nur die Ordner leeren, die dieser Lauf auch neu baut - sonst wuerde
# "-Target linux" das fertige Windows-Paket mitloeschen.
if (-not $KeepOld) {
    $targets = if ($Target -eq 'all') { @('windows', 'linux') } else { @($Target) }
    foreach ($name in $targets) {
        $dir = Join-Path $publishIn $name
        if (Test-Path $dir) {
            Write-Step "Alten Ordner entfernen: $name"
            Remove-Item $dir -Recurse -Force
        }
    }
}

function Publish-Runtime {
    param([string]$Rid, [string]$OutDir)

    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    dotnet publish $project `
        -c Release `
        -r $Rid `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:Version=$version `
        -o $OutDir `
        --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish fuer $Rid fehlgeschlagen." }
}

# ------------------------------------------------------------------ Windows
if ($Target -in @('all', 'windows')) {
    $winDir = Join-Path $publishIn 'windows'
    Write-Step "Windows (win-x64) -> $winDir"
    Publish-Runtime -Rid 'win-x64' -OutDir $winDir

    # Die .pdb braucht im Release-Paket niemand.
    Get-ChildItem $winDir -Filter *.pdb -ErrorAction SilentlyContinue | Remove-Item -Force

    $winTemplate = Join-Path $root 'packaging\windows\einrichten.ps1'
    if (-not (Test-Path $winTemplate)) { throw "Vorlage fehlt: $winTemplate" }
    Copy-Item $winTemplate (Join-Path $winDir 'einrichten.ps1') -Force

    @"
ImageConverter $version - Windows (portabel)
===========================================

ImageConverter.exe laeuft ohne Installation, .NET muss nicht vorhanden sein.

Rechtsklick-Integration ("Oeffnen mit ImageConverter"):

  Windows registriert "Oeffnen mit" automatisch, sobald du die exe einmal
  ueber Rechtsklick > Oeffnen mit > Andere App auswaehlen einem Bild
  zugeordnet hast.

  Dauerhaft fuer alle Bildtypen, ohne Adminrechte:

      .\einrichten.ps1

  Rueckgaengig:

      .\einrichten.ps1 -Uninstall
"@ | Set-Content (Join-Path $winDir 'LIESMICH.txt') -Encoding UTF8
}

# -------------------------------------------------------------------- Linux
if ($Target -in @('all', 'linux')) {
    $rid      = "linux-$Arch"
    $linuxDir = Join-Path $publishIn 'linux'
    Write-Step "Linux ($rid) -> $linuxDir"
    Publish-Runtime -Rid $rid -OutDir $linuxDir

    Get-ChildItem $linuxDir -Filter *.pdb -ErrorAction SilentlyContinue | Remove-Item -Force

    if (Test-Path $iconSrc) {
        Copy-Item $iconSrc (Join-Path $linuxDir 'imageconverter.png') -Force
    } else {
        Write-Warning "Icon nicht gefunden: $iconSrc - einrichten.sh laeuft trotzdem."
    }

    # Die Setup-Skripte liegen als Vorlage im Repo und werden mitkopiert.
    $templates = Join-Path $root 'packaging\linux'
    foreach ($name in @('einrichten.sh', 'deinstallieren.sh')) {
        $src = Join-Path $templates $name
        if (-not (Test-Path $src)) { throw "Vorlage fehlt: $src" }
        $dest = Join-Path $linuxDir $name
        # LF-Zeilenenden erzwingen, sonst scheitert /bin/sh an \r
        $text = (Get-Content $src -Raw) -replace "`r`n", "`n"
        [System.IO.File]::WriteAllText($dest, $text, (New-Object System.Text.UTF8Encoding $false))
    }

    @"
ImageConverter $version - Linux (self-contained)
===============================================

Kein .NET noetig. Erst ausfuehrbar machen, dann einrichten:

    chmod +x ImageConverter einrichten.sh deinstallieren.sh
    ./einrichten.sh

einrichten.sh legt an:

    ~/.local/share/imageconverter/           Programm und Icon
    ~/.local/share/applications/
        imageconverter.desktop               Rechtsklick > Oeffnen mit
    ~/.local/share/icons/hicolor/256x256/apps/imageconverter.png
    ~/.local/bin/imageconverter              Aufruf im Terminal

Danach erscheint ImageConverter in Nautilus/Dolphin/Thunar unter
"Oeffnen mit". Taucht es nicht auf, einmal abmelden und neu anmelden.

Entfernen:

    ./deinstallieren.sh
"@ | Set-Content (Join-Path $linuxDir 'LIESMICH.txt') -Encoding UTF8
}

Write-Step 'Fertig'
Get-ChildItem $publishIn -Directory | ForEach-Object {
    $size = (Get-ChildItem $_.FullName -Recurse -File | Measure-Object -Sum Length).Sum / 1MB
    Write-Host ("  {0,-10} {1,7:N1} MB" -f $_.Name, $size)
}
Write-Host ''
Write-Host "Ausgabe: $publishIn"
