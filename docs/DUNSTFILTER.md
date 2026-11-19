# Dunstfilter

Der Dunstfilter entfernt Dunst und Nebel nach dem physikalischen Streumodell und ohne trainiertes Modell. Code:
`ImageConverter.Core/Imaging/Dehaze.cs` (Modell, Himmelskarte, Luftlicht, Helligkeitsausgleich, Guided Filter),
`DehazeLook.cs` (Klarheit, Kurve, Dynamik, Schärfe je Durchlauf), `SceneMap.cs` (Himmel, Lichthof, Himmelsfarbe, Tiefe,
Rauschen), `Dehaze.Gpu.cs` (die zwei großen Pixelschleifen auf der Grafikkarte), Durchläufe in `Adjustments.Dehazed`.

## Ablauf

1. **Streumodell** in linearem Licht: I = J·t + A·(1 − t). Alle Schätzungen auf einer verkleinerten Kopie (lange Kante 1024 px);
   das große Bild wird je Durchlauf einmal durchlaufen.
2. **Himmelskarte** (`SkyMap`): hell und ohne Struktur = Himmel. Die Struktur (Bandpass zweier Glättungen relativ zur Helligkeit)
   wird mit dem Guided Filter geglättet, nicht isotrop, damit die Struktur einer Baumkante nicht in den Himmel daneben läuft.
   Einzelne glatte Flecken ohne Verbindung zum oberen Rand und unter 1 % Fläche zählen nicht (`DropIsolatedSky`), Grün nie
   (`SkyColour`). Werte über 1 markieren den Lichthof um übersteuerte Pixel (Sonne).
3. **Himmelsfarbe** (`SceneMap`): die Farbe des Himmels in der Nähe, als normierte Faltung über den Himmel. Ein Pixel in dieser
   Farbe zählt neben Silhouetten als Himmel, auch wo die Strukturkarte einen Übergangswert hat.
4. **Luftlicht** als Feld (6 × 4 Kacheln, vertrauensgewichtet gegen Spalten- und Gesamtwert): Kandidaten sind die 0,1 % mit dem
   höchsten Dunkelkanal ohne übersteuerte Pixel, gemittelt das hellste Zehntel (Grundverfahren nach He, Sun, Tang 2009).
5. **Durchlässigkeit** t = 1 − 0,95 · Dunkelkanal, verfeinert mit dem Guided Filter (Farb-Führungsbild), kantentreu auf volle
   Größe; „Toleranz“: was dem Luftlicht sehr ähnlich ist, zählt nicht als dichter Dunst. Himmel zu 85 % geschützt (ab dem zweiten
   Durchlauf ganz).
6. **Rückrechnung** J = (I − A)/t + A, gemischt mit der Stärke; Farbe: Helligkeit vom Modell, Farbton vom Original, Sättigung
   begrenzt, Farbstich des Luftlichts anteilig abgezogen („Dunstfarbe“).
7. **Helligkeitsausgleich**: Verhältnis der geglätteten Lichtmengen vorher/nachher, getrennt für Landschaft und Himmel; Anteil
   = Regler „Helligkeit“ × Stärke des Durchlaufs (so seit jeher, die Regler sind darauf eingestellt). In dichtem Dunst nur ein
   Teil (30 % bis zu einer Durchlässigkeit von 0,35, voll ab 0,85), sonst käme die milchige Ferne zurück.
8. **Wahrnehmung** je Durchlauf: Klarheit (kantentreue Basis), Kurve, Dynamik, feine Schärfung. Die Schärfung greift nur bei
   Details über dem Rauschen (Coring, Schwelle 1 … 2,5 × σ, σ aus dem Rauschen des Originals nach Immerkær 1996 mal der
   Verstärkung der Durchläufe, höchstens ×2); nicht im Himmel (dort 40 %), voll im Lichthof (die Strahlen sind Struktur).
9. **Grafikkarte**: Modell- und Look-Schleife laufen mit denselben Formeln auf der GPU (Himmelsfarbe und Tiefe als gemeinsamer
   Puffer); `DehazeTests.Dehaze_GpuAndCpu_Agree` prüft die Übereinstimmung.

## Patentlage (Stand 19.11.2026, Anspruchstexte bei Google Patents; keine Rechtsberatung)

| Patent | Kern | Folge für den Filter |
|---|---|---|
| US 9 305 339 B2 (bis 2034) | trainiertes Modell aus mehreren Merkmalskarten → t; abhängig: Luftlicht als Median, Helligkeitsabgleich | kein trainiertes Modell, also nicht berührt; die zwei Bausteine wurden trotzdem ersetzt (Fehlergedächtnis #127) |
| US 8 340 461 B2 (Dark Channel Prior) | Dunkelkanal → t | erloschen (Gebühren) |
| US 11 810 272 B2 (Haze-Lines, bis 2037) | Farbcluster über das Bild → t, Luftlicht per Hough | nicht verwenden |
| US 9 508 129 B1 (bis 2035) | beim Entdunsten Kanten unter einer Gradientenschwelle des Eingangs dämpfen | Coring nur mit Schwelle aus dem Rauschmodell, nie aus dem Eingangsgradienten |
| US 9 241 093 B2 (bis 2034) | Detail → Entrauschen → Verstärkung aus der lokalen Varianz | keine varianzabhängige Verstärkung |
| US 9 508 126 B2 (bis 2035) | Untergrenze von t aus einem bildabhängigen Faktor | `KeepShare` bleibt eine Konstante |

## Labor

`tools/DunstLabor` erzeugt Landschaften mit bekannter Tiefe (Hügelketten, Bäume vor dem Himmel, eine weiße Wand, ein heller Weg,
ein physikalisch verdunsteter Himmel mit Verlauf, optional eine Sonne mit Hof und zwölf Strahlen), verdunstet sie mit dem
Streumodell (Dichte 0 … 4, mit und ohne Rauschen) und misst das Ergebnis gegen die Wahrheit: ΔE in Oklab je Tiefenband (zerlegt
in ΔL, ΔC, ΔH), Helligkeit zur Wahrheit, Himmelsdrift, Sättigung des Himmels neben Land gegen innen, Rauschen in glatten
Flächen, Erhalt der Strahlen; dunstfreie Kontrollbilder müssen (fast) unverändert bleiben. Aufruf: `dotnet run -c Release --
lab` im Ordner des Werkzeugs; mit `-p:CoreProject=…` gegen eine andere Fassung bauen und vergleichen. Entscheidung über Median
und schlechtesten Fall.

**Messwerte 0.12.7 → 0.12.14-beta** (Median / schlechtester Fall; „Schätzer“ = Dunst 100, 3 Durchläufe, ohne Wahrnehmungsregler;
„Beispiel“ = Dunst 36, 8 Durchläufe mit Klarheit, Kontrast, Dynamik, Schärfe):

| Messgröße | Schätzer | Beispiel |
|---|---|---|
| ΔE nah | 3,41 → 2,79 / 3,87 → 3,43 | 7,16 → 6,40 / 7,75 → 6,95 |
| ΔE Mitte | 13,79 → 12,83 / 17,38 → 16,79 | 19,41 → 18,33 / 22,72 → 22,38 |
| ΔE fern | 26,73 → 26,56 / 28,84 → 28,74 | 30,71 → 30,51 / 32,46 → 32,36 |
| Rauschen fern (Ergebnis ÷ Eingang) | 1,20 → 1,20 | 1,09 → 1,01 |
| Rand 0–25 px ÷ innen | 1,04 → 1,04 / 1,12 → 1,22 | 1,07 → 1,05 / 1,11 → 1,12 |
| Kontrolle ohne Dunst | 1,77 → 1,85 | 4,36 → 4,27 |

Am Panorama gegen die Sonne (Dunst +36, 8 Durchläufe, Gleiche Farbe, Luminanzmaske, Körnung): Rauschen in der Ferne −20 %,
Sättigung neben Silhouetten 3,0 → 2,6 (Original 1,6), kein Fleck im Feld, Strahlen und Hof wie 0.12.7, Helligkeit wie 0.12.7.

## Verworfen

- **Volle Wirkung des Reglers „Helligkeit“** (ohne die Stärke des Durchlaufs): Tal heller und kontrastreicher, aber die Ferne
  milchiger als 0.12.7 und alle eingestellten Bilder verändert.
- **Sonnenhof vor Zielwert, Luminanzmaske und Lichter schonen**: ohne messbare Wirkung (Strahlen 13,94 → 13,96), kostete eine
  zusätzliche Himmelskarte je Vorschau. Die Strahlen sind nach dem Filter stärker als im Original; abgeschwächt wirken sie, wenn
  „Gesamt“ die ganze Umgebung aufhellt.
- **Horizontregel** als harte Bedingung (bedeckter Himmel würde voll entdunstet; 0.4.39), **Braun als Nicht-Himmel** (Sprenkel im
  warmen Horizonthimmel), **Haze-Lines** (Patent).

## Offen

- Unter dichtem Dunst ist die Struktur so schwach, dass die Himmelskarte die Ferne für Himmel hält und schützt; ein Strukturmaß,
  das die Durchlässigkeit herausrechnet, und ein Luftlicht per Regression gegen den Dunkelkanal könnten die Ferne klarer machen
  (Labor: Ferne ΔE 29 → 15–20 erwartet) – nur mit Wächtern für Nebel, Smog, Schnee und dunstfreie Bilder.
- Ein Regler „Himmel“ (Klärung entlang des eigenen Himmelsverlaufs, nie heller) statt des festen Schutzes von 85 %.
