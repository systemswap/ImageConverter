#!/usr/bin/env bash
set -Eeuo pipefail

# Erstellt die fertigen Programme in published/:
#
#   published/linux-x64/ImageConverter-x86_64.AppImage   eine Datei: Programm, .NET und Avalonia
#   published/linux-x64/Tools/ImageMagick/               ImageMagick (Bildformate) mit Lizenztexten
#   published/linux-x64/Tools/OnnxRuntime/               ONNX Runtime (KI) mit Lizenztexten
#   published/windows-x64/ImageConverter.exe             eine Datei: Programm, .NET und Avalonia
#   published/windows-x64/Tools/…                        wie unter Linux
#   published/ImageConverter-linux-x64.tar.gz, ImageConverter-windows-x64.zip
#
# Fremde native Bibliotheken liegen bewusst nicht in der Programmdatei, sondern
# je in einem eigenen Ordner daneben (Build-Target in ImageConverter.Desktop.csproj,
# gefunden über Core/Platform/NativeLibraries.cs). Neben jedes Programm kommen
# LICENSE, THIRD-PARTY-NOTICES.md und licenses/, unter Linux außerdem
# einrichten.sh / entfernen.sh für Menüeintrag und „Öffnen mit …“.
# Nach einem Linux-Bau trägt das Skript das Programm im Menü ein.
#
# Paketform:
#   eine Datei (Standard)  Linux AppImage, Windows EXE; .NET und Avalonia darin
#   einzeln                alle Bibliotheken lose im Ordner, ohne AppImage –
#                          falls eine Datei zu groß wird oder zur Fehlersuche
#
# Heruntergeladene KI-Modelle (KI-Modelle/) und eigene Sprachdateien (languages/)
# neben dem Programm bleiben bei jedem Bau stehen; die Modelle kommen nicht in die
# Pakete, die Sprachdateien schon. Alles andere wird ersetzt. Ein Linux-Bau lässt
# den Windows-Stand stehen und umgekehrt.
#
# Liegt eine lokale Sicherung der Modelle in tools/KI-Models/<Modell>/ (gleicher
# Aufbau wie KI-Modelle/, nicht im Git), kommt sie neben jedes gebaute Programm:
# kopiert wird nur, was dort fehlt oder in der Sicherung neuer ist.
#
# Aufruf ohne Argumente fragt erst nach der Paketform, dann nach dem Ziel.
# Ohne Rückfrage (Reihenfolge egal, Paketform sonst „eine Datei“):
#   ./publish.sh beides
#   ./publish.sh linux
#   ./publish.sh windows
#   ./publish.sh linux einzeln
#
# Benötigt: .NET SDK 10, wget (lädt appimagetool beim ersten Mal), zip

# Keine Nutzungsdaten an Microsoft (.NET, ONNX Runtime) oder Avalonia senden, auch wenn es
# im System nicht abgeschaltet ist.
export DOTNET_CLI_TELEMETRY_OPTOUT=1 AVALONIA_TELEMETRY_OPTOUT=1 ORT_DISABLE_TELEMETRY=1 DO_NOT_TRACK=1
export DOTNET_NOLOGO=1

PROJECT_DIR="$(cd "$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")" && pwd)"
PROJECT="$PROJECT_DIR/ImageConverter.Desktop/ImageConverter.Desktop.csproj"
SOLUTION="$PROJECT_DIR/ImageConverter.sln"
OUT_DIR="$PROJECT_DIR/published"
LINUX_DIR="$OUT_DIR/linux-x64"
WINDOWS_DIR="$OUT_DIR/windows-x64"
CACHE_DIR="${XDG_CACHE_HOME:-$HOME/.cache}/imageconverter-build"
WORK_DIR="$CACHE_DIR/publish"
ARTIFACTS="$CACHE_DIR/artifacts"
APPIMAGETOOL="$CACHE_DIR/appimagetool-x86_64.AppImage"
NUGET_DIR="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
ICON="$PROJECT_DIR/ImageConverter.Ui/Assets/Icon.png"

TARGET=""
FORM=""
for argument in "$@"; do
  case "$argument" in
    linux|windows) TARGET="$argument" ;;
    beides|all) TARGET=all ;;
    eine-datei|datei|standard) FORM=eine-datei ;;
    einzeln|ordner) FORM=einzeln ;;
    *) echo "Aufruf: $0 [linux|windows|beides] [eine-datei|einzeln]" >&2; exit 1 ;;
  esac
done

[ -n "$FORM" ] || [ -z "$TARGET" ] || FORM=eine-datei

if [ -z "$FORM" ]; then
  echo
  echo "Wie soll das Paket aussehen?"
  echo "  1) Eine Datei (Standard) – Linux: AppImage, Windows: EXE; .NET und Avalonia darin"
  echo "  2) Einzeln               – alle Bibliotheken lose im Ordner"
  echo
  read -r -p "Auswahl [1]: " selection
  case "${selection:-1}" in
    1) FORM=eine-datei ;;
    2) FORM=einzeln ;;
    *) echo "Fehler: Ungültige Auswahl." >&2; exit 1 ;;
  esac
fi

if [ -z "$TARGET" ]; then
  echo
  echo "Wofür soll gebaut werden?"
  echo "  1) Linux"
  echo "  2) Windows"
  echo "  3) beides"
  echo
  read -r -p "Auswahl [3]: " selection
  case "${selection:-3}" in
    1) TARGET=linux ;;
    2) TARGET=windows ;;
    3) TARGET=all ;;
    *) echo "Fehler: Ungültige Auswahl." >&2; exit 1 ;;
  esac
fi

if [ "$FORM" = eine-datei ]; then SINGLE_FILE=true; else SINGLE_FILE=false; fi
echo "Paketform: $FORM, Ziel: $TARGET"

command -v dotnet > /dev/null || { echo "Fehler: .NET SDK fehlt." >&2; exit 1; }

# Nicht, solange das Programm aus published/ läuft: Es wandelt vielleicht gerade um.
if pgrep -f "$LINUX_DIR/ImageConverter(-x86_64\.AppImage)?( |$)" > /dev/null; then
  echo "Fehler: ImageConverter läuft gerade (aus published/) – bitte erst beenden, dann neu bauen." >&2
  exit 1
fi

VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PROJECT_DIR/Directory.Build.props" | head -1)"
package_version() { sed -n "s:.*Include=\"$1\" Version=\"\([^\"]*\)\".*:\1:p" "$PROJECT_DIR/Directory.Packages.props" | head -1; }
MAGICK_VERSION="$(package_version Magick.NET-Q16-OpenMP-x64)"
ONNX_VERSION="$(package_version Microsoft.ML.OnnxRuntime)"
echo "ImageConverter $VERSION"

rm -rf "$WORK_DIR"
mkdir -p "$WORK_DIR" "$OUT_DIR"

# Ein kaputter Stand soll nicht in published/ landen.
echo "== Tests"
dotnet test "$SOLUTION" -c Release -p:UseArtifactsOutput=true -p:ArtifactsPath="$ARTIFACTS" > "$WORK_DIR/test.log" 2>&1 \
  || { tail -30 "$WORK_DIR/test.log" >&2; echo "Fehler: Tests fehlgeschlagen – nichts veröffentlicht." >&2; exit 1; }
grep -E "Bestanden!|Passed!" "$WORK_DIR/test.log" | sed 's/^/   /' || true

# Lizenz, Hinweise auf fremde Bestandteile und deren Lizenztexte neben das Programm.
notices() {
  local dest="$1"

  install -m 0644 "$PROJECT_DIR/LICENSE" "$dest/LICENSE.txt"
  install -m 0644 "$PROJECT_DIR/THIRD-PARTY-NOTICES.md" "$dest/THIRD-PARTY-NOTICES.md"
  rm -rf "$dest/licenses"
  mkdir -p "$dest/licenses"
  install -m 0644 "$PROJECT_DIR/packaging/licenses/"* "$dest/licenses/"

  # Lizenztexte der mitgelieferten Bibliotheken genau aus den verwendeten Paketen
  install -m 0644 "$NUGET_DIR/magick.net-q16-openmp-x64/$MAGICK_VERSION/Notice.txt" "$dest/Tools/ImageMagick/Notice.txt"
  # Windows: ONNX Runtime mit DirectML (Grafikkarte) — Lizenzen aus diesem Paket, dazu die von DirectML selbst
  local onnx_package=microsoft.ml.onnxruntime
  [ -f "$dest/Tools/OnnxRuntime/DirectML.dll" ] && onnx_package=microsoft.ml.onnxruntime.directml
  install -m 0644 "$NUGET_DIR/$onnx_package/$ONNX_VERSION/LICENSE" "$dest/Tools/OnnxRuntime/LICENSE"
  install -m 0644 "$NUGET_DIR/$onnx_package/$ONNX_VERSION/ThirdPartyNotices.txt" "$dest/Tools/OnnxRuntime/ThirdPartyNotices.txt"
  if [ "$onnx_package" != microsoft.ml.onnxruntime ]; then
    local dml
    dml="$(ls -d "$NUGET_DIR"/microsoft.ai.directml/*/ | sort -V | tail -1)"
    install -m 0644 "${dml}LICENSE.txt" "$dest/Tools/OnnxRuntime/DirectML-LICENSE.txt"
  fi
}

# Was das Programm neben sich schreibt; bleibt bei jedem Bau stehen.
USER_FILES=(KI-Modelle languages ImageConverter.log ImageConverter.log.alt)

# Leert einen Ausgabeordner bis auf die Dateien des Benutzers.
clear_program_folder() {
  local dest="$1" keep=() name
  mkdir -p "$dest"
  for name in "${USER_FILES[@]}"; do keep+=(! -name "$name"); done
  find "$dest" -mindepth 1 -maxdepth 1 "${keep[@]}" -exec rm -rf -- {} +
}

# Eine eigenständige Programmdatei für eine Plattform (bei „einzeln“ ein Ordner
# mit allen Bibliotheken), die fremden nativen Bibliotheken in Tools/.
publish() {
  local rid="$1" dest="$2"

  dotnet publish "$PROJECT" -c Release -r "$rid" --self-contained true \
    -p:PublishSingleFile=$SINGLE_FILE \
    -p:IncludeNativeLibrariesForSelfExtract=$SINGLE_FILE \
    -p:EnableCompressionInSingleFile=$SINGLE_FILE \
    -p:DebugType=None -p:DebugSymbols=false \
    -p:UseArtifactsOutput=true -p:ArtifactsPath="$ARTIFACTS" \
    -o "$dest" > "$WORK_DIR/publish-$rid.log" 2>&1 \
    || { tail -20 "$WORK_DIR/publish-$rid.log" >&2; echo "Fehler: dotnet publish für $rid fehlgeschlagen." >&2; exit 1; }
  find "$dest" -name '*.pdb' -delete
  [ -d "$dest/Tools/ImageMagick" ] && [ -d "$dest/Tools/OnnxRuntime" ] \
    || { echo "Fehler: Tools/ImageMagick oder Tools/OnnxRuntime fehlt in der Ausgabe für $rid." >&2; exit 1; }
  # Windows: ohne passende DirectML.dll nähme Windows die ältere aus System32 — die KI liefe nur auf der CPU
  if [ "$rid" = win-x64 ] && [ ! -f "$dest/Tools/OnnxRuntime/DirectML.dll" ]; then
    echo "Fehler: DirectML.dll fehlt in Tools/OnnxRuntime der Windows-Ausgabe." >&2; exit 1
  fi
}

# Modelle aus der lokalen Sicherung neben das Programm: nur fehlende und neuere Dateien
# (Änderungszeit), über eine .part-Datei, damit ein Abbruch keine halbe Datei hinterlässt.
MODEL_BACKUP="$PROJECT_DIR/tools/KI-Models"
bundle_models() {
  local dest="$1/KI-Modelle" src rel target copied=0
  [ -d "$MODEL_BACKUP" ] || return 0
  while IFS= read -r -d '' src; do
    rel="${src#"$MODEL_BACKUP"/}"
    target="$dest/$rel"
    if [ ! -e "$target" ] || [ "$src" -nt "$target" ]; then
      mkdir -p "$(dirname "$target")"
      cp -p "$src" "$target.part"
      mv -f "$target.part" "$target"
      copied=$((copied + 1))
    fi
  done < <(find "$MODEL_BACKUP" -type f ! -name '*.part' -print0)
  echo "   KI-Modelle aus tools/KI-Models: $copied Datei(en) kopiert"
}

# Paket ohne die heruntergeladenen KI-Modelle und ohne das Log.
package() {
  local folder="$1" archive="$2"
  rm -f "$OUT_DIR/$archive"
  case "$archive" in
    *.tar.gz) tar -C "$OUT_DIR" --exclude="$folder/KI-Modelle" --exclude="$folder/ImageConverter.log*" -czf "$OUT_DIR/$archive" "$folder" ;;
    *.zip) (cd "$OUT_DIR" && zip -qr "$archive" "$folder" -x "$folder/KI-Modelle/*" "$folder/ImageConverter.log*") ;;
  esac
}

# ---------------------------------------------------------------- Linux
if [ "$TARGET" = all ] || [ "$TARGET" = linux ]; then
  echo "== Linux ($FORM)"
  publish linux-x64 "$WORK_DIR/linux"

  # ImageMagick (Mehrkern) braucht libgomp.so.1 — meist installiert; als Rückfall die dieses Rechners daneben legen
  # (NativeLibraries nimmt zuerst die des Systems). GPL-3.0 mit GCC-Laufzeit-Ausnahme, Texte in licenses/.
  magick_so="$(ls "$WORK_DIR/linux/Tools/ImageMagick/"Magick.Native-*.so 2>/dev/null | head -1)"
  gomp="$( [ -n "$magick_so" ] && ldd "$magick_so" | awk '/libgomp\.so\.1/ {print $3}' )"
  if [ -n "$gomp" ] && [ -f "$gomp" ]; then
    install -m 0644 "$(readlink -f "$gomp")" "$WORK_DIR/linux/Tools/ImageMagick/libgomp.so.1"
  else
    echo "Warnung: libgomp.so.1 nicht gefunden – nicht mitgeliefert (Debian: libgomp1, Fedora: libgomp)." >&2
  fi

  clear_program_folder "$LINUX_DIR"

  if [ "$SINGLE_FILE" = true ]; then
    APPDIR="$WORK_DIR/AppDir"
    mkdir -p "$APPDIR/usr/bin"
    install -m 0755 "$WORK_DIR/linux/ImageConverter" "$APPDIR/usr/bin/ImageConverter"
    install -m 0644 "$PROJECT_DIR/packaging/linux/ImageConverter.desktop" "$APPDIR/ImageConverter.desktop"
    install -m 0644 "$ICON" "$APPDIR/imageconverter.png"

    # ORT_DISABLE_TELEMETRY schon hier: Die ONNX Runtime sendet sonst Nutzungsdaten an
    # Microsoft. Das Programm setzt es beim Start noch einmal (NativeLibraries).
    cat > "$APPDIR/AppRun" << 'APPRUN'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
export ORT_DISABLE_TELEMETRY=1
exec "$HERE/usr/bin/ImageConverter" "$@"
APPRUN
    chmod 0755 "$APPDIR/AppRun"

    if [ ! -x "$APPIMAGETOOL" ]; then
      mkdir -p "$CACHE_DIR"
      wget -nv -O "$APPIMAGETOOL" "https://github.com/AppImage/appimagetool/releases/download/1.9.1/appimagetool-x86_64.AppImage"
      chmod +x "$APPIMAGETOOL"
    fi

    ARCH=x86_64 "$APPIMAGETOOL" --no-appstream "$APPDIR" "$LINUX_DIR/ImageConverter-x86_64.AppImage" > "$WORK_DIR/appimagetool.log" 2>&1 \
      || { tail -20 "$WORK_DIR/appimagetool.log" >&2; echo "Fehler: appimagetool fehlgeschlagen." >&2; exit 1; }
    cp -a "$WORK_DIR/linux/Tools" "$LINUX_DIR/"
  else
    cp -a "$WORK_DIR/linux/." "$LINUX_DIR/"
    chmod 0755 "$LINUX_DIR/ImageConverter"
  fi

  notices "$LINUX_DIR"

  # Menüeintrag und „Öffnen mit …“ (GNOME zeigt das Symbol in der Leiste nur mit einem Eintrag)
  install -Dm644 "$ICON" "$LINUX_DIR/share/icons/hicolor/256x256/apps/imageconverter.png"
  install -Dm644 "$PROJECT_DIR/packaging/linux/ImageConverter.desktop" "$LINUX_DIR/share/applications/ImageConverter.desktop"
  install -m 0755 "$PROJECT_DIR/packaging/linux/einrichten.sh" "$PROJECT_DIR/packaging/linux/entfernen.sh" "$LINUX_DIR/"

  package linux-x64 ImageConverter-linux-x64.tar.gz
  bundle_models "$LINUX_DIR"
fi

# ---------------------------------------------------------------- Windows
if [ "$TARGET" = all ] || [ "$TARGET" = windows ]; then
  echo "== Windows ($FORM)"
  publish win-x64 "$WORK_DIR/windows"

  clear_program_folder "$WINDOWS_DIR"
  if [ "$SINGLE_FILE" = true ]; then
    install -m 0644 "$WORK_DIR/windows/ImageConverter.exe" "$WINDOWS_DIR/ImageConverter.exe"
    cp -a "$WORK_DIR/windows/Tools" "$WINDOWS_DIR/"
  else
    cp -a "$WORK_DIR/windows/." "$WINDOWS_DIR/"
  fi
  notices "$WINDOWS_DIR"
  # Rechtsklick „Mit ImageConverter öffnen“ im Explorer (nur für den angemeldeten Benutzer)
  install -m 0644 "$PROJECT_DIR/packaging/windows/einrichten.ps1" "$WINDOWS_DIR/einrichten.ps1"

  package windows-x64 ImageConverter-windows-x64.zip
  bundle_models "$WINDOWS_DIR"
fi

echo
echo "Fertig (ImageConverter $VERSION):"
find "$OUT_DIR" -maxdepth 4 -type f \( -name "*.AppImage" -o -name "*.exe" -o -name ImageConverter -o -name "Magick.Native*" -o -name "*onnxruntime.*" -o -name "*.tar.gz" -o -name "*.zip" \) -printf "  %P  (%s Bytes)\n" | sort

# Menüeintrag auf das neue Programm setzen (fester Pfad).
if [ "$TARGET" = all ] || [ "$TARGET" = linux ]; then
  echo
  "$LINUX_DIR/einrichten.sh"
fi
