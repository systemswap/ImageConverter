# STL-Betrachter mit Messen

Wunsch vom 30.10.2026: ImageConverter ist auch ein Bildbetrachter; er soll STL-Modelle
anzeigen und Abstände zwischen Flächen messen können. Nur STL – STEP und andere
CAD-Formate bewusst nicht. Umgesetzt ab 0.8.10-beta (Rechenteil), Anzeige und Einbau danach.

## 1. Verhalten im Programm

- Eine `.stl`-Datei öffnet sich wie ein Bild (Öffnen, Ziehen, „Öffnen mit“, Blättern im
  Ordner). Die Ordnerliste enthält Bilder **und** STL-Dateien; der Dateidialog hat dafür
  „Bilder und Modelle“, „Bilder“ und „3D-Modelle (STL)“. `einrichten.sh` meldet
  `model/stl` (samt `application/sla`, `model/x.stl-*`) an, `einrichten.ps1` die Endung `.stl`.
- Statt der Bildfläche erscheint die 3D-Ansicht, statt Rubriken und Einstellungen die
  Spalte des Modells (Kennzahlen, Ansicht, Messen). Solange ein Modell gezeigt wird, gibt
  es kein aktuelles Bild – darum sind Export, Drucken, Werkzeuge und KI von selbst aus
  (sie hängen an `HasImage`). Export- und Druckfenster listen nur Bilder.
- Bleibt nutzbar: Hell/Dunkel, Vollbild (F11, dann ohne Spalte), Blättern (←/→, Bild
  auf/ab, Pos1/Ende).
- Kennzahlen: Format (binär/Text), Größe X × Y × Z, Dreiecke, Eckpunkte, Oberfläche,
  Volumen (≈ wenn nicht geschlossen), geschlossen (offene / mehrfach genutzte Kanten),
  Teile, erkannte Flächen, Kopfzeile, Hinweise zu übersprungenen Dreiecken.
- Die Einstellungen des Betrachters (Darstellung, Kanten, Teile färben, Projektion,
  Drehpunkt, Einheit) liegen in `viewer3d.json` neben den Programmeinstellungen.

## 2. Aufbau: fest eingebautes Plugin

Eigenes Projekt `ImageConverter.Viewer3D`, mitgebaut, nur über eine schmale Schnittstelle
mit dem Hauptprogramm verbunden (`ImageConverter.Ui/Documents/DocumentViewers.cs`):

```
IDocumentViewer                         IOpenDocument : IDisposable
  Extensions      (".stl")                Details   Zeile unter dem Dateinamen
  FileTypeName    (Dateidialog)           View      ersetzt die Bildfläche
  OpenAsync(path, ct) → IOpenDocument     Panel     ersetzt die Einstellungsspalte
```

- Angemeldet im Startprojekt (`Program.Main`: `DocumentViewers.Register(new StlViewer())`);
  die Oberfläche kennt das Plugin nicht. `MainViewModel.Documents.cs` zeigt Dokumente.
- `Geometry/` ohne Avalonia (Einlesen, Nachbarschaft, Flächen, Suchbaum, Messen – voll
  getestet), `Rendering/` (OpenGL, Kamera, vorbereitete Daten), `Views/` (Ansicht,
  Maßlinien, Spalte), `ModelViewModel` (Zustand, Hintergrundschritte, Auswahl, Messliste).
- Kein Nachladen aus einem `plugins/`-Ordner: das Programm wird als Einzeldatei bzw.
  AppImage gebaut, Nachladen würde Bau, Versionen und Startprüfung verkomplizieren. Die
  Schnittstelle lässt es später zu.

## 3. Einlesen (`StlReader`)

- Binär und Text; Erkennung am Inhalt: passt `84 + 50 × Anzahl` zur Dateigröße, ist es
  binär (auch wenn die Kopfzeile mit `solid` beginnt); sonst Text, wenn es mit `solid`
  beginnt und nur Textzeichen enthält; sonst binär mit Restbytes oder abgeschnitten
  (dann wird gelesen, was da ist, und „unvollständig“ gemeldet).
- Grenzen gegen kaputte oder präparierte Dateien: höchstens 20 Mio. Dreiecke (vor dem
  Anlegen geprüft) und 24 Mio. einzelne Eckpunkte, Zeilen höchstens 4096 Byte, Zahlen
  endlich und höchstens ±10⁹, mehr als 64 Ecken je Facette werden verworfen. Gespeicherte
  Normalen werden nicht gelesen.
- Gleiche Eckpunkte werden zu einem zusammengeführt (eigene Hash-Tabelle mit zufälligem
  Startwert je Programmlauf, damit eine Datei keine Kollisionsketten erzwingen kann; −0 = +0).
  Dreiecke mit zwei gleichen Ecken fallen weg und werden gezählt.
- STL hat keine Einheit: angenommen werden Millimeter, umschaltbar auf Zoll.

## 4. Was im Hintergrund folgt

Das Modell steht sofort auf dem Bildschirm; danach, je fertig auf dem UI-Thread übernommen:

1. Nachbarschaft (`MeshTopology`): Kanten über Sortieren aller Halbkanten – O(T log T),
   auch bei präparierten Netzen –, offene und mehrfach genutzte Kanten, Teile
   (Union-Find), Oberfläche, Volumen. Dazu Kantenlinien, Teilefarben, Bodenschatten.
2. Flächen und Suchbaum parallel; danach ist Messen möglich.

Gemessen (5 Mio. Dreiecke, eine Kugel als ungünstigster Fall für die Flächen): Lesen 0,4 s,
Nachbarschaft 0,4 s, Flächen 1,6 s, Suchbaum 0,45 s; ein Klick (Strahl) etwa 2 µs.

## 5. Darstellung

- OpenGL über `OpenGlControlBase` aus Avalonia (Windows: ANGLE, Linux: EGL/GLX). **Kein
  neues Paket:** die nötigen GL-Befehle werden über Funktionszeiger selbst angebunden
  (`Rendering/Gl.cs`); Shader für GLSL 1.30/1.50/3.30 und GLSL ES 3.00. Ohne OpenGL 3
  bzw. ES 3 erscheint ein Hinweis, die Kennzahlen stehen trotzdem in der Spalte.
- Flache, kantenscharfe Schattierung; die Flächennormale entsteht im Fragment-Shader aus
  den Ableitungen der Position – geteilte Eckpunkte reichen, auch bei Millionen Dreiecken.
- Gestaltung: ruhiger Verlauf als Hintergrund (folgt Hell/Dunkel), mattes Material,
  Hauptlicht von links oben, schwache Aufhellung von rechts (beide an die Kamera
  gebunden), Himmel-/Bodenlicht entlang der Hochachse, Glanz und Randlicht schwach.
  **Umgebungsverdeckung im Bildraum** (SSAO, halbe Auflösung, 16 Proben, geglättet):
  Ecken, Bohrungen und der Fuß eines Zapfens werden weich dunkler – unabhängig von der
  Modellgröße und ohne Rechenzeit auf dem Prozessor. Eine Verdeckung je Eckpunkt wurde
  verworfen: große ebene Flächen haben zu wenige Eckpunkte, um sie zu zeigen.
  Weicher **Bodenschatten** aus der Draufsicht (256 × 256, geglättet), blendet beim Blick
  von unten aus. Feine Linien an scharfen Kanten (> 40°), Kantenglättung (4× MSAA).
- Ansichten „Fläche“, „Drahtgitter“ (alle Kanten, auch verdeckte) und „Bauplan“ (blau,
  flache Flächen, weiße Kanten); „Teile färben“ zeigt die zusammenhängenden Teile.
- Gezeichnet wird nur, wenn sich etwas ändert.

### Bedienung

- Links ziehen = drehen, rechts/mitte ziehen (oder Strg + links) = verschieben, Mausrad =
  zoomen zur Maus, Doppelklick oder F = einpassen. Ein Klick ohne Ziehen (unter 4 px)
  wählt beim Messen.
- **Drehpunkt:** wählbar „Klickstelle“ (um die Stelle unter der Maus, sonst um den
  bisherigen Mittelpunkt – die angeschaute Stelle bleibt im Bild) oder „Modellmitte“ (um
  die senkrechte Mittelachse des Modells, wie auf einem Drehteller). Beim Drehen markiert
  ein Punkt den Drehpunkt.
- **Drehscheibe:** die Hochachse (Z, wie beim 3D-Druck) bleibt immer senkrecht;
  waagerecht ziehen dreht um sie, senkrecht ziehen kippt höchstens bis zur Drauf- bzw.
  Untersicht.
- Knöpfe: Einpassen, Iso, Vorne, Hinten, Links, Rechts, Oben, Unten (mit kurzem Übergang);
  Orthografisch/Perspektive (zum Messen orthografisch voreingestellt).

## 6. Flächen (`FaceFinder`)

STL kennt nur einzelne Dreiecke; eine gekrümmte Fläche kommt als Streifen ebener Facetten:

1. **Ebenen:** von den größten Dreiecken aus (ihre Normalen sind am genauesten) wachsen
   Nachbarn an, solange ihre Ecken in der Startebene liegen (2·10⁻⁵ der Modellgröße,
   mindestens einige Float-Schritte der Koordinaten).
2. **Facetten:** eine Ebene gilt als Facette einer gekrümmten Fläche, wenn mehr als die
   Hälfte ihres Randes sanft (≤ 40°) an Nachbarn stößt und sie nicht deutlich breiter ist
   als diese – so bleibt die Oberseite eines Quaders mit gerundeten Kanten eine Ebene.
3. **Zylinder:** benachbarte Facetten, deren Normalen alle senkrecht zu einer Achse
   stehen (Eigenvektor zum kleinsten Eigenwert von Σ A n nᵀ) und deren Ecken auf einem
   Kreis liegen (Kåsa-Ausgleich). Streng: Ecken eines CAD-Zylinders liegen exakt auf dem
   Kreis, ein Kugelstreifen weicht um etwa α²/8 ab (α = Winkel der Facetten) – zugelassen
   ist ein Fünfzigstel davon. Geschlossen (Ring aus Facetten) → Ø, sonst Bogen → R.
4. **Freiform:** übrige Facetten, über sanfte Kanten verbunden.

Ein gescheiterter Zylinderversuch nimmt seine Facetten aus weiteren Versuchen heraus – die
Laufzeit bleibt linear. Treffer beim Klick über den Hüllkörperbaum (`Bvh`, Binning mit
Flächenheuristik, Tiefe begrenzt, ohne Rekursion; obere Ebenen werden geteilt, die
Teilbäume parallel gebaut).

## 7. Messen

Werkzeug **Messen** einschalten (Knopf unten rechts oder in der Spalte), dann:

1. Klick auf eine Fläche → sie leuchtet in der Farbe des nächsten Maßes (A).
2. Klick auf eine zweite Fläche (B) → das Maß erscheint und **bleibt stehen**.
3. Der nächste Klick beginnt ein neues Paar; alle fertigen Maße bleiben sichtbar, jedes in
   eigener Farbe, ihre Flächen leicht getönt. Eine Fläche darf in mehreren Maßen vorkommen
   (z. B. mehrere Maße von einer Bezugsfläche aus).
4. Klick auf die schon gewählte Fläche des offenen Paares hebt die Wahl auf (bei Punkten:
   innerhalb von 6 Pixeln).
5. Klick auf die Beschriftung eines Maßes (mit ✕) oder ✕ in der Liste entfernt es; Esc hebt
   das offene Paar auf; „Alle Maße löschen“ räumt auf. Maße gelten nur für das gezeigte
   Modell und werden nicht gespeichert.

Beim Überfahren wird die Fläche unter der Maus leicht hervorgehoben; die Leiste zeigt, was
sie ist (Ebene mit Fläche, Zylinder mit Ø, Rundung mit R, Freiform). Ein einzelner Klick auf
einen Zylinder zeigt sofort seinen **Durchmesser**. **Punkt zu Punkt:** Umschalt + Klick →
gerader Abstand, ΔX/ΔY/ΔZ; Punkt ↔ Fläche geht auch.

| A ↔ B | angezeigt |
|---|---|
| Ebene ∥ Ebene | senkrechter Abstand (Linie vom Klick auf A senkrecht auf B) |
| Ebene ∦ Ebene | Winkel, dazu kürzester Abstand |
| Zylinder ↔ Zylinder, Achsen parallel | Achsabstand, kürzester und größter Abstand, beide Ø (Rohr: Wandstärke) |
| Zylinder ↔ Zylinder, sonst | Winkel der Achsen, Abstand der Achsen |
| Zylinder ↔ Ebene | Achse parallel: Abstand Achse–Ebene, kürzester/größter Abstand; sonst Winkel |
| mit Freiform | kürzester Abstand (0 = berühren sich) |

„Parallel“: Ebenen unter 0,1°, Achsen unter 0,5°. Gerechnet wird mit den eingelesenen
Dreiecken in doppelter Genauigkeit, nicht mit der Anzeige; kürzeste Abstände über einen
Suchbaum je Fläche. Anzeige in mm mit zwei Nachkommastellen (Zoll: drei), Winkel mit 0,01°.

## 8. Tests

- Die Tests erzeugen ihre Modelle selbst (Quader, Zylinder, gedrehter Zylinder, Rohr,
  Profil mit Rundung, Kugel, binär und Text) und prüfen Einlesen, Kennzahlen,
  Flächenerkennung, jede Maßart, Suchbaum gegen Ausprobieren aller Dreiecke, präparierte
  und zufällig beschädigte Dateien (Zeitgrenze), Kamera (Rückprojektion, Drehpunkt bleibt
  stehen, Zoom zur Maus, Einpassen), vorbereitete Daten, Texte.
- Die OpenGL-Ansicht wurde mit einem Prüfprogramm im echten Fenster aufgenommen (alle
  Ansichten, hell/dunkel, Messklicks) und das Blättern Bild ↔ Modell ↔ Bild geprüft.
  Ziehen mit der Maus und das Gefühl der Bedienung nimmt der Benutzer ab.

## 9. Lizenzen und Datenschutz

Kein neues Paket und kein fremder Code → keine Telemetrie, keine neuen Einträge in
`THIRD-PARTY-NOTICES.md`. Verfahren aus Fachliteratur stehen im Code als Quelle:
Möller–Trumbore (Strahl/Dreieck), Ericson (nächste Punkte), Kåsa (Kreisausgleich),
Chapman bzw. Mittring (Umgebungsverdeckung im Bildraum).

## 10. Analyserunde (31.10.2026, 0.8.11 → 0.8.12-beta)

Wunsch des Benutzers: Sicherheit zuerst (keine Speicherüberläufe, beschädigte Dateien dürfen das System nicht verlangsamen,
keine Lücken), dann Speicher und Tempo. Prüfprogramme im lokalen Testordner (`stl-fuzz`, `stl-probe`, `stl-shot`).

**Sicherheit**

- Fuzzing des ganzen Ablaufs (Lesen, Nachbarschaft, Flächen, Suchbaum, Kanten, Schatten, Sortieren, 40 Strahlen, Messen):
  860 000 zufällig beschädigte Dateien (Bytes, Abschneiden, NaN/∞/Subnormal/±10⁹, falsche Anzahl, Einfügungen, Blöcke
  vertauscht) – kein Absturz, nur klare Meldungen, langsamster Fall 24 ms, Speicher höchstens 53 MB.
- Präparierte Dateien: 2 Mio. Dreiecke auf einer Kante, Fächer um einen Punkt, 2 Mio. deckungsgleiche Dreiecke, subnormale
  Koordinaten, riesiger Versatz, geometrische Reihe (Tiefe des Suchbaums), 2 Mio. getrennte Dreiecke, Nadeln, entartete
  Dreiecke, „20 Mio. angekündigt, 1 Mio. vorhanden“, „4 Mrd. angekündigt“, Text: 60 MB Eckzeilen ohne Facetten, eine
  100-MB-Zeile, nan/inf/1e999, 1000 Ecken je Facette, 50 MB Zufall. Gefunden und behoben: Strahlen und Bodenschatten bei
  deckungsgleichen Dreiecken (Fehlergedächtnis #112, #113), keine Grenze nach Arbeitsspeicher, unbemerkter Grafikspeicher-
  Mangel (#114). Danach dauert jede dieser Dateien höchstens 2,5 s im Hintergrund.
- Durchgesehen: Ganzzahlen (Anzahlen ≤ 20 Mio., Indizes ≤ 60 Mio., Puffergrößen als `nint`/`long`), Indizes an die
  Grafikkarte (vom `Mesh` geprüft, jeder < Eckpunktzahl), Kopfzeile nur druckbares ASCII, keine Rekursion, Hash mit
  zufälligem Startwert, Einstellungsdatei höchstens 64 KB.

**Speicher und Tempo** (Kugel mit 1 bzw. 5 Mio. Dreiecken, im echten Programm, AMD RX 6650 XT)

| | 1 Mio. | 5 Mio. |
|---|---|---|
| Arbeitsspeicher (ohne Modell 531 MB) | 642 MB | 1 109 MB (Spitze 1 286 MB) |
| verwalteter Speicher je Dreieck, dauerhaft | – | 55 Byte (vorher 66) |
| Modell auf dem Bildschirm nach | sofort | 0,34 s |
| Flächen und Messen bereit nach | 1,3 s | 4,0 s |
| Drehen | 62 Bilder/s | 62 Bilder/s (Bildwiederholrate) |
| Strahl unter der Maus | ~2 µs | ~3 µs |

Verworfen: Flächen und Suchbaum nacheinander statt gleichzeitig zu rechnen (Spitze etwas niedriger, aber 0,45 s später
messbereit bei 5 Mio.).

## 11. Später möglich

Vorschaubilder in der Dateiliste, Schnittebene, Wandstärke an beliebiger Stelle, Messwerte
kopieren, freies Drehen (Trackball) als Umschalter.
