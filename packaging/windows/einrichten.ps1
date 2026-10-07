<#
.SYNOPSIS
    Registriert ImageConverter im Windows-Rechtsklick-Menue fuer Bilddateien.

.DESCRIPTION
    Schreibt nur nach HKCU (aktueller Benutzer) - keine Adminrechte noetig.
    Legt pro Bildformat einen Eintrag unter
    HKCU:\Software\Classes\SystemFileAssociations\<ext>\shell\ImageConverter an,
    ausserdem eine "Oeffnen mit"-Registrierung unter Applications.

.PARAMETER Uninstall
    Entfernt alle Eintraege wieder.

.EXAMPLE
    .\einrichten.ps1
    .\einrichten.ps1 -Uninstall
#>
[CmdletBinding()]
param([switch]$Uninstall)

$ErrorActionPreference = 'Stop'

$appId   = 'ImageConverter'
$exeName = 'ImageConverter.exe'
$exePath = Join-Path $PSScriptRoot $exeName

$extensions = @(
    '.jpg', '.jpeg', '.jpe', '.png', '.bmp', '.gif', '.tif', '.tiff',
    '.webp', '.avif', '.jxl', '.heic', '.heif', '.ico',
    '.cr2', '.nef', '.arw', '.dng'
)

$appsKey = "HKCU:\Software\Classes\Applications\$exeName"

function Remove-Registration {
    foreach ($ext in $extensions) {
        $key = "HKCU:\Software\Classes\SystemFileAssociations\$ext\shell\$appId"
        if (Test-Path $key) {
            Remove-Item $key -Recurse -Force
            Write-Host "entfernt: $ext"
        }
    }
    if (Test-Path $appsKey) {
        Remove-Item $appsKey -Recurse -Force
        Write-Host "entfernt: Oeffnen-mit-Eintrag"
    }
}

if ($Uninstall) {
    Write-Host 'ImageConverter aus dem Kontextmenue entfernen...'
    Remove-Registration
    Write-Host ''
    Write-Host 'Fertig. Explorer neu starten, damit das Menue aktualisiert wird.'
    return
}

if (-not (Test-Path $exePath)) {
    throw "$exeName liegt nicht neben diesem Skript ($PSScriptRoot)."
}

Write-Host "Registriere $exePath ..."

# --- Kontextmenue pro Dateityp ---------------------------------------------
foreach ($ext in $extensions) {
    $shellKey = "HKCU:\Software\Classes\SystemFileAssociations\$ext\shell\$appId"
    $cmdKey   = "$shellKey\command"

    New-Item -Path $cmdKey -Force | Out-Null
    # MUIVerb ist der Text im Kontextmenue.
    Set-ItemProperty -Path $shellKey -Name 'MUIVerb' -Value 'Mit ImageConverter oeffnen'
    Set-ItemProperty -Path $shellKey -Name 'Icon'    -Value "`"$exePath`",0"
    # %1 = angeklickte Datei. Mehrfachauswahl liefert Windows als
    # mehrere Aufrufe; die App sammelt sie selbst ein.
    Set-ItemProperty -Path $cmdKey -Name '(default)' -Value "`"$exePath`" `"%1`""
    Write-Host "  $ext"
}

# --- "Oeffnen mit"-Liste ----------------------------------------------------
$cmdKey = "$appsKey\shell\open\command"
New-Item -Path $cmdKey -Force | Out-Null
Set-ItemProperty -Path $appsKey -Name 'FriendlyAppName' -Value 'ImageConverter'
Set-ItemProperty -Path $cmdKey  -Name '(default)' -Value "`"$exePath`" `"%1`""

$typesKey = "$appsKey\SupportedTypes"
New-Item -Path $typesKey -Force | Out-Null
foreach ($ext in $extensions) {
    Set-ItemProperty -Path $typesKey -Name $ext -Value ''
}

Write-Host ''
Write-Host 'Fertig. ImageConverter steht jetzt im Rechtsklick-Menue von Bilddateien.'
Write-Host 'Wenn der Eintrag nicht erscheint, Explorer neu starten:'
Write-Host '    Stop-Process -Name explorer'
Write-Host ''
Write-Host 'Rueckgaengig: .\einrichten.ps1 -Uninstall'
