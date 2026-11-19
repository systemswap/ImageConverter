# STL-Betrachter mit Messen

ImageConverter ist auch ein Bildbetrachter; er zeigt STL-Modelle an und misst Abstände
zwischen Flächen. Nur STL – STEP und andere
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
- `Geometry/` ohne Avalonia (Einlesen, Nachbarschaft, Flächen, Suchbaum, Messen, STL schreiben –
  voll getestet), `Editing/` (Bearbeiten, Abschnitt 13: der einzige Teil, der den Geometriekern
  kennt), `Rendering/` (OpenGL, Kamera, vorbereitete Daten), `Views/` (Ansicht, Maßlinien, Spalte,
  Gruppe BEARBEITEN, Rahmen der gezeichneten Flächen), `ModelViewModel` (Zustand,
  Hintergrundschritte, Auswahl, Messliste; das Bearbeiten in eigenen Teildateien je Werkzeug).
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
  Ausnahme in den Qualitätsstufen Mittel und Hoch: fein unterteilte Rundungen (unten).
- Gestaltung: ruhiger Verlauf als Hintergrund (folgt Hell/Dunkel), mattes Material,
  Hauptlicht von links oben, schwache Aufhellung von rechts (beide an die Kamera
  gebunden), Himmel-/Bodenlicht entlang der Hochachse, Glanz und Randlicht schwach.
  **Umgebungsverdeckung im Bildraum** (SSAO, halbe Auflösung, 16 Proben, geglättet):
  Ecken, Bohrungen und der Fuß eines Zapfens werden weich dunkler – unabhängig von der
  Modellgröße und ohne Rechenzeit auf dem Prozessor. Eine Verdeckung je Eckpunkt wurde
  verworfen: große ebene Flächen haben zu wenige Eckpunkte, um sie zu zeigen. Damit Flächen
  bei flachem Blick nicht schlagartig fleckig dunkel werden: Der Tiefendurchgang schreibt die
  genaue Flächennormale mit (statt sie aus den Nachbartiefen zu schätzen), der Mindestabstand
  der Proben wächst mit der Neigung der Fläche, die Verdeckung blendet bei fast streifendem
  Blick aus, und das Glätten mischt nur Nachbarn derselben Fläche (keine dunklen Säume über
  Umrisse hinweg).
  Weicher **Bodenschatten** aus der Draufsicht (256 × 256, geglättet), blendet beim Blick
  von unten aus. Feine Linien an scharfen Kanten (> 40°), Kantenglättung (MSAA).
- **Helligkeit** (Spalte › Ansicht, 50–150 %, gespeichert; Doppelklick auf den Wert = 100 %):
  heller hebt die Mitteltöne entlang einer Kurve an, so dass beleuchtete Flächen nicht weiß
  ausbrennen; dunkler skaliert das Licht herunter. Der Hintergrund bleibt.
- **Qualität** (Spalte › Ansicht, gespeichert, voreingestellt Hoch) – gegen Treppen an Kanten und
  auf Rundungen; die Geometrie (Umriss, Kantenlinien, Messen) bleibt in jeder Stufe die der Datei:

  | Stufe | Inhalt | Aufwand |
  |---|---|---|
  | Schnell | flache Facetten, Schnitt je Bildpunkt, Linien 1 px, 4× MSAA | am wenigsten, für langsame Rechner |
  | Mittel | alle Verfeinerungen (unten), 4× MSAA | kaum mehr als Schnell; Normalen einmal im Hintergrund |
  | Hoch | wie Mittel, 8× MSAA (wo die Grafikkarte es kann) | doppelte Arbeit an den Kanten des Bildes |

  Die Verfeinerungen:
  - *Rundungen glatt* (`SmoothShading`): Nur Flächen, die die Flächenerkennung als Zylinder
    oder Freiform erkannt hat, und nur, wo deren Facetten höchstens 5° von der Normalen an
    ihren Ecken abweichen (Zylinder ab 36 Facetten je Umdrehung). Zylinder bekommen die
    genaue Normale des erkannten Zylinders (radial zur Achse), Freiformflächen das nach dem
    Winkel gewichtete Mittel der Facetten. Ebenen bleiben exakt flach, Kanten zwischen
    Flächen scharf, grob unterteilte Teile facettiert wie in der Datei – nichts sieht runder
    oder weicher aus, als die Datei es beschreibt. Dafür bekommen die Ecken der Rundungen
    eigene Eckpunkte (je Fläche), die Eckpunkte der Datei behalten ihre Nummern (Kantenlinien).
  - *Schnittkanten geglättet*: Die Schnittflächen schneidet die Grafikkarte geometrisch ab
    (`gl_ClipDistance`) statt je Bildpunkt zu verwerfen – die Schnittkante wird geglättet
    wie jede andere Kante.
  - *Umgebungsschatten kantengenau*: An Tiefensprüngen (Umriss, Schnittkante) übernimmt jeder
    Bildpunkt die Verdeckung aus dem Bildpunkt halber Auflösung, der auf seiner Fläche liegt;
    sonst blutet die Verdeckung als Zwei-Pixel-Treppe über die Kante.
  - *Kantenlinien geglättet*: Jede Kante wird als schmales Band gezeichnet, das zum Rand weich
    ausläuft (Abdeckung eines Bildpunkts durch eine 1 px breite Linie); die Linie bleibt genau
    auf der Kante. Bis 4 Mio. Kanten, darüber 1-px-Linien.
  - *Dithering*: ein halber Helligkeitswert Rauschen gegen Stufen in langen Verläufen (8 Bit).

  Unter OpenGL ES (Windows mit ANGLE) fallen geometrisches Abschneiden und geglättete Linien
  auf die einfachen Wege zurück (dort fehlen `gl_ClipDistance` und `noperspective`). Gemessen
  am Gehäuse: Ebenen ändern sich höchstens um einen Helligkeitswert (Dithering), größere
  Unterschiede gibt es nur an Kanten und auf Rundungen (0,8 % der Bildpunkte).
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
- **Schnittflächen (Werkzeug), bis zu sechs gleichzeitig:** jede schneidet weg, was vor ihr liegt; zu sehen ist, was hinter
  allen liegt (zwei Schnitte stellen z. B. eine Ecke frei). „Schnittfläche anlegen“ (unten neben Messen, oder Spalte ›
  Schnitt › Anlegen), dann auf eine Fläche klicken – eine weitere Schnittfläche legt sich darauf (Normale ins Material, bei
  Ebenen deren genaue Richtung) und ist ausgewählt; gezeichnet als halbdurchsichtige Ebene mit Rand, die ausgewählte kräftiger.
  „Schnittfläche verschieben“ schaltet den Schiebemodus ein (nur über diesen Knopf): eine Fläche anfassen und ziehen (sie
  wird dabei ausgewählt und folgt der Maus entlang ihrer Richtung; blickt man senkrecht darauf, schiebt Ziehen nach oben
  tiefer), Mausrad über ihr fein (1 % des sichtbaren Ausschnitts). Ein Klick neben alle Flächen beendet den Schiebemodus,
  damit Klicks wieder auf Flächen gehen (Messen). Eine kleine Karte oben rechts im Bild („SCHNITTFLÄCHEN“ mit Anzahl)
  listet jede Schnittfläche: Klick auf die Zeile wählt sie aus (hinterlegt, Markierung links), rechts daneben Richtung
  (⊥ X/Y/Z oder schräg) und Tiefe in mm (Pfeile ± 0,1), ✕ entfernt sie; unten Neu, Verschieben, Alle ✕. Esc beendet zuerst die
  Werkzeuge, dann entfernt es alle Schnitte. Durchzoomen und Umschalt + Mausrad wirken auf die ausgewählte Schnittfläche
  (ohne eine legen sie eine neue parallel zum Bildschirm an).
- **Hineinsehen:**
  - *Wand für Wand durchzoomen* (orthografisch): das Mausrad vergrößert bis zu einer Bildhöhe von 2 % der Modellgröße;
    jede weitere Raste nimmt die nächste Wand unter der Maus weg – der Schnitt springt knapp hinter die Stelle, an der der
    Strahl die Wand verlässt (Fläche von innen gesehen). Herauszoomen lässt die Wand weg, so dass man sieht, was dahinter
    liegt (z. B. die Fläche eines Stegs, die zur Außenwand zeigt); Esc oder ✕ holt alle Wände zurück.
  - *Durchfliegen* (Perspektive): das Auge fährt entlang des Strahls unter der Maus; nah an einer Fläche mindestens
    0,5 % der Modellgröße je Raste, hinter der Fläche auf die nächste zu (oder geradeaus); die nahe Schnittebene schneidet
    weg, was davor liegt. Herauszoomen geht denselben Weg zurück, ebenfalls mit dieser Mindestschrittweite. Der Drehpunkt
    behält dabei seine Tiefe, wird beim Durchfliegen mitgeschoben und beim Herauszoomen auf die Tiefe der Modellmitte
    zurückgeholt.
  - *Umschalt + Mausrad* schiebt den Schnitt fein (2 % des sichtbaren Ausschnitts je Raste), Anzeige oben links in mm.
  - Der Schnitt wird parallel zum Bildschirm gesetzt und bleibt dann im Modell stehen – man kann um das Aufgeschnittene
    herumdrehen. Klicks und Überfahren treffen erst hinter dem Schnitt, der Bodenschatten ist dann aus.
  - **Innenseiten** (Rückseiten von Flächen – im Material oder an aufgeschnittenen Wänden) erscheinen warm grau; bei
    Dateien mit falsch herum gespeicherten Dreiecken sind auch Außenflächen so getönt.
- Knöpfe: Einpassen (F), Zentrieren (C: Modellmitte in die Bildmitte, Zoom und Blickrichtung bleiben), Iso, Vorne,
  Hinten, Links, Rechts, Oben, Unten (mit kurzem Übergang);
  Orthografisch/Perspektive (zum Messen orthografisch voreingestellt).

## 6. Flächen (`FaceFinder`)

STL kennt nur einzelne Dreiecke; eine gekrümmte Fläche kommt als Streifen ebener Facetten:

1. **Ebenen:** von den größten Dreiecken aus (ihre Normalen sind am genauesten) wachsen
   Nachbarn an, solange ihre Ecken in der Startebene liegen (2·10⁻⁵ der Modellgröße,
   mindestens einige Float-Schritte der Koordinaten).
2. **Facetten:** eine Ebene gilt als Facette einer gekrümmten Fläche, wenn mehr als ein
   Viertel ihres Randes sanft (≤ 40°) an Nachbarn stößt und sie nicht deutlich breiter ist
   als diese – so bleibt die Oberseite eines Quaders mit gerundeten Kanten eine Ebene.
3. **Zylinder:** benachbarte Facetten, deren Normalen alle senkrecht zu einer Achse
   stehen (Eigenvektor zum kleinsten Eigenwert von Σ A n nᵀ), die sich entlang von Kanten
   parallel zur Achse treffen und deren Ecken auf einem Kreis liegen (Kåsa-Ausgleich).
   Streng: Ecken eines CAD-Zylinders liegen bis auf die Rundung der Datei genau auf dem
   Kreis (binär: Float, Text: nach der Stellenzahl der Zahlen); ein offenes Stück muss
   mindestens 15° abdecken und entlang der Achse 2 % seines Radius lang sein. Geschlossen
   (Ecken rundum) → Ø, sonst Bogen → R. Nicht erkannt: Bögen aus zwei Streifen und
   Vielecke mit weniger als neun Ecken (die gelten als Ebenen).
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
| Ebene ∦ Ebene | Winkel, kürzester Abstand; bis 5° Neigung zuerst der Abstand am Klickpunkt |
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
  stehen, Zoom zur Maus, Einpassen), vorbereitete Daten, Texte, weiche Normalen (Quader und
  flache Kante bleiben flach, feines Rohr radial, grobes Rohr facettiert, Kugel), Qualitätsstufen,
  Bearbeiten (`SolidEditorTests`: Länge ändern entlang jeder Achse und schräg, Bohrung wandert mit,
  Kürzen nur im gleichförmigen Bereich, gleichförmige Bereiche über eine Naht hinweg, Quader
  hinzufügen und gespiegelt wegnehmen, Zylinder entlang jeder Achse, Fläche verschieben ganz und
  maskiert, offene Modelle abgelehnt, STL schreiben und wieder lesen).
- Die Bedienung des Bearbeitens wurde im Prüfprogramm mit echten Mausereignissen durchgespielt
  (Länge ändern, Körper andocken und frei verschieben, Fläche zeichnen, Griffe, Einrasten,
  Bemaßen von Kanten und Höhe, Fläche verschieben mit Maske), jeweils mit Volumen-Gegenrechnung.
- Die OpenGL-Ansicht wurde mit einem Prüfprogramm im echten Fenster aufgenommen (alle
  Ansichten, hell/dunkel, Messklicks) und das Blättern Bild ↔ Modell ↔ Bild geprüft.
  Ziehen mit der Maus und das Gefühl der Bedienung werden von Hand abgenommen.

## 9. Lizenzen und Datenschutz

Für das Bearbeiten: **ManifoldSharp** 0.6.0 (Apache-2.0) und **Clipper2** 1.5.4 (Boost Software
License 1.0), beide reines C# ohne native Teile und ohne Netzzugriff, eingetragen in
`THIRD-PARTY-NOTICES.md`, Lizenztexte in `packaging/licenses/`. Sonst kein fremder Code, keine
Telemetrie. Verfahren aus Fachliteratur stehen im Code als Quelle:
Möller–Trumbore (Strahl/Dreieck), Ericson (nächste Punkte), Kåsa (Kreisausgleich),
Chapman bzw. Mittring (Umgebungsverdeckung im Bildraum).

## 10. Analyserunde (31.10.2026, 0.8.11 → 0.8.12-beta)

Reihenfolge der Schwerpunkte: Sicherheit zuerst (keine Speicherüberläufe, beschädigte Dateien dürfen das System nicht verlangsamen,
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

## 11. Analyserunde Genauigkeit (31.10.2026, 0.8.14 → 0.8.15-beta)

Prüfmodelle mit exakt bekannten Maßen, je zwölfmal zufällig gedreht und 0 / 100 / 1000 / 10 000 / 100 000 mm vom Nullpunkt
verschoben, Facettierung 8–512 Segmente (Prüfprogramm `stl-fuzz accuracy`). Größte Abweichung:

| Maßart | 0 mm | 1 000 mm | 10 000 mm | 100 000 mm |
|---|---|---|---|---|
| Abstand paralleler Ebenen | 0,010 µm | 0,023 µm | 0,32 µm | 2,1 µm |
| Winkel zweier Ebenen | 0,008 m° | 0,04 m° | 0,33 m° | 3,9 m° |
| Durchmesser (9–512 Ecken) | 0,006 µm | 0,014 µm | 0,11 µm | 1,3 µm |
| Achsabstand paralleler Zylinder | 0,005 µm | 0,004 µm | 0,04 µm | 0,44 µm |
| Wandstärke eines Rohrs | 0,000 µm | 0,011 µm | 0,04 µm | 2,6 µm |
| Achse ↔ parallele Ebene | 0,002 µm | 0,004 µm | 0,21 µm | 1,3 µm |
| Radius einer Rundung (3–8 Streifen) | 0,03 µm | 0,36 µm | 6 µm | 82 µm |
| kürzester Abstand zweier Kugeln | 0,001 µm | 0,03 µm | 0,6 µm | 32 µm |

Die Abweichungen folgen der Auflösung der Datei (32-Bit-Float: etwa 6·10⁻⁸ der Koordinate); angezeigt wird auf 0,01 mm.
Gefunden und behoben: Schein-Zylinder auf Kugeln (#116), Rundungen an scharfen Kanten (#117); neu: Abstand am Klickpunkt für
leicht geneigte Ebenen (unter 0,1° gelten Ebenen als parallel, der Abstand ist dann der ihrer Mittelpunkte entlang der Normale).
Grundsätzlich: der kürzeste Abstand wird zwischen den Dreiecken gemessen – an gekrümmten Flächen liegen die Facetten innerhalb
der echten Fläche, er ist dort um bis zur Sehnenhöhe der Facettierung zu groß; Achsabstände, Durchmesser und Radien sind davon
nicht betroffen (der Kreis geht durch die Ecken).

## 12. Später möglich

Vorschaubilder in der Dateiliste, Schnittebene, Wandstärke an beliebiger Stelle, Messwerte
kopieren, freies Drehen (Trackball) als Umschalter. Beim Bearbeiten: Zeichnen auf schrägen
Flächen, Maskenrahmen mit der Maus ziehen, Körper drehen, Vielecke als gezeichnete Fläche.

## 13. Bearbeiten (Spalte › BEARBEITEN)

Nur für **geschlossene** Modelle (Kennzahl „Geschlossen: ja“). Jede Änderung erzeugt ein neues Netz,
das das alte ganz ersetzt; Flächen, Messen und Bild werden neu ermittelt, Maße gelöscht,
Schnittflächen bleiben. **Rückgängig/Wiederholen** (bis 30 Schritte) und **„Speichern unter …“**
(binäre STL; zuerst in eine `.part`-Datei, dann ersetzt – die Originaldatei bleibt unverändert).

**Werkzeuge**

| Werkzeug | Bedienung | Ergebnis |
|---|---|---|
| Länge ändern | mit „Messen“ zwei parallele Ebenen anklicken, am Maß „↔ Länge ändern“, neue Länge eintippen, wählen, welche Fläche stehen bleibt (grün; orange rückt weiter) | an einer Trennebene (orange): verlängern füllt die Lücke mit dem Querschnitt, kürzen nimmt den Streifen hinter der Ebene heraus. Die Ebene legt sich selbst in einen Bereich mit gleichem Querschnitt (`UniformStretches`), damit Schlitze, Bohrungen und Stege unverändert bleiben; kürzen wird abgelehnt, wenn der Streifen nicht gleichförmig ist |
| Körper hinzufügen oder wegnehmen | Quader („von–bis“ je Achse) oder Zylinder (Mitte, Durchmesser, Länge, Achse), wahlweise gespiegelt an der Modellmitte (X); im Bild anfassen: dockt an die Fläche darunter an (aufsetzen oder einsenken, seitlich an Wände), Umschalt + Ziehen frei | Vereinigung bzw. Differenz |
| Fläche verschieben | ebene Fläche anklicken (Überfahren zeigt sie), ziehen oder Abstand eintippen; **Maske** (orange) begrenzt den verschobenen Teil (von/bis in den beiden Richtungen der Fläche, Datei-Koordinaten); rastet an parallelen Flächen ein | die (maskierte) Fläche entlang ihrer Normalen: + aufbauen, − wegnehmen. Verdeckte Flächen (z. B. zur Wand hin) mit einer Schnittfläche freilegen |
| Fläche zeichnen | Rechteck oder Kreis auf einer ebenen Fläche quer zu einer Achse aufziehen (Ecken rasten an Wänden ein, Umschalt frei); Liste „GEZEICHNETE FLÄCHEN“ unten rechts; Höhe/Tiefe, „▲ Aufbauen“, „▼ Wegnehmen“ (grün/rot in der Vorschau) | die Fläche als Prisma vereinigt bzw. abgezogen; die gezeichnete Fläche bleibt in der Liste |
| … verschieben, Griffe | gezeichnete Fläche anfassen und ziehen (Kanten rasten ein, Umschalt frei); Rahmen mit 8 Griffen (Kreis: 4): Kanten docken an, Ecke mit Umschalt **zu Beginn** proportional bis zum Loslassen | Lage und Größe |
| … bemaßen | „📐 Bemaßen“: Kante (Kreis: Mitte) oder Fläche selbst (Höhe) anklicken, dann Wand, Kante oder Fläche des Modells, Abstand eintippen | Kante bzw. Boden des Wegnehmens/Oberseite des Aufbaus rückt genau auf das Maß. Bezug exakt aus einer sichtbaren Modellkante oder einer Ebene quer zur Messrichtung, sonst „ungefähr“ (angeklickter Punkt) |

**Genauigkeit**

- Der Kern rechnet doppelt genau; Ein- und Ausgabe in einfacher Genauigkeit wie die STL-Datei selbst.
  Nicht berührte Eckpunkte behalten ihre Werte; bei Achsen-Richtungen dreht der Kern exakt (Faktoren
  0/±1). Werkzeuge reichen 1 µm über die Fläche, an der sie beginnen (fügen sich an, keine Haut).
- Zylinder als Vieleck mit höchstens 1 µm Abstand zum Kreis (⌀ 3 mm: 86 Ecken, 0,09 % Fläche weniger).
- Ziehen mit der Maus rundet auf 0,01 mm (angezeigte Einheit); Einrasten innerhalb von 8 Bildpunkten
  genau auf die Lage einer Ebene (Wände, Stegseiten).
- Am Gehäuse nachgerechnet: Steg bis zur Wand +126,0000 mm³ (2 × 0,75 × 28 × 3), Fläche verschoben
  +63,0000, Mitte maskiert −69,6000, Schlitz 0,4 × 2 × 1,5 mm −1,2000, verlängert um 5 mm +Querschnitt ×
  5 auf 4 Nachkommastellen.

**Aufbau**

- `Editing/SolidEditor*.cs` – Kern-Anbindung (`SolidEditor.cs`: Netz ↔ Kern, Drehung auf Z, Prüfen),
  `.Length.cs` (Länge, gleichförmige Bereiche), `.Bodies.cs` (Quader/Zylinder), `.Faces.cs` (Umriss
  einer Fläche, Maske, Verschieben); `Editing/Body.cs`. Nur hier steht ManifoldSharp – ein anderer Kern
  ließe sich dort tauschen.
- `ModelViewModel.Editing.cs` (Rückgängig, Speichern, Netz tauschen, Länge ändern), `.Bodies.cs`,
  `.FaceMove.cs`, `.Sketches.cs` (Liste, Aufbauen/Wegnehmen), `.SketchPlacement.cs` (Verschieben,
  Einrasten, Bemaßen), `.SketchGrips.cs` (Rahmen, Größe); `SketchItem.cs` (gezeichnete Fläche).
- `Views/EditPanel.axaml` (Gruppe BEARBEITEN), `Views/ModelGlView.Editing.cs` (Maus der Werkzeuge),
  `Views/SketchOverlay.cs` (Rahmen und Griffe in 2D), `ModelRenderer` (Vorschau-Körper, Trennebene,
  neue Szene nach einer Änderung).
- Der Kern wurde vor der Wahl gegen das C++-Original (Manifold 3.5.4, selbst gebaut) verglichen: gleiche
  Volumen auf 4 Nachkommastellen, größter Abstand der Ergebnisse 0,002 µm; das Original ist rund
  sechsmal schneller (Gehäuse mit 20 000 Dreiecken: 206 gegen 36 ms) – bei Bedarf hinter derselben
  Schnittstelle austauschbar.
