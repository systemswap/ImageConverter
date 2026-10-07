#!/bin/sh
# ImageConverter unter Linux einrichten.
#
# Legt eine .desktop-Datei an, damit die App im Dateimanager unter
# "Oeffnen mit" im Rechtsklick-Menue erscheint. Laeuft komplett im
# Benutzerverzeichnis - keine Root-Rechte, kein sudo.
#
#   ./einrichten.sh            installieren
#   ./einrichten.sh --here     nicht kopieren, Programm am aktuellen Ort verlinken
#
set -eu

APP_ID="imageconverter"
APP_NAME="ImageConverter"
BINARY="ImageConverter"

HERE=$(cd "$(dirname "$0")" && pwd)

DATA_HOME="${XDG_DATA_HOME:-$HOME/.local/share}"
INSTALL_DIR="$DATA_HOME/$APP_ID"
DESKTOP_DIR="$DATA_HOME/applications"
ICON_DIR="$DATA_HOME/icons/hicolor/256x256/apps"
BIN_DIR="$HOME/.local/bin"
DESKTOP_FILE="$DESKTOP_DIR/$APP_ID.desktop"

IN_PLACE=0
[ "${1:-}" = "--here" ] && IN_PLACE=1

if [ ! -f "$HERE/$BINARY" ]; then
    echo "Fehler: $BINARY liegt nicht neben diesem Skript ($HERE)." >&2
    exit 1
fi

# --- MIME-Typen, fuer die wir im Rechtsklick auftauchen wollen ---------------
# Lesbare Formate laut Konzept. image/jxl und image/avif kennen aeltere
# Dateimanager nicht - sie zu nennen schadet nicht.
MIMES="image/jpeg;image/png;image/bmp;image/gif;image/tiff;image/webp;image/avif;image/jxl;image/heif;image/heic;image/vnd.microsoft.icon;image/x-icon;image/svg+xml;image/x-canon-cr2;image/x-nikon-nef;image/x-sony-arw;image/x-adobe-dng;"

mkdir -p "$DESKTOP_DIR" "$ICON_DIR" "$BIN_DIR"

if [ "$IN_PLACE" -eq 1 ]; then
    TARGET_BIN="$HERE/$BINARY"
    ICON_SRC="$HERE/$APP_ID.png"
    echo "Programm bleibt an seinem Platz: $TARGET_BIN"
else
    mkdir -p "$INSTALL_DIR"
    cp -f "$HERE/$BINARY" "$INSTALL_DIR/$BINARY"
    [ -f "$HERE/$APP_ID.png" ] && cp -f "$HERE/$APP_ID.png" "$INSTALL_DIR/$APP_ID.png"
    TARGET_BIN="$INSTALL_DIR/$BINARY"
    ICON_SRC="$INSTALL_DIR/$APP_ID.png"
    echo "Programm kopiert nach: $TARGET_BIN"
fi

chmod +x "$TARGET_BIN"

# --- Icon ------------------------------------------------------------------
if [ -f "$ICON_SRC" ]; then
    cp -f "$ICON_SRC" "$ICON_DIR/$APP_ID.png"
    ICON_VALUE="$APP_ID"
else
    echo "Hinweis: kein Icon gefunden, es wird ein generisches verwendet."
    ICON_VALUE="image-x-generic"
fi

# --- Startskript im PATH ---------------------------------------------------
cat > "$BIN_DIR/$APP_ID" << WRAPPER
#!/bin/sh
exec "$TARGET_BIN" "\$@"
WRAPPER
chmod +x "$BIN_DIR/$APP_ID"

# --- .desktop --------------------------------------------------------------
# %F uebergibt mehrere markierte Dateien als Batch-Liste an die App.
cat > "$DESKTOP_FILE" << DESKTOP
[Desktop Entry]
Type=Application
Version=1.0
Name=$APP_NAME
GenericName=Image Converter
GenericName[de]=Bildkonverter
Comment=Convert, resize and crop images
Comment[de]=Bilder konvertieren, skalieren und zuschneiden
Exec=$TARGET_BIN %F
Icon=$ICON_VALUE
Terminal=false
Categories=Graphics;2DGraphics;RasterGraphics;
MimeType=$MIMES
Keywords=image;convert;resize;crop;scale;webp;avif;dpi;
Keywords[de]=Bild;konvertieren;skalieren;zuschneiden;umwandeln;
StartupNotify=true
StartupWMClass=ImageConverter
DESKTOP

chmod 644 "$DESKTOP_FILE"

# --- Caches aktualisieren --------------------------------------------------
# Ohne update-desktop-database kennt der Dateimanager die MIME-Zuordnung nicht.
if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database "$DESKTOP_DIR" 2>/dev/null || true
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
    gtk-update-icon-cache -f -t "$DATA_HOME/icons/hicolor" 2>/dev/null || true
fi
if command -v desktop-file-validate >/dev/null 2>&1; then
    desktop-file-validate "$DESKTOP_FILE" || \
        echo "Hinweis: desktop-file-validate meldet Auffaelligkeiten (meist harmlos)."
fi

echo
echo "Fertig."
echo "  Desktop-Datei : $DESKTOP_FILE"
echo "  Terminal      : $APP_ID"
echo
echo "ImageConverter steht jetzt im Dateimanager unter Rechtsklick > Oeffnen mit."
echo "Falls nicht: einmal abmelden und neu anmelden."

case ":$PATH:" in
    *":$BIN_DIR:"*) ;;
    *) echo
       echo "Hinweis: $BIN_DIR liegt nicht im PATH."
       echo "Fuer den Terminal-Aufruf in ~/.profile ergaenzen:"
       echo "    export PATH=\"\$HOME/.local/bin:\$PATH\"" ;;
esac
