<#
.SYNOPSIS
    Baut ImageConverter unter Windows nach .\published\ - Pendant zu publish.sh.

.DESCRIPTION
    Legt dieselben Ordner an wie publish.sh:

        published\windows-x64\   ImageConverter.exe   eine Datei: Programm, .NET und Avalonia
                                 Tools\ImageMagick\   ImageMagick mit Lizenztexten
                                 Tools\OnnxRuntime\   ONNX Runtime mit Lizenztexten
                                 einrichten.ps1       Rechtsklick "Mit ImageConverter oeffnen"
        published\linux-x64\     ImageConverter       Linux-Programm als eine Datei (ohne AppImage -
                                                      die baut nur publish.sh unter Linux)
        published\ImageConverter-windows-x64.zip

    Fremde native Bibliotheken liegen nicht in der Programmdatei, sondern je in einem
    eigenen Ordner daneben (Build-Target in ImageConverter.Desktop.csproj). Heruntergeladene
    KI-Modelle (KI-Modelle\) und eigene Sprachdateien (languages\) neben dem Programm bleiben
    bei jedem Bau stehen; die Modelle kommen nicht ins Paket, die Sprachdateien schon.
    Jedes Ziel loescht nur seine eigene Ausgabe.

.PARAMETER Target
    windows (Standard), linux oder beides.

.PARAMETER Einzeln
    Alle Bibliotheken lose im Ordner statt einer Datei (zur Fehlersuche).

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -Target beides
#>
[CmdletBinding()]
param(
    [ValidateSet('windows', 'linux', 'beides')]
    [string]$Target = 'windows',

    [switch]$Einzeln
)

$ErrorActionPreference = 'Stop'

# Keine Nutzungsdaten an Microsoft (.NET, ONNX Runtime) oder Avalonia senden.
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$env:ORT_DISABLE_TELEMETRY = '1'
$env:DO_NOT_TRACK = '1'
$env:DOTNET_NOLOGO = '1'

$root      = $PSScriptRoot
$project   = Join-Path $root 'ImageConverter.Desktop\ImageConverter.Desktop.csproj'
$solution  = Join-Path $root 'ImageConverter.sln'
$outDir    = Join-Path $root 'published'
$cacheDir  = Join-Path $env:LOCALAPPDATA 'imageconverter-build'
$workDir   = Join-Path $cacheDir 'publish'
$artifacts = Join-Path $cacheDir 'artifacts'
$nugetDir  = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$single    = if ($Einzeln) { 'false' } else { 'true' }

function Write-Step([string]$text) {
    Write-Host ''
    Write-Host "==> $text" -ForegroundColor Cyan
}

[xml]$props    = Get-Content (Join-Path $root 'Directory.Build.props')
[xml]$packages = Get-Content (Join-Path $root 'Directory.Packages.props')
$version = ([string]($props.Project.PropertyGroup.Version | Where-Object { $_ })).Trim()
function Get-PackageVersion([string]$name) {
    ($packages.Project.ItemGroup.PackageVersion | Where-Object { $_.Include -eq $name }).Version
}
$magickVersion = Get-PackageVersion 'Magick.NET-Q16-AnyCPU'
$onnxVersion   = Get-PackageVersion 'Microsoft.ML.OnnxRuntime'

Write-Step "ImageConverter $version"

if (Test-Path $workDir) { Remove-Item $workDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $workDir, $outDir | Out-Null

# --- Tests zuerst: ein kaputter Stand soll nicht im published-Ordner landen ---
Write-Step 'Tests'
dotnet test $solution -c Release --nologo -p:UseArtifactsOutput=true "-p:ArtifactsPath=$artifacts"
if ($LASTEXITCODE -ne 0) { throw 'Tests fehlgeschlagen - nichts veroeffentlicht.' }

# Leert einen Ausgabeordner bis auf die Dateien des Benutzers: KI-Modelle und Sprachdateien.
function Clear-ProgramFolder([string]$dest) {
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Get-ChildItem $dest -Force | Where-Object { $_.Name -notin @('KI-Modelle', 'languages') } | Remove-Item -Recurse -Force
}

function Publish-Runtime([string]$rid, [string]$dest) {
    dotnet publish $project -c Release -r $rid --self-contained true --nologo `
        "-p:PublishSingleFile=$single" `
        "-p:IncludeNativeLibrariesForSelfExtract=$single" `
        "-p:EnableCompressionInSingleFile=$single" `
        -p:DebugType=None -p:DebugSymbols=false `
        -p:UseArtifactsOutput=true "-p:ArtifactsPath=$artifacts" `
        -o $dest
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish fuer $rid fehlgeschlagen." }
    Get-ChildItem $dest -Recurse -Filter *.pdb | Remove-Item -Force
}

# Lizenz, Hinweise auf fremde Bestandteile und deren Lizenztexte neben das Programm.
function Copy-Notices([string]$dest) {
    Copy-Item (Join-Path $root 'LICENSE') (Join-Path $dest 'LICENSE.txt') -Force
    Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $dest -Force
    $licenses = Join-Path $dest 'licenses'
    if (Test-Path $licenses) { Remove-Item $licenses -Recurse -Force }
    Copy-Item (Join-Path $root 'packaging\licenses') $licenses -Recurse
    Copy-Item (Join-Path $nugetDir "magick.net-q16-anycpu\$magickVersion\Notice.txt") (Join-Path $dest 'Tools\ImageMagick\') -Force
    Copy-Item (Join-Path $nugetDir "microsoft.ml.onnxruntime\$onnxVersion\LICENSE") (Join-Path $dest 'Tools\OnnxRuntime\') -Force
    Copy-Item (Join-Path $nugetDir "microsoft.ml.onnxruntime\$onnxVersion\ThirdPartyNotices.txt") (Join-Path $dest 'Tools\OnnxRuntime\') -Force
}

# ------------------------------------------------------------------ Windows
if ($Target -in @('windows', 'beides')) {
    $winDir = Join-Path $outDir 'windows-x64'
    Write-Step "Windows (win-x64) -> $winDir"
    $build = Join-Path $workDir 'windows'
    Publish-Runtime 'win-x64' $build
    Clear-ProgramFolder $winDir
    Copy-Item (Join-Path $build '*') $winDir -Recurse -Force
    Copy-Notices $winDir
    Copy-Item (Join-Path $root 'packaging\windows\einrichten.ps1') $winDir -Force

    $zip = Join-Path $outDir 'ImageConverter-windows-x64.zip'
    if (Test-Path $zip) { Remove-Item $zip -Force }
    $items = Get-ChildItem $winDir | Where-Object { $_.Name -ne 'KI-Modelle' }
    Compress-Archive -Path $items.FullName -DestinationPath $zip
}

# -------------------------------------------------------------------- Linux
if ($Target -in @('linux', 'beides')) {
    $linuxDir = Join-Path $outDir 'linux-x64'
    Write-Step "Linux (linux-x64, ohne AppImage) -> $linuxDir"
    $build = Join-Path $workDir 'linux'
    Publish-Runtime 'linux-x64' $build
    Clear-ProgramFolder $linuxDir
    Copy-Item (Join-Path $build '*') $linuxDir -Recurse -Force
    Copy-Notices $linuxDir

    $share = Join-Path $linuxDir 'share'
    New-Item -ItemType Directory -Force -Path "$share\icons\hicolor\256x256\apps", "$share\applications" | Out-Null
    Copy-Item (Join-Path $root 'ImageConverter.Ui\Assets\Icon.png') "$share\icons\hicolor\256x256\apps\imageconverter.png" -Force
    # LF-Zeilenenden erzwingen, sonst scheitern Skript und Menueintrag unter Linux an \r
    foreach ($name in @('einrichten.sh', 'entfernen.sh', 'ImageConverter.desktop')) {
        $text = (Get-Content (Join-Path $root "packaging\linux\$name") -Raw) -replace "`r`n", "`n"
        $dest = if ($name -like '*.desktop') { "$share\applications\$name" } else { Join-Path $linuxDir $name }
        [System.IO.File]::WriteAllText($dest, $text, (New-Object System.Text.UTF8Encoding $false))
    }
    Write-Host 'Unter Linux: chmod +x ImageConverter einrichten.sh entfernen.sh; ./einrichten.sh'
}

Write-Step 'Fertig'
Get-ChildItem $outDir -Directory | ForEach-Object {
    $size = (Get-ChildItem $_.FullName -Recurse -File | Where-Object { $_.FullName -notlike '*\KI-Modelle\*' } | Measure-Object -Sum Length).Sum / 1MB
    Write-Host ("  {0,-12} {1,7:N1} MB" -f $_.Name, $size)
}
Write-Host ''
Write-Host "Ausgabe: $outDir"
