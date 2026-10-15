# Tempo der Vorschau und der Filter

Stand 0.5.95 beta. Gemessen auf dem Laptop mit Notebook-Prozessor (8 Kerne, 16 Threads, Radeon „gfx90c“ im Prozessor),
Vorschau 2048 px (Familienfoto 2592 × 1944 → 2048 × 1536 = 3,1 MP; Panorama 7895 × 3544 → 2048 × 919 = 1,9 MP).

## Was beim Ziehen eines Reglers neu gerechnet wird (vor der Arbeit, 0.5.90)

| Regler | Dauer je Vorschau | Stufen |
|---|---|---|
| Helligkeit, Kontrast, Sättigung, Farbe | 0,19 s | Licht und Farbe |
| Luminanz | 0,35 s | dazu Bereiche |
| Tiefen / Lichter | 0,8 s | Local Laplacian |
| Dunst und Feinregler | 3,9 s | Dunst (3 Durchläufe), alles danach |
| Rauschen (mit Dunst) | 6,5 s | Entrauschen, Dunst, alles danach |
| Schärfen / Körnung | 0,1 / 0,15 s | nur diese Stufe |

Dazu die Dateigrößen-Schätzung nach 0,35 s Ruhe (zuerst 4 s, danach 0,2 s).

## Was gemacht wurde

1. **Grob beim Ziehen** (0.5.91): Solange eine Maustaste gedrückt ist oder Änderungen schnell kommen (< 300 ms), rechnet
   die Vorschau mit 1024 px (ein Viertel der Pixel), danach fein mit 2048 px. Eine laufende grobe Vorschau rechnet fertig
   statt abgebrochen zu werden (sonst kam beim Dunst bis zum Innehalten keine). Die Schätzung wartet aufs Loslassen.
   Der Dunst schätzt in der groben Vorschau auf 512 statt 1024 px (0.5.95). Geprüft (`drag-probe`): die feine Vorschau
   ist Pixel für Pixel dieselbe wie ohne Ziehen.
2. **CPU** (0.5.92, bitgleich): Kastenmittel des Entrauschens blockweise (vorher spaltenweise über den ganzen Speicher,
   12 MB neu je Aufruf), Einkern-Schleifen im Dunst und im Guided Filter auf alle Kerne. Etwa 1,3×.
3. **Grafikkarte** über ILGPU (OpenCL für AMD/Intel, CUDA für NVIDIA; `GpuFilters`, `Dehaze.Gpu.cs`):
   - Entrauschen, Non-Local Means (0.5.93): alle 80 Verschiebungen je Pixel in einem Faden.
   - Dunst (0.5.94): die Pixelschleifen von Modell und LookTone. Die Schätzungen auf der kleinen Fassung bleiben auf der CPU.
   - Ohne Grafikkarte, abgeschaltet oder nach einem Fehler: CPU (der Fehler steht im Log, bis zum Neustart CPU).
   - Kurz nach dem Start übersetzt `GpuFilters.WarmUpAsync` die Kernel im Hintergrund (2,9 s), sonst wartete die
     erste Vorschau darauf.

## Ergebnis (abwechselnd alt/neu gemessen, bester von 5 Läufen)

| Stufe | 0.5.91 | 0.5.94 | Faktor | Ergebnis |
|---|---|---|---|---|
| Rauschen | 2,25 s / 1,56 s | 0,73 s / 0,44 s | ×3,1 / ×3,6 | gleich (unter 0,0005 Stufen) |
| Dunst | 2,76 s / 1,71 s | 1,77 s / 1,15 s | ×1,6 / ×1,5 | gleich bis auf vereinzelte Pixel |
| Export-Kette (alles) | 6,9 s / 3,8 s | 3,9 s / 2,4 s | ×1,8 / ×1,6 | vereinzelte Pixel |

„Vereinzelte Pixel“: unter 1 von 10 000 Werten, im Mittel 0,0003 Stufen, an einzelnen Stellen bis zu einigen zehn
Stufen — dort kippt eine Schwelle (Entstufen, Himmel) durch einen Unterschied in der siebten Nachkommastelle. Bitgleich mit
der CPU geht auf der Grafikkarte nicht (Exponential- und Potenzfunktionen runden anders). Prüfstand:
`~/.cache/imageconverter-build/gleich-probe` (`ref` speichert die CPU-Ergebnisse, `ab.sh` misst alt/neu im Wechsel und
vergleicht); Tests `DenoiserTests.LumaDenoise_GpuAndCpu_Agree`, `DehazeTests.Dehaze_GpuAndCpu_Agree`.

## Was nicht ging (gemessen, verworfen)

- **Entrauschen in Streifen** (alle Verschiebungen je 16 Zeilen im Cache): nicht schneller — der Engpass ist die Rechnung
  (Exponentialfunktion), nicht der Speicher. Dazu vereinzelte Abweichungen. Zurückgenommen.
- **Guided Filter auf der Grafikkarte** (laufende Summen je Zeile/Spalte): langsamer bzw. gleich schnell wie die CPU,
  auch mit Transponieren. Laufende Summen sind eine Kette von Schritten — der Grafik im Prozessor liegt das nicht.
- **Licht und Farbe, Tiefen/Lichter auf der Grafikkarte**: nicht gemacht. Die Pixel hin und zurück zu kopieren kostet
  rund 30 ms — so viel wie die Rechnung in der groben Vorschau (Licht und Farbe ≈ 40 ms, Tiefen ≈ 120 ms).

## Messen auf diesem Laptop

Der Notebook-Prozessor ist strombegrenzt: Bei Dauerlast taktet er herunter, die zweite Hälfte einer langen Messung ist 20–30 %
langsamer — auch Stufen, die sich nicht geändert haben. Deshalb alt und neu im Wechsel messen (Arbeitsbaum mit dem alten
Stand, `ab.sh`) und den besten von mehreren Läufen nehmen. Mehr Kerne helfen nur, wo vorher Kerne frei waren.

## Auf dem Debian-Rechner (RX 6650 XT)

ILGPU braucht dort OpenCL (ROCm oder Mesa rusticl). Erwartet: deutlich mehr Gewinn als hier (eigene Grafikkarte mit
eigenem Speicher). Ohne OpenCL rechnet die CPU — die Zeile in Einstellungen › KI-Modelle zeigt, was gilt.
