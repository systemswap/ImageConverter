#!/bin/sh
# Entfernt, was einrichten.sh angelegt hat. Keine Root-Rechte noetig.
set -eu

APP_ID="imageconverter"

DATA_HOME="${XDG_DATA_HOME:-$HOME/.local/share}"
INSTALL_DIR="$DATA_HOME/$APP_ID"
DESKTOP_FILE="$DATA_HOME/applications/$APP_ID.desktop"
ICON_FILE="$DATA_HOME/icons/hicolor/256x256/apps/$APP_ID.png"
WRAPPER="$HOME/.local/bin/$APP_ID"

removed=0
for path in "$DESKTOP_FILE" "$ICON_FILE" "$WRAPPER"; do
    if [ -e "$path" ]; then
        rm -f "$path"
        echo "entfernt: $path"
        removed=1
    fi
done

if [ -d "$INSTALL_DIR" ]; then
    rm -rf "$INSTALL_DIR"
    echo "entfernt: $INSTALL_DIR"
    removed=1
fi

if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database "$DATA_HOME/applications" 2>/dev/null || true
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
    gtk-update-icon-cache -f -t "$DATA_HOME/icons/hicolor" 2>/dev/null || true
fi

if [ "$removed" -eq 0 ]; then
    echo "Nichts zu entfernen - ImageConverter war nicht eingerichtet."
else
    echo
    echo "ImageConverter wurde entfernt."
fi

echo "Einstellungen unter ~/.config/$APP_ID bleiben erhalten."
