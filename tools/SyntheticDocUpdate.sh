#!/usr/bin/env bash
# Gibt es Neues bei SyntheticDoc? Vergleicht das Original-Repository (GitHub, Zweig main) mit dem Stand, den wir
# nach extern/SyntheticDoc kopiert haben (Prüfsummen in extern/SyntheticDoc/ORIGINAL.sha256 — so stören unsere
# eigenen Ergänzungen nicht), und prüft, ob die Gewichte bei der ETH sich geändert haben. Ändert nichts im Projekt.
# Aufruf (Linux oder Git Bash unter Windows, braucht curl, unzip, sha256sum):  tools/SyntheticDocUpdate.sh
# Rückgabe: 0 = nichts Neues, 1 = etwas geändert (Liste mit Bewertung), 2 = Fehler beim Laden.
# Nach einer Übernahme die Prüfsummen neu schreiben: tools/SyntheticDocUpdate.sh --stand-merken
set -u
cd "$(dirname "$0")/.."
ours="extern/SyntheticDoc"
manifest="$ours/ORIGINAL.sha256"
repo="tanguymagne/SyntheticDoc"
ckpt_url="https://igl.ethz.ch/projects/SyntheticDoc/highres_121x177.ckpt"
ckpt_size=101733025                      # wie AiModelCatalog.SyntheticDoc
ckpt_date="Thu, 06 Aug 2026 14:36:24 GMT"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
news=0

hashes() { (cd "$1" && find . -type f ! -path './media/*' | sed 's|^\./||' | LC_ALL=C sort \
    | while IFS= read -r f; do printf '%s  %s\n' "$(sha256sum "$f" | cut -c1-64)" "$f"; done); }

if ! curl -fsSL -o "$tmp/main.zip" "https://github.com/$repo/archive/refs/heads/main.zip"; then
    echo "Original nicht ladbar"; exit 2
fi
unzip -q "$tmp/main.zip" -d "$tmp"
up="$tmp/SyntheticDoc-main"
hashes "$up" > "$tmp/now.sha256"

if [ "${1:-}" = "--stand-merken" ]; then
    cp "$tmp/now.sha256" "$manifest"
    echo "Stand des Originals gemerkt: $(wc -l < "$manifest") Dateien in $manifest"
    exit 0
fi

echo "== Letzte Änderungen im Original (GitHub)"
curl -fsS "https://api.github.com/repos/$repo/commits?per_page=5" \
    | awk -F'"' '/^ +"date":/ && !d { d = substr($4, 1, 10) } /^ +"message":/ { m = $4; sub(/\\n.*/, "", m); print "  " d "  " m; d = "" }' \
    || echo "  (GitHub-API nicht erreichbar)"

echo
echo "== Dateien: Original jetzt gegen unseren Stand ($(basename "$manifest"))"
while IFS= read -r line; do
    kind="${line%% *}"; f="${line#* }"
    news=1
    case "$f" in
        training/model.py)               rate="WICHTIG – Netz geändert: SyntheticDocNet.cs nachziehen, sonst passen neue Gewichte nicht" ;;
        training/inference.py|training/unwarp.py)
                                         rate="prüfen – Vorverarbeitung/Schatten: SyntheticDocDewarp.cs vergleichen" ;;
        training/train.py)               rate="nur fürs Training; bei uns ergänzt (--init-weights, --accelerator) – zusammenführen" ;;
        training/*)                      rate="nur fürs Training (Daten, Verlust, Lernplan)" ;;
        generation/*)                    rate="nur fürs Erzeugen eigener Daten" ;;
        *.md)                            rate="lesen – neue Modelle, Links, Hinweise?" ;;
        *)                               rate="ansehen" ;;
    esac
    printf '  %-9s %-40s %s\n' "$kind" "$f" "$rate"
done < <(join -j 2 -a 1 -a 2 -e - -o 0,1.1,2.1 <(awk '{print $1, $2}' "$manifest" | sort -k2) \
                                            <(awk '{print $1, $2}' "$tmp/now.sha256" | sort -k2) \
         | awk '$2 == "-" { print "neu " $1 } $3 == "-" { print "entfernt " $1 } $2 != "-" && $3 != "-" && $2 != $3 { print "geändert " $1 }')
[ "$news" = 0 ] && echo "  keine Änderungen seit unserer Kopie"

echo
echo "== Gewichte bei der ETH"
head="$(curl -fsSI "$ckpt_url" | tr -d '\r')" || { echo "  nicht erreichbar"; exit 2; }
size="$(grep -i '^content-length:' <<< "$head" | awk '{print $2}')"
date="$(grep -i '^last-modified:' <<< "$head" | cut -d' ' -f2-)"
if [ "$size" = "$ckpt_size" ] && [ "$date" = "$ckpt_date" ]; then
    echo "  unverändert ($size Bytes, $date)"
else
    news=1
    echo "  GEÄNDERT: $size Bytes, $date (bekannt: $ckpt_size, $ckpt_date)"
fi
grep -ohE '[A-Za-z0-9_-]+\.ckpt' "$up"/README.md "$up"/training/README.md 2>/dev/null | sort -u \
    | grep -vxE '(highres_121x177|original_31x45|last)\.ckpt' | sed 's/^/  im README erwähnt, neu?: /'
[ "$news" = 1 ] && echo && echo "Bewerten: docs/KI-BEGRADIGEN.md, Abschnitt „Updates des Originals“."
exit $news
