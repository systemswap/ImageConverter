# Hintergrund entfernen

Stand 29.10.2026 (0.6.35 beta). Eigene Rubrik „Hintergrund“ in der Leiste links. Das Modell stellt das Motiv frei; der
Hintergrund wird **transparent** oder **unscharf** (Porträt-Effekt, Regler „Stärke“). Ein Bildschritt im Pixelbild wie
„Entfernt“: Zeile „Hintergrund entfernt“ / „Hintergrund unscharf“ in „Bearbeitungen“, Rückgängig mit Strg+Z, vor
Geometrie, Größe/Upscaling und allen Filtern.

## Bedienung

- **Maus: Automatik** (Standard): kein Pinsel, das Motiv wird ohne Markierung erkannt. Die Maus arbeitet wie sonst
  (Vorher/Nachher-Strich, Zuschnitt).
- **Rot: weg** — grob über etwas malen, das nicht zum Motiv gehört: jedes Objekt, das der Strich berührt, verschwindet
  ganz (dazu alles direkt unter dem Strich).
- **Grün: bleibt** — grob über etwas malen, das bleiben soll: berührte Objekte sind vor Rot geschützt. Fehlt ein Teil des
  Motivs, rechnet das Modell den Bereich um die grünen Striche (Rahmen + 50 %) vergrößert noch einmal und ergänzt die
  Objekte, die Grün dort berührt.
- **Noch einmal anwenden** (Striche, Modus oder Stärke geändert) rechnet vom Bild vor dem Hintergrund-Schritt und nimmt die
  gemerkte Maske — ohne neue Erkennung, 1–3 s. Der Regler rechnet erst beim Klick, nicht beim Ziehen.
- **Anderes Modell:** dasselbe mit neuer Erkennung; das Ergebnis ist pixelgleich mit diesem Modell direkt auf dem Bild
  davor. Die Vorschau zeigt während der Rechnung das Bild davor. Der neue Versuch ersetzt den vorigen: Dessen Ergebnis wird
  freigegeben, Strg+Z führt zum Stand vor dem Hintergrund-Schritt.
- **Bildschritte danach** (KI, Entfernen): Ein neuer Versuch geht trotzdem zum Bild vor dem Hintergrund-Schritt zurück; sie
  sind dann nicht enthalten. Das sagt der Hinweis vorher, Strg+Z holt sie zurück. Hat die KI das Bild danach vergrößert,
  meldet die App das und rechnet nicht. „Auswahl anwenden“ macht alles Bisherige endgültig: Danach ist das Bild der Ausgang.
- Gerechnet wird der **sichtbare Ausschnitt** (nach Zuschnitt); außerhalb ist transparent bzw. unverändert.
- JPEG/BMP: Hinweis mit „Als PNG speichern“ — beim Speichern als JPEG/BMP wird der Hintergrund weiß.
- Modelle: Einstellungen › KI › Hintergrund. RMBG-2.0: „Seite öffnen“, dort zustimmen und laden, dann „Datei wählen …“ —
  die App prüft Größe und Prüfsumme und kopiert die Datei in ihren Modellordner.
- BiRefNet general: rechnet die Grafikkarte (DirectML), nimmt die App den „Zusatz für die Grafikkarte“ (fp16), wenn er
  geladen ist; sonst die volle Datei auf der CPU.

## Modelle

Die App liefert keine Modelle mit; jeder Nutzer lädt sie selbst (Download-Manager, Gruppe „HINTERGRUND“). Darum gelten
die Lizenzen für den jeweiligen Nutzer — die App darf kostenlos weitergegeben werden.

| Modell | Datei | Größe | SHA-256 | Eingang | Lizenz | Quelle |
|---|---|---|---|---|---|---|
| BiRefNet general (Standard) | `BiRefNet-general-epoch_244.onnx` | 972 666 916 | `58f621f00f5d756097615970a88a791584600dcf7c45b18a0a6267535a1ebd3c` | 1024², ImageNet-normiert | MIT | GitHub-Release `ZhengPeng7/BiRefNet` v1 |
| BiRefNet lite (Swin tiny) | `BiRefNet-general-bb_swin_v1_tiny-epoch_232.onnx` | 224 005 088 | `5600024376f572a557870a5eb0afb1e5961636bef4e1e22132025467d0f03333` | 1024², ImageNet-normiert | MIT | dito |
| BiRefNet HR-matting | `BiRefNet_HR-matting-epoch_135.onnx` | 1 098 928 867 | `45b7d92ce0e2e75e1c8a564a9e84c87ac9da7f4315d2deeef3674864e809aef3` | 2048², ImageNet-normiert | MIT | dito |
| BEN2 base | `BEN2_Base.onnx` | 222 932 053 | `22cea62108ff53b7ccc20f7a008bf30494228d84b1687f29ecbe76936a998101` | 1024², 0…1 **ohne** Normierung | MIT | Hugging Face `PramaLLC/BEN2` |
| RMBG-2.0 (Bria) | `onnx/model.onnx` | 1 024 331 469 | `5b486f08200f513f460da46dd701db5fbb47d79b4be4b708a19444bcd4e79958` | beliebig (gerechnet 1024²), ImageNet-normiert, Eingang `pixel_values`, Ausgang `alphas` | CC BY-NC 4.0 — **nur nicht-kommerziell** | Hugging Face `briaai/RMBG-2.0` (Anmeldung nötig) |

Nicht genommen: `BiRefNet_lite-general-2K` — fester Eingang 1440 × 2560 (Hochformat), Querformate würden verzerrt.

Ausgänge: je nach Modell Logits oder schon 0…1 — der Messplatz prüft den Wertebereich und rechnet Logits mit dem Sigmoid um.

### RMBG-2.0 von Hand laden

Bria gibt das Modell nur nach Zustimmung frei; die App kann es deshalb nicht selbst laden.

1. Bei Hugging Face anmelden (kostenloses Konto) und https://huggingface.co/briaai/RMBG-2.0 öffnen.
2. Im Formular Name und E-Mail angeben und den Haken setzen („Non commercial use … Personal use / Academy / Non
   profit“). Damit gehen Name und E-Mail an Bria; der Zugang ist sofort frei.
3. Im selben Browser https://huggingface.co/briaai/RMBG-2.0/resolve/main/onnx/model.onnx öffnen (1,02 GB, volle
   Genauigkeit; die anderen Dateien im Ordner `onnx` sind kleinere, ungenauere Fassungen).
4. In den Einstellungen bei RMBG-2.0 „Datei wählen …“ und die Datei aus dem Download-Ordner wählen — die App prüft Größe
   und Prüfsumme und kopiert sie nach `KI-Modelle/rmbg-2.0/model.onnx` (neben dem Programm, sonst im Benutzerordner).

Wer die App weitergibt: Jeder Nutzer muss diese Zustimmung selbst geben.

## Reihenfolge in der Kette

1. **Pixelbild:** Original, dann KI, Entfernen und Hintergrund in der Reihenfolge der Klicks.
2. **Geometrie** (`GeometryRecipe`): Begradigen, Entzerren, Gerade richten, Zuschnitt, Drehen/Spiegeln.
3. **Größe** und Rauschen: beim Vergrößern erst Rauschen, sonst danach.
4. **Regler:** Dunst, Tiefen/Lichter, Licht und Farbe, Schärfen, Körnung.
5. **Ausgabe:** Farbprofil, Metadaten.

Das Modell sieht das Bild also ohne Regler (Stufe 4); sie bleiben beim neuen Versuch stehen und wirken danach auf das
Ergebnis. Es sieht aber KI- und Entfernen-Schritte, die vor dem Hintergrund-Schritt liegen.

Transparente Pixel sparen in den Stufen 3–4 heute keine Rechenzeit: Die Filter rechnen alle Pixel und kopieren Alpha nur
durch. Unter voll transparenten Pixeln liegen die alten Farben, die Filter sehen sie also (Wirkung nicht gemessen).

## Rechnung (`ImageConverter.Core/Ai/BackgroundRemover.cs`)

1. **Erkennen:** Ausschnitt auf 1024² (Modellkante) und auf die Arbeitsgröße (lange Kante ≤ 2048), Modell rechnen,
   Logits → Sigmoid, Maske auf die Arbeitsgröße.
2. **Pinsel:** Objekte = zusammenhängende Flächen mit Alpha > ½, bis in ihre weichen Ränder (Alpha > 0,02) erweitert.
   Grün zuerst (vergrößerter zweiter Lauf, berührte Objekte dazu), dann Rot (berührte Objekte und Pixel unter dem
   Strich auf 0, außer grün geschützte Objekte).
3. **Kanten:** Guided Filter (σ 2, ε 10⁻³) mit dem Foto als Führung; die Koeffizienten bei Arbeitsgröße, angewendet mit
   dem Foto in voller Auflösung (Fast Guided Filter) — keine Treppen auch bei 24 MP.
4. **Farbsaum:** Vordergrundfarbe per Blur Fusion (Forte & Pitié 2021, zwei Durchgänge r ≈ 90 und 6) — sonst bleibt an
   Haaren und Armen ein Rand in der alten Hintergrundfarbe (an den Testbildern weg).
5. **Ergebnis:** transparent = Alpha (16 Bit) mit geschätzter Vordergrundfarbe; unscharf = F·α + B·(1 − α), B = Gauß
   (σ = Stärke × 2 % der langen Kante) nur über den Hintergrund (normierte Faltung, das Motiv färbt nicht ab).
   Speichern: PNG, WebP, TIFF, AVIF, JPEG XL mit Transparenz; JPEG/BMP mit Weiß. Upscaling vergrößert die Maske mit.

Zeiten nach dem Modell (8 Testbilder bis 3000 px, CPU): Kanten + Farben 0,2–2,9 s, unscharf 0,2–1,5 s.
Bei 24 MP (Desktop mit 32 Threads, BiRefNet lite auf der CPU): Erkennen 5 s, roter Pinsel 0,05 s, grüner Pinsel mit
Zoom-Lauf 5 s, Kanten + Farben 0,4–1,5 s, unscharf 0,4–0,8 s.

Speicher: Das Modell braucht auf der CPU je Lauf 6–7 GB Zwischenspeicher. Hintergrund-Modelle laufen deshalb ohne
den Speicher-Pool der ONNX Runtime – sonst blieb er nach dem Lauf belegt und wuchs (7,0 → 11,7 GB, Fehlergedächtnis
#88). Danach hält das Programm rund 1 GB.
Prüfung: `BackgroundTests` (Ersatzmodell: Kanten, Rot, Grün mit Zoom, Farbsaum, Unscharf, Ausschnitt);
`~/.cache/imageconverter-build/hintergrund-probe` (Kern an den Testbildern), `hintergrund-ui` (Rubrik im unsichtbaren
Fenster: Automatik, Rot, Grün, Unscharf, Export PNG, Rückgängig).

## Messplatz

`~/.cache/imageconverter-build/hintergrund/` (`messplatz.py`: CPU-Zeiten, Masken, Vergleichsbögen; `gpu.py`: Zeit mit
DirectML und Abweichung zur CPU). Acht Testbilder: lange Haare, Zuckerwatte, Zopf vor Palmen, ganze Person mit Blumen,
Schildkröte mit zwei Personen, Gruppe am Strand, Fliegenpilz, kahle Äste vor Himmel (Bilder bis 3000 px).

### Ergebnis (29.10.2026, Notebook mit integrierter Grafik)

| Modell | CPU je Bild | Grafikkarte (DirectML) | Qualität |
|---|---|---|---|
| BiRefNet general | 24 s | 11–13 s mit `onnx-community/BiRefNet-ONNX` `model_fp16.onnx` und **ohne Graph-Optimierung**; Abweichung zu fp32 im Mittel 0,02–0,05 Stufen, > 8 Stufen an 0,005–0,05 % der Pixel. fp32 auf der Grafikkarte: bricht ab (Speicher) | am saubersten: Zuckerwatte ohne die Leute daneben, Pilz, Gruppe |
| BiRefNet lite | 11 s | 7–9 s (fp32, ohne Graph-Optimierung), bitgleich bis 1 Stufe | gut, lässt öfter Reste stehen (Beine im Hintergrund) |
| BEN2 | 17 s | 9 s, aber **weicht ab** (bis 122 Stufen, Mittel 0,7) — vorerst CPU | feinste Haare (abstehende Strähnen), behält aber Personen im Hintergrund (Zuckerwatte) |
| RMBG-2.0 | 23 s | 14 s, gleich (höchstens 1 Stufe), mit Optimierung | etwa wie BiRefNet general |
| BiRefNet HR-matting | 116–196 s | lädt nicht | **verworfen**: zu langsam, Speicher reichte beim 6. Bild nicht, Schleier und falsche Reste (Blatt beim Pilz, Sand) |

- **DirectML und BiRefNet:** Mit Graph-Optimierung bricht das Laden ab („Split … Cannot parse data from external tensors“) —
  bei allen BiRefNet-Dateien; RMBG-2.0 (gleiches Netz, anders exportiert) lädt. Ohne Optimierung geht es.
- **Grenze:** Landschaft ohne klares Motiv (Äste vor Himmel): BiRefNet entfernt alles, BEN2/RMBG-2.0 behalten zufällige
  Teile. Das Verfahren stellt ein Motiv frei — Himmel ersetzen bräuchte ein eigenes Himmel-Modell.
- **Farbsaum:** An Armen vor Grün bleibt ein grüner Rand (alle Modelle) — der Schritt „Farbsaum entfernen“ ist nötig.

## Analyserunde (29.10.2026, 0.6.38–0.6.42 beta)

Geprüft mit BiRefNet lite auf der CPU (Desktop, 32 Threads), Ersatzmodellen und künstlichen Bildern; Prüfprogramme
außerhalb des Projekts: `~/.cache/imageconverter-build/hintergrund-ui` (unsichtbares Fenster: Blättern und neue
Bilder während der Berechnung) und `hintergrund-probe` (Modi `eingaben`, `grau`, `modelle`, `formate`, `messen`,
`arena`, `fuzz`).

| Bereich | Ergebnis |
|---|---|
| Blättern während der Berechnung | **Fehler #85** (Bild freigegeben, aus dem gerechnet wurde – betraf alle KI-Bildschritte) und **#86** (Ergebnis des falschen Bildes gemerkt) – behoben |
| Bildarten | CMYK, Palette, schon transparent: in Ordnung. **Graustufen: Fehler #87** – behoben |
| Größen | 1 × 1, 2 × 2, 4000 × 3, 3 × 4000, Ausschnitt 10 × 10 am Rand: in Ordnung |
| Präparierte Modellausgaben | falsche Länge, leer, NaN: **#87** – jetzt klare Meldung; ±unendlich ohne Absturz |
| Rundläufe transparent | PNG/TIFF 16 Bit mit Alpha, WebP/AVIF/JPEG XL mit Alpha, JPEG/BMP mit weißem Hintergrund: in Ordnung |
| Speicher | **#88**: Speicher-Pool der ONNX Runtime hielt bis 11,7 GB – für Hintergrund-Modelle aus, pixelgleich |
| Modell-Import | **#89**: Verknüpfung abgelehnt – behoben; falsche Größe/Inhalt, Abbruch, gleiche Datei: in Ordnung |
| Fuzzing | 2500 + 1500 Läufe (Größen, Ausschnitte, Striche außerhalb, NaN-Punkte, Radius bis 10⁶, Stärke −1…5): keine Fehler, langsamster Lauf 0,36 s. Speicher stabil (220–255 MB), sobald der Pufferpool wie im Programm nach jedem Lauf geleert wird – ohne Leeren wuchs er im Prüfprogramm, kein Fehler des Programms |
| Tempo | nichts zu holen: Pinsel 0,05 s, Kanten + Farben unter 1,5 s bei 24 MP; den Rest braucht das Modell |

Nicht geprüft: Grafikkarte (DirectML gibt es nur unter Windows), die anderen Hintergrund-Modelle (nur BiRefNet
lite ist hier geladen), echte Fotos.
