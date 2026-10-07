#!/usr/bin/env bash
# Baut ImageConverter nach ./published/ - Pendant zu publish.ps1, fuer Linux.
#
#   ./publish.sh                 windows + linux
#   ./publish.sh linux           nur linux
#   ./publish.sh linux arm64     linux fuer arm64
#   ./publish.sh linux x64 --appimage   zusaetzlich ein AppImage bauen
#
set -euo pipefail

TARGET="${1:-all}"
ARCH="${2:-x64}"
WANT_APPIMAGE=0
for arg in "$@"; do
    [ "$arg" = "--appimage" ] && WANT_APPIMAGE=1
done

ROOT="$(cd "$(dirname "$0")" && pwd)"
PROJECT="$ROOT/ImageConverter.Desktop/ImageConverter.Desktop.csproj"
SOLUTION="$ROOT/ImageConverter.sln"
PUBLISHED="$ROOT/published"
TEMPLATES="$ROOT/packaging/linux"
ICON_SRC="$ROOT/ImageConverter.Ui/Assets/Icon.png"

VERSION=$(grep -oP '(?<=<Version>)[^<]+' "$ROOT/Directory.Build.props" | head -1 || echo "0.0.0")

step() { printf '\n==> %s\n' "$1"; }

step "ImageConverter $VERSION"

step "Tests"
dotnet test "$SOLUTION" -c Release --nologo

mkdir -p "$PUBLISHED"

# Nur die Ordner leeren, die dieser Lauf auch neu baut - sonst wuerde
# "./publish.sh linux" das fertige Windows-Paket mitloeschen.
if [ "$TARGET" = "all" ] || [ "$TARGET" = "windows" ]; then rm -rf "$PUBLISHED/windows"; fi
if [ "$TARGET" = "all" ] || [ "$TARGET" = "linux"   ]; then rm -rf "$PUBLISHED/linux";   fi

publish_rid() {
    local rid="$1" out="$2"
    mkdir -p "$out"
    dotnet publish "$PROJECT" \
        -c Release \
        -r "$rid" \
        --self-contained true \
        -p:PublishSingleFile=true \
        -p:Version="$VERSION" \
        -o "$out" \
        --nologo
    rm -f "$out"/*.pdb
}

# --------------------------------------------------------------- Windows
if [ "$TARGET" = "all" ] || [ "$TARGET" = "windows" ]; then
    WIN_DIR="$PUBLISHED/windows"
    step "Windows (win-x64) -> $WIN_DIR"
    publish_rid win-x64 "$WIN_DIR"
    cp -f "$ROOT/packaging/windows/einrichten.ps1" "$WIN_DIR/"
    cat > "$WIN_DIR/LIESMICH.txt" << TXT
ImageConverter $VERSION - Windows (portabel)

ImageConverter.exe laeuft ohne Installation.
Rechtsklick-Integration:  powershell -ExecutionPolicy Bypass -File einrichten.ps1
Rueckgaengig:             powershell -ExecutionPolicy Bypass -File einrichten.ps1 -Uninstall
TXT
fi

# ----------------------------------------------------------------- Linux
if [ "$TARGET" = "all" ] || [ "$TARGET" = "linux" ]; then
    RID="linux-$ARCH"
    LINUX_DIR="$PUBLISHED/linux"
    step "Linux ($RID) -> $LINUX_DIR"
    publish_rid "$RID" "$LINUX_DIR"

    [ -f "$ICON_SRC" ] && cp -f "$ICON_SRC" "$LINUX_DIR/imageconverter.png"
    cp -f "$TEMPLATES/einrichten.sh" "$TEMPLATES/deinstallieren.sh" "$LINUX_DIR/"
    chmod +x "$LINUX_DIR/ImageConverter" "$LINUX_DIR"/*.sh

    cat > "$LINUX_DIR/LIESMICH.txt" << TXT
ImageConverter $VERSION - Linux (self-contained, kein .NET noetig)

Einrichten:   ./einrichten.sh
Entfernen:    ./deinstallieren.sh

einrichten.sh legt die .desktop-Datei an, damit ImageConverter im
Dateimanager unter Rechtsklick > "Oeffnen mit" erscheint.
TXT

    # ------------------------------------------------------- AppImage
    if [ "$WANT_APPIMAGE" -eq 1 ]; then
        step "AppImage"
        if ! command -v appimagetool >/dev/null 2>&1; then
            echo "appimagetool nicht gefunden - AppImage uebersprungen." >&2
            echo "Download: https://github.com/AppImage/appimagetool/releases" >&2
        else
            APPDIR="$PUBLISHED/ImageConverter.AppDir"
            rm -rf "$APPDIR"
            mkdir -p "$APPDIR/usr/bin" \
                     "$APPDIR/usr/share/applications" \
                     "$APPDIR/usr/share/icons/hicolor/256x256/apps"

            cp -f "$LINUX_DIR/ImageConverter" "$APPDIR/usr/bin/"
            chmod +x "$APPDIR/usr/bin/ImageConverter"

            MIMES="image/jpeg;image/png;image/bmp;image/gif;image/tiff;image/webp;image/avif;image/jxl;image/heif;image/heic;image/vnd.microsoft.icon;image/svg+xml;"
            cat > "$APPDIR/imageconverter.desktop" << DESKTOP
[Desktop Entry]
Type=Application
Name=ImageConverter
GenericName=Image Converter
GenericName[de]=Bildkonverter
Comment=Convert, resize and crop images
Comment[de]=Bilder konvertieren, skalieren und zuschneiden
Exec=ImageConverter %F
Icon=imageconverter
Terminal=false
Categories=Graphics;2DGraphics;RasterGraphics;
MimeType=$MIMES
StartupWMClass=ImageConverter
DESKTOP
            cp "$APPDIR/imageconverter.desktop" "$APPDIR/usr/share/applications/"

            if [ -f "$ICON_SRC" ]; then
                cp -f "$ICON_SRC" "$APPDIR/imageconverter.png"
                cp -f "$ICON_SRC" "$APPDIR/usr/share/icons/hicolor/256x256/apps/imageconverter.png"
            fi

            cat > "$APPDIR/AppRun" << 'APPRUN'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/bin/ImageConverter" "$@"
APPRUN
            chmod +x "$APPDIR/AppRun"

            ARCH_LABEL=x86_64
            [ "$ARCH" = "arm64" ] && ARCH_LABEL=aarch64
            ARCH="$ARCH_LABEL" appimagetool "$APPDIR" \
                "$PUBLISHED/linux/ImageConverter-$VERSION-$ARCH_LABEL.AppImage"
            rm -rf "$APPDIR"
        fi
    fi
fi

step "Fertig"
du -sh "$PUBLISHED"/* 2>/dev/null || true
echo
echo "Ausgabe: $PUBLISHED"
