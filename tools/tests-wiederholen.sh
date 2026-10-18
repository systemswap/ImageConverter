#!/usr/bin/env bash
# Tests im Release-Modus mehrmals hintereinander — seltene Abstürze (z. B. bei gleichzeitigen Aufrufen nativer
# Bibliotheken) fallen in einem einzelnen Lauf nicht auf. Hält beim ersten Fehler an; bei einem Absturz schreibt .NET
# einen Bericht mit Aufrufstapel (*.crashreport.json) in den Ordner unten.
#
#   tools/tests-wiederholen.sh        10 Läufe
#   tools/tests-wiederholen.sh 25
set -Eeuo pipefail
export DOTNET_CLI_TELEMETRY_OPTOUT=1 AVALONIA_TELEMETRY_OPTOUT=1 ORT_DISABLE_TELEMETRY=1 DO_NOT_TRACK=1 DOTNET_NOLOGO=1

PROJECT_DIR="$(cd "$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")/.." && pwd)"
CACHE_DIR="${XDG_CACHE_HOME:-$HOME/.cache}/imageconverter-build"
ARTIFACTS="$CACHE_DIR/artifacts"
REPORTS="$CACHE_DIR/tests-wiederholen"
RUNS="${1:-10}"
mkdir -p "$REPORTS"

dotnet build "$PROJECT_DIR/ImageConverter.sln" -c Release -p:UseArtifactsOutput=true -p:ArtifactsPath="$ARTIFACTS" > "$REPORTS/build.log" 2>&1 \
  || { tail -20 "$REPORTS/build.log" >&2; exit 1; }

for i in $(seq 1 "$RUNS"); do
  if DOTNET_DbgEnableMiniDump=1 DOTNET_DbgMiniDumpType=1 DOTNET_EnableCrashReport=1 DOTNET_DbgMiniDumpName="$REPORTS/absturz-$i-%p.dmp" \
     dotnet test "$PROJECT_DIR/ImageConverter.sln" -c Release --no-build -p:UseArtifactsOutput=true -p:ArtifactsPath="$ARTIFACTS" \
     > "$REPORTS/lauf-$i.log" 2>&1; then
    echo "Lauf $i von $RUNS: $(grep -E 'Bestanden!|Passed!' "$REPORTS/lauf-$i.log" | sed 's/ - .*//')"
  else
    echo "Lauf $i von $RUNS: FEHLER – siehe $REPORTS/lauf-$i.log und absturz-$i-*.crashreport.json" >&2
    exit 1
  fi
done
rm -f "$REPORTS"/lauf-*.log
echo "Alle $RUNS Läufe fehlerfrei."
