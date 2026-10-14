# ImageConverter — Konzept

**Ziel:** Schlanker, schnell bedienbarer Bildkonverter für Linux (primär) und Windows.
Positionierung: Alternative zu *Referenzwerkzeug* (GNOME) — gleiche Einfachheit, aber mit
Zuschneiden, Schärfen und Rauschreduktion.

**Leitsatz:** Ein Fenster. Bild rein, 3–4 Regler, Export. Alles Weitere versteckt sich
hinter „Mehr". Wenn ein Feature die Startansicht verkompliziert, kommt es in v2.

---

## 1. Anforderungen, geordnet

### A. Formate (I/O)

| Gruppe | Lesen | Schreiben | Bemerkung |
|---|---|---|---|
| Klassisch | JPEG, PNG, BMP, GIF, TIFF | JPEG, PNG, BMP, TIFF | Pflicht |
| Moderne Web | WebP, AVIF, JPEG XL | WebP, AVIF, JPEG XL | „das neuere Web-Format" = WebP; AVIF/JXL gleich mitnehmen |
| Kamera/Apple | HEIC/HEIF, RAW (CR2, NEF, ARW, DNG) | — | nur Import, Decoder-Abhängigkeit |
| Icons/Vektor | ICO, SVG (rastern) | ICO | SVG-Import nice-to-have |

Pro Format eigene Encoder-Optionen: Qualität (JPEG/WebP/AVIF/JXL), lossless-Schalter
(WebP/JXL), Kompressionsstufe (PNG), Chroma-Subsampling, Progressive/Interlaced,
Bittiefe (8/16), Alpha-Behandlung inkl. Hintergrundfarbe beim Flatten.

### B. Skalieren

- **Zielgröße**: Pixel (B×H). Entschieden: kein Modus-Auswahlfeld in der
  Einzelbild-Ansicht — die Kette reicht (zu = proportional, offen = frei).
  „Längste Kante" und Prozent kommen erst mit dem Batch-Modus zurück, wo sie
  gebraucht werden (gemischt Hoch- und Querformat).
- **Proportional / nicht-proportional**: Kettensymbol zum Sperren des Seitenverhältnisses.
- **Verhalten bei gesperrter Kette (wichtig):** Ich gebe *eine* Kante ein, die andere
  wird sofort passend nachgerechnet und als abgeleiteter Wert angezeigt (ausgegraut
  oder mit Hinweis), während ich noch tippe bzw. beim Verlassen des Feldes.
  - Breite eingetippt ⇒ `höhe = round(breite / seitenverhältnis)`, und umgekehrt.
  - Seitenverhältnis wird **einmal beim Laden** aus dem Original (bzw. nach Zuschnitt
    aus der Auswahl) festgehalten, nicht nach jedem Rundungsschritt neu berechnet —
    sonst driftet das Verhältnis beim Hin-und-Her-Tippen weg.
  - Rundung: mindestens 1 px, nie 0. Rundungsfehler ≤ 1 px akzeptieren statt die
    eingegebene Kante zu korrigieren — die vom Nutzer getippte Zahl bleibt exakt.
  - Rekursionsschutz: das Nachrechnen darf kein erneutes Nachrechnen der ersten Kante
    auslösen (Flag `isUpdating` im ViewModel).
  - Kette *nachträglich* geschlossen ⇒ Verhältnis der aktuellen Werte übernehmen,
    keine Werte springen lassen.
  - Bei Prozent/„längste Kante"/Megapixel folgen immer **beide** Felder und sind
    read-only.
- Bei entsperrter Kette: freie Verzerrung, beide Felder unabhängig. Zusätzlich Modi
  *Fit* (einpassen, Rand) und *Fill/Cover* (füllen, beschneiden).
- ~~**Nicht vergrößern**-Schalter~~ — entfallen in 0.5.77 beta: vergrößert wird, wenn die Maße es sagen.
- **Verfahren** (Dropdown, Standard Lanczos3, seit 0.5.77 beta je nach Richtung):
  beim Verkleinern Lanczos3, Bicubic, Catmull-Rom, Mitchell, Spline, Bilinear, Box/Area, Nearest;
  beim Vergrößern dieselben ohne Bilinear und Box, darunter die KI-Scaler (§1 N).
- Skalierung in **Linear-Light** rechnen (gamma-korrekt) — sichtbar bessere Ergebnisse,
  als Option „Gamma-korrekt skalieren".

### C. Auflösung & DPI

- Pixelmaße (= eigentliche Auflösung) und **DPI/PPI** getrennt bedienbar.
- Zwei Modi — seit 0.5.77 beta über die **Einheit** statt eigener Schalter:
  1. Einheit **Pixel**: nur Metadaten — DPI-Tag umschreiben, Pixel bleiben (ändert Druckgröße).
  2. Einheit **mm**: neu berechnen — physische Zielgröße × DPI ⇒ neue Pixelmaße.
- In der Gruppe „Größe“ (eigene Gruppe „Auflösung & DPI“ entfallen), nur das Zahlenfeld, Erklärung im Tooltip.
- Anzeige der resultierenden Druckgröße in cm/inch live mitlaufen lassen.

### D. Zuschneiden (Referenzwerkzeug-artige Auswahl) — Kernfeature

- Rechteck-Overlay auf dem Bild mit **8 Griffen** (4 Ecken + 4 Kantenmitten),
  Innenfläche zum **Verschieben** per Drag.
- Außenbereich abgedunkelt, Drittel-Raster als Overlay.
- Seitenverhältnis-Zwang: frei, 1:1, 4:3, 3:2, 16:9, eigenes, „wie Original";
  Umschalten per Dropdown, temporär per Shift beim Ziehen.
- Numerische Felder für X/Y/B/H, Einrasten an Bildkanten, Pfeiltasten = 1 px,
  Shift+Pfeil = 10 px.
- Buttons: **Auf Auswahl zuschneiden**, Auswahl zurücksetzen.
- Zoom (Strg+Mausrad), Pan (Strg + Ziehen; Auswahlrahmen-Knopf aus = Ziehen verschiebt), „Einpassen", 100 %.
- Auswahl wird in **Bildkoordinaten** gehalten, nicht in Bildschirmpixeln — sonst
  verschiebt sich der Zuschnitt beim Zoomen.

### E. Bildverbesserung

- **Schärfen**: Unsharp Mask mit Radius / Stärke / Schwelle. Voreinstellungen
  „Web sanft", „Druck", „Stark". Optional nur auf Luminanz (YCbCr-Y), damit keine
  Farbsäume entstehen. Immer **nach** dem Skalieren anwenden.
- **Rauschreduktion klassisch**: bilateral / Non-Local-Means / Wavelet — schnell, ohne Modell.
- **Rauschreduktion & Upscale per KI** (optionales Modul, s. §5):
  kleines ONNX-Modell, CPU-taugliche Laufzeit, Download on demand statt im Bundle.
- Basis-Korrekturen: Helligkeit, Kontrast, Sättigung, Gamma, Auto-Levels.
- **Rotieren/Spiegeln**: 90°-Schritte, freie Rotation, EXIF-Orientierung automatisch
  anwenden.

### F. Batch

- Mehrere Dateien / Ordner (optional rekursiv) per Drag & Drop.
- Alle Einstellungen gelten für die Liste; Zuschneiden im Batch nur mit
  relativer Auswahl (%) oder bei gleicher Bildgröße.
- **Namensschema** mit Platzhaltern: `{name}_{breite}x{höhe}.{ext}`, Zähler `{n}`,
  Datum. Ziel: gleicher Ordner / Unterordner / frei gewählt.
- Konfliktverhalten: überschreiben / umbenennen / überspringen.
- Fortschritt pro Datei, Fehler sammeln statt abbrechen, Parallelisierung über Kerne.

### G. Metadaten & Farbe

- EXIF/IPTC/XMP: **behalten / entfernen / nur GPS entfernen** (Privacy-Default
  für Web-Export: entfernen).
- ICC-Profil: behalten, in sRGB konvertieren, oder verwerfen. Default für Web: sRGB.
- Rotations-Tag beim Export normalisieren.

### H. Lokalisierung

- Deutsch, Englisch; Struktur für weitere Sprachen (fr, es, it, nl, pl, pt, tr, uk, zh).
- Sprache aus System-Locale, im UI überschreibbar. RTL-Layout vorbereiten (ar, he).
- Übersetzbar sind auch Einheiten, Zahlenformate und Dateigrößen.

**Umgesetzt (festgelegt am 08.10.2026):** statt `.resx` wie gettext — der deutsche Text
ist der Schlüssel. XAML: `Text="{l:T 'Bilder hierher ziehen'}"`, C#: `Tr.T("…")`,
Listen mit deutschen Werten zeigen über `ComboBox.tr` bzw. `{l:TItem}` die Übersetzung.
Deutsch und Englisch stehen im Code (`Core/Localization/English.cs`), weitere Sprachen
als JSON in `languages/` neben dem Programm (Code, Name in der Sprache selbst, Flagge
als Streifen oder PNG, Texte); fehlende Texte erscheinen auf Englisch. Einstellungen →
Tab „Sprache“: Combobox mit Flagge und Name, wirkt sofort, gespeichert in
`settings.json` im Benutzerordner; „Vorlage erstellen“ schreibt `_vorlage.json` mit
allen Texten. Neue Texte brauchen einen englischen Eintrag — sonst schlägt
`LocalizationTests.English_CoversEveryText` fehl. Der Ordner `konvertiert` behält
seinen Namen in allen Sprachen.

### I. Desktop-Integration

- **Linux**: `.desktop`-Datei mit `MimeType=image/jpeg;image/png;…` ⇒ erscheint unter
  „Öffnen mit" im Nautilus/Dolphin/Thunar-Rechtsklick. Dateien als Argumente annehmen
  (`%F`), mehrere Dateien ⇒ Batch-Liste. Icon nach Freedesktop-Spec.
- **Windows**: Einträge unter `HKCU\Software\Classes\SystemFileAssociations\<ext>\shell`
  (benutzerweit, keine Admin-Rechte) + „Öffnen mit"-Registrierung.
- Drag & Drop aus dem Dateimanager ins Fenster.
- Optional CLI-Modus (`imageconverter --resize 1920 --quality 85 *.jpg`) — fällt
  praktisch gratis an und macht die App skriptbar.

> **Stolperstein AppImage:** Ein AppImage registriert von sich aus *keinen*
> Rechtsklick-Eintrag — der Dateimanager kennt die App erst, wenn eine `.desktop`-Datei
> unter `~/.local/share/applications/` liegt. Lösungen: (a) AppImageLauncher/appimaged
> beim Nutzer, (b) Erst-Start-Dialog „In Dateimanager integrieren?", der die
> `.desktop`-Datei selbst schreibt, (c) zusätzlich **Flatpak** anbieten, wo die
> Integration automatisch funktioniert. Empfehlung: (b) umsetzen *und* Flatpak als
> zweite Auslieferungsform einplanen.

### J. Drucken (Wunsch vom 07.10.2026)

- Neben „Exportieren" ein **„Drucken"** — gedruckt wird in der eingestellten Größe
  (Pixel bzw. mm bei der gewählten DPI), mit Schärfe/Entrauschen wie beim Export.
- **Mehrere Bilder:** Auswahl zwischen
  - **„So viele wie möglich auf eine Seite"** — Bilder in ihrer Druckgröße auf der Seite
    anordnen (Reihen von links nach rechts, mit einstellbarem Abstand), neue Seite erst,
    wenn keins mehr passt,
  - **„Jedes Bild auf eine eigene Seite"**.
- **Druckrand berücksichtigen:** nichts in den Bereich legen, den der Drucker nicht
  bedrucken kann; zusätzlich einstellbarer eigener Rand.
- Papierformat (A4, A5, A3, Letter, 10×15 …), Hoch-/Querformat, Anzahl Kopien.
- Bild größer als die bedruckbare Fläche: Hinweis plus Option „auf Seite verkleinern".
- **Seitenvorschau** mit Rand und Anordnung, bevor gedruckt wird.

Technik (Vorschlag): Avalonia hat keine Druckfunktion. Die Anordnung der Bilder auf
den Seiten rechnet der Core (plattformunabhängig, testbar). Daraus entsteht pro Seite ein
Bild in Druckauflösung bzw. ein PDF mit exakten physischen Maßen. Ausgabe unter Linux über
CUPS (`lp`), unter Windows über den Windows-Druckdienst.

### K. Körnung (Wunsch vom 12.10.2026, umgesetzt in 0.5.53 beta – siehe Fehlergedächtnis #45)

- Eigene Gruppe **KÖRNUNG** als **letzte** Gruppe unter den Reglern (nach FARBE) —
  die Gruppen stehen in der Reihenfolge, in der sie wirken. Unabhängig von und
  zusätzlich zur Körnung in „Haut & Details“, nicht an die Luminanzmaske gebunden,
  in „Bearbeitungen“ ausschaltbar.
- Wird **immer zuletzt** gerechnet: nach Entrauschen und Schärfen, auch nach KI und
  „Haut & Details“, egal welche Reihenfolge KI ↔ Einstellungen gewählt ist (sonst
  glättet das eine das Korn wieder weg).
- Regler: **Stärke**, **Korngröße** (0,1 px … grob; ab 0,25 px Pixel für Pixel, darunter
  feiner als ein Pixel: ohne grobe Häufungen, 0.5.66), **Rauheit** (gleichmäßig …
  klumpig wie Film), **Farbanteil** (0 = reines Helligkeitskorn), **Härte** (weiche
  Tupfen … scharf begrenzte Körner, körniger bei gleicher Stärke; 0.5.66).
- Verteilung wie bei Film: Mitteltöne am meisten, tiefes Schwarz und reines Weiß
  fast keins (Stärke als Funktion der Helligkeit wie beim Kornprofil in
  `FaceDetails`).
- **„Aus dem Foto ermitteln“**: misst das Korn im Original (vor dem Entrauschen) und
  stellt Stärke, Korngröße und Farbanteil ein; alle drei bleiben danach von Hand
  verstellbar. Die vorhandene Messung (`FaceDetails.MeasureGrain`, nur Helligkeit)
  wird dafür um das Farbkorn ergänzt.
- Korn fest je Bildpixel (gleiches Muster bei jeder Rechnung): kein Flimmern,
  Vorschau = Export. Korngröße bezogen auf die Ausgabegröße, damit es beim
  Verkleinern gleich aussieht.

### L. Gewölbtes Blatt gerade ziehen (Wunsch vom 12.10.2026, Schritt 1 – von Hand – in 0.5.54 beta, Schritt 2 – Vorschläge ohne Modell – in 0.5.55 beta, genauer und mit senkrechten Linien in 0.5.56 beta, siehe Fehlergedächtnis #46–#48)

Fotografiertes Blatt oder Buch wölbt sich, gerade Linien werden zu Bögen. Recherche
(Stand 10/2026): Die KI-Verfahren (UVDoc 2023, DocTr++, DocRes 2024, DocMatcher,
ForCenNet, DvD 2025, Axis-Aligned Dewarping, BookNet 2026) arbeiten vollautomatisch,
schneiden auf das Blatt zu und lassen sich nicht nachbessern; keines nimmt Vorgaben
des Benutzers. Interaktiv gibt es nur Teile: Referenzwerkzeug Advanced (obere und untere
Kurve), Referenzwerkzeug „Adaptive Weitwinkelkorrektur“ (Linien nachziehen, Gitter wird
optimiert; Carroll/Agrawala/Agarwala 2009). Das echte Seitenverhältnis liefert nur ein
Blattmodell im Raum (page_dewarp, Zucker 2016; Kim/Koo/Cho 2015; Kil u. a. 2017).
Daher: eigenes Werkzeug, das beides verbindet.

**Bedienung** — eigenes Symbol oben in der Vorschau (neben „Entzerren“), Lupe beim
Ziehen wie dort.
- **Ansehen (Vorgabe):** Ziehen verschiebt das Bild (auch eingepasst, bis jede Stelle zur
  Mitte kommt — Ränder unter der Leiste bleiben erreichbar), Ziehen an einem **Punkt**
  verschiebt den Punkt. **Klick auf eine Linie** (oder einen ihrer Punkte) schaltet das
  Zeichnen ein, die Linie wächst am näheren Ende weiter; das **×** daneben entfernt sie.
- **„Zeichnen“** (Schalter, auch Taste N): beginnt immer eine neue Linie; jeder Klick setzt
  einen Punkt auf die **aktive Linie**; **Enter** oder Doppelklick beginnt die nächste,
  **Esc** beendet das Zeichnen und hebt die Markierung auf. Esc verlässt das Werkzeug nie
  (0.5.68) — verlassen nur über das Symbol oben, sonst wären die Linien weg. Klick auf den
  Verlauf einer Linie fügt dort einen Punkt ein. Pfeiltasten 1 px (Umschalt: 10 px), Entf
  löscht den Punkt, Umschalt+Entf (bzw. Entf auf markierter Linie) die Linie.
- Jede Linie hat eine **Richtung**: *waagerecht* (Textzeile, Ober-/Unterkante),
  *senkrecht* (Seitenkante, Spalte) oder *Falz* (Doppelseite). Wird aus dem Verlauf
  geraten, per Rechtsklick umschaltbar; je Richtung eine Farbe. Blattränder sind
  gewöhnliche Linien – geht also auch ohne sichtbaren Rand.
- **„Vorschlagen“**: Vorschläge gestrichelt; jede Änderung (Punkt ziehen, weiterzeichnen,
  Richtung, Pfeiltasten) macht einen zur eigenen Linie. Erneutes Vorschlagen ersetzt nur
  unberührte Vorschläge, eigene Linien bleiben, und was auf einer eigenen Linie liegt,
  wird nicht noch einmal vorgeschlagen. Was nicht zum Blattmodell passt, wird gleich
  weggelassen (kein Abwählen mehr, seit 0.5.68).
- Leiste unten: Vorschlagen · Vorschläge weg · Zeichnen · Zurücksetzen · „Gerade ziehen“.
- **Nach „Gerade ziehen“** (0.5.69) bleibt das Werkzeug offen und zeigt das Ergebnis, unten Größe,
  Seitenverhältnis und **„Justieren“** — wieder das Original mit den verwendeten Linien. Das Symbol
  oben öffnet bei schon gerade gezogenem Bild direkt beim Justieren (ein **Punkt am Symbol** zeigt
  das an), ebenso der **Stift** an der Zeile „Gerade gezogen“ in „Bearbeitungen“. Rückgängig bleibt
  ein gewöhnlicher Schritt. Verwendete Linien kommen als eigene zurück; „Vorschlagen“ ergänzt nur,
  was nicht schon auf ihnen liegt.

**Rechnung**
1. **Blattmodell im Raum:** Kamera (Brennweite aus EXIF, sonst ≈ 28 mm KB), Lage des
   Blatts, Blatt als in einer Richtung gebogene Fläche (abwickelbar, wie Papier),
   Biegung als Kurve mit mehreren Stützpunkten. Doppelseite: zwei solche Flächen, am
   Falz verbunden, dort mit Knick. Waagerechte Linien liegen auf dem Blatt bei
   v = konst., senkrechte bei u = konst., der Falz bei u = 0. Gelöst mit
   Levenberg-Marquardt (Abstand der Linienpunkte zur Projektion).
2. **Feinkorrektur mit Gitter:** was das Modell nicht trifft (welliges Papier), wird
   nachgezogen, bis jede Linie gerade ist; dazwischen möglichst unverzerrt.
3. **Seitenverhältnis** aus dem abgewickelten Blatt. Liegt es **höchstens 3 %** neben
   DIN (1 : √2, gilt für A4, A5, A6 … hoch und quer), wird DIN genommen.
4. **Ganzes Bild bleibt erhalten:** die Verformung läuft außerhalb des Blatts weich
   aus; danach wird auf das **größte volle Rechteck** zugeschnitten (keine leeren
   Ecken).
5. Nur **eine** Linie: nur sie wird gerade gezogen, Seitenverhältnis bleibt.
6. **Eigene Stufe im Rezept** vor dem Entzerren:
   Wölbung → Entzerren → Gerade richten → Zuschnitt → Drehen/Spiegeln. Ausschaltbar,
   immer aus dem Original, Linien relativ gespeichert. Vorschau rechnet nach jedem
   gezogenen Punkt auf einer verkleinerten Fassung neu.

**Vorschläge** (später, s. Reihenfolge)
- Ohne Download: Textzeilen (wie page_dewarp), Blattrand, Falz, gezeichnete Linien (auch
  schwache wie Karoraster), Ränder von Balken, farbigen Kästen und Fotos, Textränder.
  Seitenverhältnis nur, wenn die senkrechten Linien wenigstens 60 % der Höhe überspannen.
- **KI begradigen** (eigenes Symbol neben „Gewölbtes Blatt“, beide in einem Rahmen; 0.5.59):
  SyntheticDoc (ECCV 2026, UVDoc-Nachfolger, 31 MB ONNX, MIT) sagt das Gitter 121 × 177 der
  Rückabbildung voraus — ein Klick, keine Linien, eine Stufe im Rezept (dieselbe wie
  „Gewölbtes Blatt“, ersetzt dessen Linien), ein zweiter Klick nimmt sie heraus. Stark bei
  Knittern, Bons in der Hand, Büchern; bei glatten Arbeitsblättern ist das Linienverfahren
  10- bis 30-mal genauer. Seitenverhältnis aus unserem Blattmodell über die Gitterlinien
  (DIN-Einrasten), ersatzweise Bogenlängen. Die App lädt den Original-Checkpoint und baut
  die ONNX-Dateien selbst (C#, ohne Python); Training und Datenerzeugung liegen in
  `extern/SyntheticDoc/`, Anleitung und Entfernen-Liste in `docs/KI-BEGRADIGEN.md`.
  **Schatten entfernen** (0.5.64, voreingestellt an, Rechtsklick auf das Symbol): die
  Schattenkarte des Modells wird beim Rendern herausgerechnet; die App merkt sich die Wahl.
- Mit **UVDoc** (MIT, ≈ 31 MB ONNX, nach `KI-Modelle/`): liefert ein Gitter, daraus
  einige waagerechte und senkrechte Linien als Vorschläge; Doppelseite am Falz
  geteilt, jede Hälfte einzeln (UVDoc kennt nur Einzelseiten). Eingabe nur
  ≈ 712 × 488 – Näherung zum Nachbessern. Lizenz der Gewichte vor dem Einbau prüfen.

**Reihenfolge der Umsetzung:** (1) Werkzeug von Hand mit Richtungen, Falz, Rechnung,
Rezept, Tests · (2) Vorschläge ohne Modell · (3) Vorschläge mit UVDoc.

### M. Doppelseite trennen (Wunsch vom 12.10.2026, noch nicht umgesetzt)

- Eigenes Symbol oben in der Vorschau: setzt eine senkrechte **Trennlinie**, die sich
  wie die Vorher/Nachher-Linie verschieben lässt (Vorschlag: am erkannten bzw.
  gesetzten Falz, sonst in der Mitte).
- Gespeichert wird wahlweise als **zwei Bilder** oder als **PDF mit zwei Seiten**
  (PDF-Ausgabe wie beim Drucken).
- Offen: Dateinamen der zwei Bilder (z. B. `-1`/`-2`), ob die Trennung als Stufe im
  Rezept gilt (ausschaltbar) oder nur beim Speichern wirkt, PDF-Seitengröße (DIN aus
  der Schätzung?).

### N. Upscaling (Wunsch vom 14.10.2026, Konzept mit Messplatz und Modellwahl — umgesetzt in 0.5.76 beta, siehe `docs/KI-UPSCALING.md`)

**Ziel:** Vergrößern auf dem Stand der Technik, ohne etwas Neues ins Bild zu erfinden — das
Original muss ohne Weiteres wiederzuerkennen sein. Vorbild Referenzwerkzeug/Referenzwerkzeug „Precision“
(laut Hersteller: Auflösung höher, Inhalt und Aufbau bleiben, keine neuen Elemente). Wunsch
als Messlatte: Panorama aus 5000+ px auf 600 px verkleinert, wieder vergrößert, sieht aus
wie das Original — idealerweise mit Nadeln und Blättern im Wald.

**Was physikalisch geht (ehrlich):** Auf 600 px sind einzelne Nadeln und Blätter nicht mehr im
Bild, nur ihre mittlere Farbe und Struktur. Jedes Verfahren, das sie „zurückbringt“, malt
*plausible* Nadeln — nicht die echten. Das ist unvermeidlich und auch bei Referenzwerkzeug/Referenzwerkzeug so.
Sauber wird es durch eine **Treue-Sicherung** (unten): das Ergebnis muss, wieder verkleinert,
genau das Original ergeben. Dann ist alles Gemalte feiner als ein Originalpixel, und das
Bild bleibt in jeder Größe, in der es vorher zu sehen war, dasselbe.

**Ort in der Oberfläche (entschieden 14.10.2026):** eigene Gruppe **„UPSCALING“ im allgemeinen
Tab direkt unter „GRÖSSE“** (dort liegen die meistgenutzten Funktionen). „Größe“ legt weiter
die Pixel fest, ihr Filter gilt fürs Verkleinern und für den letzten Schritt auf die genaue
Zielgröße; „Upscaling“ bestimmt, *wie* vergrößert wird. Aktiv, sobald das Ziel größer als das
Bild ist (sonst ausgegraut, Hinweis „nur beim Vergrößern“; steht „Nicht vergrößern“ an, sagt
die Gruppe das und löst den Haken mit einem Klick).
- **Verfahren** (ComboBox): Filter · KI sofort (x4v3, der bisherige) · KI mittel
  (UltraSharpV2) · KI wie Referenzwerkzeug (PiSA-SR) — hinter jedem Eintrag die geschätzte Zeit für das
  aktuelle Bild; nicht heruntergeladene Modelle sichtbar, aber ausgegraut („im Tab KI
  herunterladen“).
- **Knopf „Upscalen“:** rechnet nur auf Abruf, nie bei jeder Pixeländerung. Gerechnet wird das
  Bild, wie es zu sehen ist (mit Zuschnitt, Drehen, Entzerren, Gewölbt, KI, Entfernen), vor
  Dunst/Licht/Farbe — die wirken danach live. Wer nur einen Ausschnitt will, schneidet vorher zu.
- **Ein Lauf genügt:** das Modell rechnet fest ×4; jede Zielgröße darunter entsteht danach in
  Sekundenbruchteilen aus diesem Ergebnis (Lanczos). Pixeländerungen rechnen also nicht neu,
  nur eine Änderung am Eingangsbild oder ein anderes Modell.
- **Zustand** neben dem Knopf: „berechnet (4×)“ · „veraltet – Bild geändert“ · „nicht berechnet“.
- **„Ausschnitt testen“:** nur der sichtbare Teil in 1:1 (Sekunden statt Minuten), Vergleich mit
  Vorher/Nachher.
- **Beim Speichern** mit gewähltem, aber nicht berechnetem oder veraltetem Upscaling Rückfrage
  „Upscaler aktiv – jetzt berechnen (≈ … min)?“ · Ja · Ohne KI speichern · Abbrechen.
- **Jedes Bild:** gilt wie jede Einstellung je Bild bzw. bei „Gleiche Einstellungen für alle“
  für alle; die Rückfrage nennt dann die Gesamtzeit.
- **Fortschritt:** Balken mit Restzeit über alle Kacheln (bei PiSA-SR drei Abschnitte, nach
  Messung gewichtet); fertige Kacheln erscheinen sofort im Bild, der Rest bleibt blass;
  Abbrechen jederzeit (auch mitten in einer Kachel). Feinheit endet bei der Kachel — in Ordnung.
- **Regler, alle live nach dem einen Lauf:** **Details** (0–100 %: Mischung der Feinstruktur des
  Modells mit der Filter-Vergrößerung) · **Treue** (0–100 %: Stärke der Rückprojektion; 100 % =
  verkleinert genau das Original). Korn: die Modelle glätten es weg — Gruppe Körnung („Aus dem
  Foto ermitteln“) füllt es in Ausgabepixeln wieder auf (Hinweis in der Gruppe).
- Die Vergrößerung im Tab KI („2× / 4×“ beim Allround-Modell) zieht hierher um; der Tab KI bleibt
  fürs Restaurieren (Rauschen, Artefakte, Gesichter).
- **Keine feste Größengrenze** (Wunsch: auch sehr große Ergebnisse): gerechnet wird in Kacheln;
  vor dem Start prüft die App den Arbeitsspeicher und nennt Zeit und Bedarf; nur wenn es nicht
  passt, schlägt sie ×2 oder den Filter vor.
- **Nur lokal, nur ONNX** (Wunsch; Referenzwerkzeug & Co. nur als Vergleich, nie eingebaut). Mindestens
  CPU; AMD-Grafik: unter Windows über DirectML (`Microsoft.ML.OnnxRuntime.DirectML`, jede
  DirectX-12-Karte), unter Linux ROCm/MIGraphX oder WebGPU prüfen.

**Stelle in der Bildkette:** ersetzt beim Vergrößern das Skalieren: Pixelbild nach Ausrichten →
Modell ×4 → Treue-Sicherung → mit Lanczos genau auf die Zielgröße → Entrauschen, Dunst, Licht und
Farbe, Schärfen, Körnung wie bisher (Körnung in Ausgabepixeln). Das Modellergebnis wird je Bild
zwischengespeichert (Schlüssel: Eingangsbild, Modell). Vorschau: ab 100 % Zoom der echte
Ausschnitt, darunter verkleinert.

**Treue-Sicherung (Rückprojektion):** Ergebnis E, Original O, Verkleinern V, Vergrößern U:
E ← E + U(O − V(E)), drei Durchgänge (V = Lanczos, U = bikubisch). Danach gilt V(E) ≈ O —
keine Farbverschiebung, keine verrutschten Kanten, kein neuer Gegenstand größer als ein
Originalpixel. Sekundenbruchteile, unabhängig vom Modell. Gemessen: hebt die Übereinstimmung
verkleinert/Original bei allen Modellen von 28–50 dB auf 49–65 dB und behebt die Tonverschiebung
von PiSA-SR (roh dunkler und orangestichig) vollständig.

**Entschieden: drei Modelle** (Messplatz 14.10.2026, vom Benutzer gewählt):

| Platz | Modell | Größe | Lizenz | CPU-Zeit 0,15 MP → 2,4 MP |
|---|---|---|---|---|
| KI sofort | Real-ESRGAN general x4v3 (schon im Katalog) | 4 MB | BSD-3 | 3 s |
| KI mittel | 4x-UltraSharpV2 (DAT, 05/2025, Kim2091), ONNX fp32 opset 17 | 52 MB | CC BY-NC-SA 4.0 (privat in Ordnung) | 2 min |
| KI wie Referenzwerkzeug | PiSA-SR (CVPR 2025, SD 2.1-base + Dual-LoRA, ein Schritt) | ≈ 5,2 GB fp32 (2,6 GB fp16) + 33 MB LoRA | Code Apache-2.0, SD 2.1 OpenRAIL++, LoRA ohne Angabe | 7,5 min |

**Messplatz** (`~/.cache/imageconverter-build/pisa/`, Skript `werkzeug/messplatz.py`): zwei
Ausschnitte des Panoramas (2000 × 1200, Kiefernnadeln und Waldhang) auf 500 × 300 verkleinert und
zurückgerechnet; Bilder `TestBilder/Merge/upscale-vergleich-{nadeln,hang}.jpg`,
`upscale-ohne-treue-nadeln.jpg`.

| Verfahren (+ Treue) | Zeit | Nadeln dB | Hang dB | Eindruck |
|---|---|---|---|---|
| Lanczos | sofort | 25,6 | 34,4 | matschig |
| x4v3 | 3 s | 25,7 | 34,9 | knapp besser als Lanczos |
| 4xNomos2 RealPLKSR | 40 s | 24,1 | 34,0 | Kleckse, Plastik — verworfen |
| 4xNomos2 DAT2 | 2 min | 23,7 | 33,9 | scharf, aber wurmige Strukturen — verworfen |
| 4x-UltraSharpV2 | 2 min | 26,2 | 35,2 | treu und natürlich |
| PiSA-SR | 7,5 min | 23,2 | 31,4 | einzelne Nadeln und Wipfel, glaubwürdig — am nächsten am Wunsch |

dB = PSNR gegen den echten Ausschnitt; das Maß bevorzugt Weiches, gemalte (nicht pixelgenaue)
Feinstruktur wird bestraft — entschieden wird am Bild. Ein überzeugendes Modell für ≈ 20 s gibt
es nicht (RealPLKSR schlechter als x4v3).

**PiSA-SR als ONNX (noch zu bauen):** Modus „Standard“ (LoRA in die UNet eingerechnet, ein
UNet-Lauf je Kachel); leere Bildbeschreibung → Textteil einmal vorab gerechnet, RAM/CLIP nicht
nötig. Drei ONNX-Teile: VAE-Encoder, UNet (merged), VAE-Decoder; Kacheln im Latentraum 96 mit
Überlappung 32, VAE in Kacheln (Gruppennorm über alle Kacheln wie im Original-Code). Ablauf:
Eingang bikubisch ×4 → Encoder → UNet (t = 1) → x − Vorhersage → Decoder. Getestet in
PyTorch fp32 auf der CPU (Original-Code, `cuda` → `cpu` umgestellt). Offen: Umwandlung in C#
(wie SyntheticDoc) oder einmalig mit Python; fp16 für die GPU; Download der Grundgewichte
(Stable Diffusion 2.1 nur noch über Spiegel, z. B. `Manojb/stable-diffusion-2-1-base`).

**Quelldateien gesichert (14.10.2026):** SD 2.1-base UNet und VAE (fp16, Spiegel), PiSA-SR-LoRA, die
vorgerechnete leere Beschreibung, der PiSA-SR-Code (Apache 2.0) und UltraSharpV2 liegen unter
`published/windows-x64/KI-Modelle/{pisasr,ultrasharpv2}` und einmal als Modell-Sicherung (1,8 GB,
mit `HERKUNFT.md`: Quellen, SHA-256, Lizenzen) neben den Code-Sicherungen — Stability AI hat SD
2.0/2.1 2025 von Hugging Face und GitHub zurückgezogen, der Lizenztext (OpenRAIL++-M, 24.11.2022)
stammt deshalb aus der ScanCode-Lizenzdatenbank. Daraus baut die App ihre ONNX-Dateien selbst
(wie SyntheticDoc); ein besseres Modell kann einen Platz später ersetzen.

**Bewusst nicht:** Online-Dienste (Referenzwerkzeug, Referenzwerkzeug, Referenzwerkzeug-Cloud) — nur lokal; SUPIR, TSD-SR
(zu groß, praktisch nur mit Grafikkarte); Prompt- oder „Kreativ“-Modi.

**Umgesetzt (0.5.76 beta):** alle drei Verfahren, Treue und Details, Knopf mit Rückfrage (ganzes Bild oder sichtbarer
Ausschnitt), Prüfansicht in voller Auflösung mit fertigen Kacheln schon während der Rechnung und Vorher/Nachher
(Filter ↔ KI), Rückfrage beim Speichern (auch für mehrere Bilder), Modelle im Download-Manager. PiSA-SR ohne Python:
Nachbau in C#, gegen PyTorch geprüft. Noch nicht: Grafikkarte (DirectML/ROCm), Speicherprüfung vor dem Rechnen, das
2×/4× im KI-Tab bleibt vorerst daneben bestehen.

**Neu gestaltet (0.5.77 beta, Wunsch vom 14.10.2026):** eine Gruppe „Größe“ — Einheit + Formate, Maße mit Kette, DPI (nur
das Feld; die Einheit entscheidet: Pixel → nur Druckgröße, mm → Pixel), Liste „Verfahren“ (je nach Richtung; Filter oben,
unter „KI-Scaler“ die KI), Gamma, Treue/Details (nur KI), Knopf „Skalieren“ für jedes Verfahren (Ergebnis in voller Größe in
der Vorschau), Druckgröße. Weggefallen: Gruppen „Upscaling“ und „Auflösung & DPI“, Filter-Liste, DPI-Regler und -Knöpfe,
„Nicht vergrößern“ (vergrößert wird, wenn die Maße es sagen), Prüfansicht als Schalter, Größe im KI-Tab.

**Offen:** ×2-Modelle (weniger Zeit bei kleinem Faktor)? „Upscaling“ auch ohne Vergrößerung als
„Details schärfen“ (Supersampling wie „Größe behalten“ der KI)? Gesichter zusätzlich mit dem
Gesichtsmodell? Gewicht der PiSA-Regler (λpix/λsem kosten einen zweiten UNet-Lauf) statt
„Details“ als Mischung?

---

## 2. Was ein einfacher Konverter noch braucht (von dir nicht genannt)

1. **Live-Vorschau mit Vorher/Nachher** — umgesetzt als Trennlinie zum Schieben (0.4.37):
   links Vorher, rechts das Ergebnis; Zoom und Ausschnitt gelten für beide.
2. **Geschätzte Ausgabedateigröße**, live bei Qualitätsänderung — das ist der Grund,
   warum man so ein Tool überhaupt öffnet.
3. **„Auf Dateigröße zielen"** — Qualität automatisch so wählen, dass z. B. < 500 KB.
4. **Presets** (eigene, speicherbar): „Web 1920 / WebP 80", „E-Mail", „Druck 300 dpi".
5. **Undo/Redo** für Zuschnitt und Regler.
6. **Großbild-Tauglichkeit**: Vorschau auf Proxy rechnen, Vollauflösung nur beim Export;
   Tiling für sehr große TIFFs, sonst frisst es GB an RAM.
7. **Verlustfreie Operationen**, wo möglich: JPEG um 90° drehen ohne Neukodierung,
   PNG-Optimierung (oxipng-artig).
8. **Animierte Bilder**: GIF/WebP/APNG — mindestens nicht kaputt machen, besser
   alle Frames gleich skalieren.
9. **Barrierefreiheit**: komplett tastaturbedienbar, Fokus-Ringe, Kontrast,
   Screenreader-Labels. Dunkles/helles Thema dem System folgend.
10. **Fehlerkultur**: defekte Datei ⇒ klare Meldung, Rest des Batches läuft weiter.
11. **Keine Telemetrie, offline-fähig** — klar kommunizieren.

---

## 3. UI-Entwurf

```
┌──────────────────────────────────────────────────────────────┐
│  [+ Bilder]   bild_01.jpg ▾ (3 Dateien)        ☰ Einst.   ? │
├───────────────────────────────┬──────────────────────────────┤
│                               │  Format   [WebP      ▾]      │
│      ┌ ─ ─ ─ ─ ─ ─ ─ ┐        │  Qualität [────●──] 82       │
│      │  Auswahl-     │        │           ≈ 412 KB (−78 %)   │
│      ○  Rahmen mit   ○        │                              │
│      │  8 Griffen    │        │  Größe  [1920] × [1080] px   │
│      └ ─ ─ ─ ○ ─ ─ ─ ┘        │         🔗 proportional      │
│                               │         [Längste Kante  ▾]   │
│                               │  Filter [Lanczos3       ▾]   │
│   Zoom 68 %  ⟲ ⟳  ◧ Vor/Nach  │  DPI    [ 300 ] ○Tag ○neu    │
│                               │  Schärfen  [──●───] 0.6      │
│                               │  Rauschen  [●─────] aus      │
│                               │  ▸ Mehr (Metadaten, Farbe…)  │
├───────────────────────────────┴──────────────────────────────┤
│ ☐ Auf Auswahl zuschneiden    Ziel: …/converted  [Exportieren]│
└──────────────────────────────────────────────────────────────┘
```

Rechte Spalte ist die ganze Bedienung; links nur Bild + Auswahlrahmen.
Bei geschlossener Kette ist das *nicht* zuletzt bearbeitete Größenfeld gedimmt —
so sieht man auf einen Blick, welcher Wert abgeleitet ist.
Auf Linux dem GNOME-Look folgen (Headerbar statt Menüleiste), unter Windows
native Titelleiste.

### Kachelleiste (festgelegt am 08.10.2026, umgesetzt in 0.4.24)

Die Einstellungen stehen inzwischen links neben dem Bild. Ganz links eine schmale
Kachelleiste (nur Icons); `»` klappt sie auf und zeigt Name und Inhalt jeder Rubrik.
Ziel: schlank bleiben, nicht aufblähen.

| Kachel | Inhalt |
|---|---|
| Standard | Format, Zuschnitt, Größe, Auflösung & DPI, Schärfen & Rauschen |
| KI | KI-Verbesserung (Modelle mit mehr Gesichtsdetails prüfen) |
| Tools | Helligkeit, Kontrast, Tiefen, Lichter (Local Laplacian Filter wie Referenzwerkzeug), Dunst (Dark Channel Prior, Luftlicht je Bereich, Farbton bleibt, Sonnenhof geschützt; Durchläufe 1–10, Vorgabe 3 sachte; Feineinstellung Klarheit/Kontrast/Dynamik/Schärfe/Kanten/Helligkeit/Entstufen; in 32 Bit gerechnet, Regler bis 300 — Stand 0.4.45 ist die Referenz), Dynamik, Sättigung, Färben (mit Farbton), geplant: „Gleiche Farbe“ (Referenzwerkzeug CS5), evtl. KI gegen JPEG-Artefakte — **je Funktion nur ein Regler** |
| Ausgabe | Metadaten, Farbprofil, Dateiname |

„Gleiche Einstellungen für alle Bilder“ bleibt oben über den Rubriken, unabhängig von
der gewählten Kachel, und erscheint nur bei mehreren Bildern. Zieht man im Bild einen
Ausschnitt auf, springt die Leiste auf „Standard“ (dort liegt der Zuschnitt).

---

## 4. Technik-Empfehlung

**Du hast schon:** Avalonia 12.1 / .NET 10 mit Heads für Desktop, Android, iOS, Browser.

**Empfehlung: Avalonia behalten, aufräumen.**

- `ImageConverter.Android`, `.iOS`, `.Browser` **löschen** — die App ist ein
  Desktop-Werkzeug mit Dateisystem-, Rechtsklick- und Batch-Fokus. Mobile/WASM
  kostet nur Kompromisse (kein Dateizugriff, keine native Bildbibliothek).
- Übrig: `ImageConverter` (UI + ViewModels), `ImageConverter.Desktop` (Host),
  neu `ImageConverter.Core` (Bildpipeline, UI-frei, testbar),
  neu `ImageConverter.Tests`.
- Strikte Trennung: Core kennt kein Avalonia. Damit sind CLI-Modus und
  automatisiertes Testen der Pipeline gratis.

**Alternative, falls dir GNOME-Integration wichtiger ist als Windows:**
GTK4 + libadwaita (Rust oder Python) — so ist Referenzwerkzeug gebaut, sieht auf GNOME
perfekt aus, Flatpak-Pfad ist ausgetreten. Preis: Windows-Support wird mühsam.
→ Da du Windows ausdrücklich willst, bleibt Avalonia die richtige Wahl.

**Bildbibliothek — die wichtigste Entscheidung:**

| Option | Formate | Größe | Lizenz |
|---|---|---|---|
| **libvips (NetVips)** | breit, sehr sparsam mit RAM, sehr schnell | mittel | LGPL |
| **Magick.NET** | sehr breit (AVIF, HEIF, JXL, RAW über Delegates) | groß (~50–100 MB native) | Apache-2.0 |
| ImageSharp | gut, aber kein AVIF/JXL/HEIC | klein, rein managed | Six-Labors-Split-Lizenz — bei kommerzieller Nutzung prüfen |
| SkiaSharp | schnell, Formatpalette begrenzt | mittel | MIT |

→ Vorschlag: **libvips/NetVips** als Pipeline-Kern (RAM-Verhalten bei großen Bildern
ist deutlich besser als alles andere, Resampling-Filter inkl. Lanczos vorhanden),
**Magick.NET** als Fallback-Decoder für exotische Formate. Falls nur *eine*
Abhängigkeit sein darf: Magick.NET. ImageSharp nur, wenn die Lizenz für dich passt.

**Weitere Bausteine**

- MVVM: `CommunityToolkit.Mvvm` (ist schon drin) — passt.
- Lokalisierung: `.resx` + generierte Keys; kleine `Loc`-Markup-Extension für
  Laufzeit-Sprachwechsel.
- KI-Modul: `Microsoft.ML.OnnxRuntime` (CPU), Modelle als separater Download.
- Tests: xUnit auf `ImageConverter.Core`, Referenzbild-Vergleiche (PSNR-Schwelle).
  Die Proportional-Logik aus §B ist reine Rechnerei ⇒ erste Unit-Tests dort.

**Auslieferung**

- Linux: **AppImage** (self-contained Publish + `linuxdeploy`), zusätzlich
  **Flatpak** für saubere Integration. `linux-x64`, später `linux-arm64`.
- Windows: einzelne, self-contained `.exe` (portabel) + optional MSIX/Inno-Setup
  für die Rechtsklick-Registrierung.
- `PublishTrimmed` prüfen; NativeAOT wäre schön, kollidiert aber ggf. mit
  Reflection in Avalonia-Bindings — erst messen, dann entscheiden.
- Realistische Größe: 40–90 MB je nach Bildbibliothek. Für ein AppImage ok,
  aber ein Grund, RAW/HEIC-Decoder optional nachladbar zu machen.

---

## 5. KI-Modul (bewusst klein halten)

**Entschieden: Inferenz über ONNX** (`Microsoft.ML.OnnxRuntime`, CPU-Provider als
Standard; DirectML unter Windows bzw. CUDA optional, falls vorhanden). Alle Modelle
liegen als `.onnx`-Datei vor und laufen komplett lokal.

Nicht im Hauptbundle, sondern „Modell herunterladen (ca. X MB)" beim ersten Nutzen.

- **Rauschreduktion**: SCUNet oder DnCNN/FFDNet als ONNX — klein (wenige MB),
  CPU-tauglich bei moderaten Bildgrößen.
- **Upscale**: Real-ESRGAN in den *compact*-Varianten oder Waifu2x —
  die kompakten Modelle laufen auf CPU in Sekunden statt Minuten.
- Verarbeitung in **Tiles mit Overlap**, sonst RAM-Explosion und sichtbare Kanten.
- Immer Fortschritt + Abbrechen; immer Vorher/Nachher.
- Erwartungsmanagement im UI: „langsam, für einzelne Bilder gedacht".

Für v1 reicht klassische Entrauschung — KI ist v2.

### Festgelegt am 07.10.2026 (noch nicht umgesetzt)

**Bedienung im Hauptfenster**
- Keine Checkbox, sondern ein **Button „KI anwenden“** — läuft nur auf Abruf, nie in der Live-Vorschau.
- **ComboBox zur Modellwahl** daneben: Allround (Rauschen/Artefakte/Upscale), große Variante,
  Gesichtsmodelle (GFPGAN/CodeFormer) — angeboten werden nur heruntergeladene Modelle.
  Seit 0.5.11 auch GFPGAN 1.3 und RestoreFormer++ (beide Apache-2.0). Vergleich an drei
  Familienfotos (Gesichter 137–443 px, Handy 2012/2016): GFPGAN 1.4 und 1.3 fast gleich — glatt,
  „Weichzeichner“-Haut, 1.3 einen Hauch weicher; RestoreFormer++ die meisten Details (Poren, Haare),
  aber härter, macht Kinder älter (Falten an Wangen); CodeFormer bleibt am nächsten an der Person,
  60 % guter Mittelweg, 90 % fast Original. Zeit je Bild: GFPGAN ~5 s, RestoreFormer++/CodeFormer ~9 s.
- ~~**Verkettung:** Jeder Lauf wirkt auf das *aktuelle* Bild, also auf das Ergebnis des vorigen
  KI-Laufs, nicht wieder auf das Original.~~ Seit 0.5.9: **keine Verkettung** — ist das Bild das
  Ergebnis des letzten KI-Schritts, rechnet der nächste Lauf auf dem Bild davor und ersetzt es
  (KI auf KI machte Gesichter immer glatter). Ablauf: „KI berechnen“ → Stärke/Hautstruktur live
  (ohne neues Rechnen), Natürlichkeit (CodeFormer) rechnet neu → „Übernehmen“ oder „Verwerfen“.
- **Detailregler nach der KI** (seit 0.5.19, nur Gesichtsmodelle, alle live): Hautstruktur (feinste
  Stufe aus dem Original), Falten (mittlere Stufe, nur Haut, nur wo Original und KI übereinanderpassen),
  Körnung (Menge, 100 % = bis zum Korn des Fotos aufgefüllt) und Korngröße (×1 = gemessen), „Karte
  zeigen“ (rot: Falten kommen zurück, blau: bleibt vom Modell). Analyse vor der KI: Gesichtsbereiche
  (BiSeNet) und Kornprofil des Fotos; nach der KI: Vergleich mit dem Ergebnis.
- **Reihenfolge KI ↔ Einstellungen** (seit 0.5.10, Einstellungen = Entrauschen, Tools/Dunst, Schärfe;
  nie doppelt): „Erst KI, dann Einstellungen“ — KI auf dem Bild ohne Look, die Regler wirken danach
  auch auf die Gesichter und bleiben verstellbar (Vorschau zeigt sie schon über dem offenen Ergebnis).
  „Erst Einstellungen, dann KI“ — der Look wird in voller Größe ins Bild gerechnet, das Modell bekommt
  das fertige Bild (Gesichter zuletzt), beim Übernehmen gehen die Regler im selben Schritt auf null
  (Feineinstellungen bleiben); Strg+Z bringt Bild und Regler zusammen zurück. Nur mit Einstellungen
  je Bild, nicht bei „Gleiche Einstellungen für alle Bilder“ mit mehreren Bildern.
- **Rückgängig/Wiederholen** wie beim Zuschneiden: Ergebnis wird als eigener Schritt im
  Verlauf zwischengespeichert (Snapshot), Strg+Z/Strg+Y funktionieren.
- Fortschritt + Abbrechen während der Berechnung (Tiles mit Overlap).

**Modelle verwalten (Menü ☰ oben rechts → Einstellungen, eigener Tab „KI-Modelle“)**
- Liste aller Modelle mit Zweck, Größe, Lizenz und Status (fehlt / vorhanden).
- Download per Klick, automatisch von der Quelle — der Nutzer muss nichts suchen.
- **Fortschrittsanzeige je Modell**, Download jederzeit **abbrechbar** (keine halben Dateien
  zurücklassen: erst in `.part` schreiben, nach Prüfung umbenennen).
- Ablage im Ordner **„KI-Modelle“ neben der AppImage bzw. der .exe**; die App erkennt
  vorhandene Modelle beim Start und nach jedem Download automatisch.

### Umgesetzt in 0.4.14 (Testmodul)

- **Eigene GroupBox „KI-Verbesserung“**, bewusst als abtrennbares Modul gebaut. Entfernen:
  Ordner `ImageConverter.Core/Ai/` und `ImageConverter.Ui/Ai/`, die Zeile `<ai:AiPanel …/>`
  in MainView, den Tab in SettingsWindow, die Eigenschaft `MainViewModel.Ai`, das Paket
  `Microsoft.ML.OnnxRuntime` und `AiTests.cs`. Was im Hauptprogramm bleibt, ist allgemein
  (vorübergehende Anzeige `OverrideBitmap`, Hinweis `ActivityText`, `ApplyImageEditAsync`).
- **Modelle** (nur bekannte Lizenzen, feste Release-Adressen, SHA-256 geprüft):
  Allround = Real-ESRGAN general x4v3 (4,3 MB, BSD-3, ONNX von Qualcomm AI Hub, feste
  Kachel 128); GFPGAN 1.4 (325 MB, Apache-2.0); CodeFormer (360 MB, S-Lab, nur
  nicht-kommerziell); YuNet-Gesichtserkennung (0,2 MB, MIT) — die drei von FaceFusion-Assets.
- **Verworfen: Real-ESRGAN x4plus** („Fotos – hohe Qualität“, 66 MB): auf der CPU
  ~400 s pro Megapixel (512 × 512 in 107 s). Ohne Grafikkarte unbrauchbar.
- **Geschwindigkeit (16 Kerne, CPU):** Allround ~14–17 s pro Megapixel; Gesichter ~2 s
  pro Gesicht plus ~0,4 s Erkennung.
- **Fortschritt:** Balken + Restzeit in der GroupBox und als Hinweis über dem Bild. Das
  Vorschaubild baut sich während der Berechnung neu auf (alle 0,4 s): fertige Kacheln
  zeigen das neue Ergebnis, der Rest ist blass. Bei Gesichtern nach jedem Gesicht.
- **Gesichter:** fünf Punkte → Ähnlichkeitstransformation auf die FFHQ-Normlage (512 px),
  weicher Rand beim Zurücksetzen. Ist das Gesicht im Bild größer als 512 px, bleiben die
  feinen Originaldetails erhalten (sie können im Ausschnitt nicht vorkommen).
- **„Größe behalten“** rechnet in 4× und mittelt blockweise zurück (Supersampling);
  2× / 4× vergrößern bis höchstens 80 Megapixel.
- Mögliche nächste Schritte: GPU (DirectML unter Windows), Vorher/Nachher auch für
  Bearbeitungsschritte, CodeFormer-„Treue“ als Regler.

### Rückgängig / Wiederholen (0.4.15)

- **Ein Schritt pro Bedienvorgang, kein Zeitfenster mehr.** Folgeänderungen (verknüpfte
  Höhe, neu berechnete Pixel bei DPI) gehören zum selben Schritt.
- **Regler und Eingabefelder:** aufeinanderfolgende Vorgänge am selben Element bleiben ein
  Schritt; abgeschlossen wird er durch ein anderes Element, Rückgängig, Bildwechsel,
  Bearbeiten oder Export. Kein Timer nötig — die offene Änderung ist Rückgängig sofort zugänglich.
- **Auswahlrahmen:** jedes Loslassen der Maus ein Schritt. **Haken/Listen:** jeder Klick einer.
- **Bildschritte** (Zuschnitt, Drehen, KI): ein Schritt vorher → nachher.
- **Schneller:** Anzeige-Bitmaps der letzten Bildstände (bis 400 MB) und je vier Ergebnisse
  der Vorschaustufen bleiben im Speicher. Das Herunterrechnen für die Vorschau kostet bei
  10 MP 1,6–2 s, Entrauschen ~0,8 s — beim Hin und Her zwischen Ständen jetzt nicht mehr.
  Gleiches Bild, gleiche Größe: Zoom und Ausschnitt bleiben (vorher/nachher vergleichen).

---

## 6. Neuaufbau der Bildkette (beschlossen am 08.10.2026, ab 0.4.46 beta)

**Referenz:** 0.4.45 (= 0.4.44 beta ohne den Zusatz „Beta“). Jede Stufe des Neubaus
muss sie im Auge und in der Messumgebung mindestens erreichen; die Exe liegt als
`ImageConverter-0.4.45-Referenz.exe` neben der aktuellen, sobald sich die Bildkette ändert.

**Warum:** Beim Dunst zeigte sich, dass die Methode trägt (Dark Channel Prior,
kantentreue Glättung, Luftlichtfeld, Farbe in Oklab, Himmel- und Sonnenhofschutz),
der Datenweg aber nicht: 8 Bit zwischen den Schritten gab Treppen, physikalisches
Modell und „Look“ waren vermischt (Dynamik wirkte doppelt), und der Himmel wurde an
vielen Stellen einzeln geschützt.

1. **Durchgehend 32-Bit-Fließkomma in linearem Licht.** Magick.NET liest schon mit 16 Bit
   (Q16) — diese Werte direkt übernehmen statt erst auf 8 Bit (`ToBgra`). Alle Schritte
   (Dunst, Tools, Tiefen/Lichter, Schärfen, Entrauschen, Skalieren) arbeiten auf einem
   gemeinsamen Pixelpuffer; 8 Bit nur für die Anzeige und beim Speichern in 8-Bit-Formate
   (mit Dither). Export zusätzlich als 16-Bit-PNG/TIFF.
2. **Modell und Look trennen.** Stufe A rechnet nur die Physik (Luftlicht, Durchlässigkeit,
   Rückrechnung, Helligkeitsausgleich). Stufe B ist der Look (Klarheit, Kontrast, Dynamik,
   Schärfe) — genau einmal je Durchlauf, an einer Stelle, mit einem Regler je Wirkung.
3. **Himmel zuerst.** Eine Szenenkarte (Himmel, Sonnenhof, Landschaft, weiche Übergänge)
   wird einmal am Anfang bestimmt und von allen Schritten gelesen, statt dass jeder
   Schritt seinen eigenen Schutz rechnet.
4. **Luftlicht hierarchisch.** Grob über das ganze Bild, dann fein in überlappenden
   Fenstern mit weicher Gewichtung, die feine Stufe nur als begrenzte Korrektur der
   groben (kleine Fenster ohne Himmel/Dunst finden allein keinen Bezug — Farben kippten).
5. **Messumgebung als feste Tests.** Synthetischer Dunst mit bekannter Wahrheit plus
   echte Bilder (Turmlandschaft, Panorama, Berglandschaft): Wiese hinten (Farbton/Buntheit),
   Sonnenstrahlen (Schwankung im Ring), Treppen im Himmel, Detail, Kontrast — jede
   Änderung wird gegen die Referenz geprüft.

**Entschieden (08.10.2026):** Puffer in linearem Licht (wie Referenzwerkzeug; Kurven und Farbe
je Pixel über Oklab). 16-Bit-Export zuerst als PNG und TIFF; JPEG XL/AVIF mit 10/12 Bit
später als eigener Punkt.

**Umgesetzt in Phase 1 (0.4.49 beta), mit einer Abweichung:** Der Puffer (`Pixels`) ist
32-Bit-Fließkomma, die Werte aber **sRGB-kodiert**, nicht linear. Kurven, Schärfen,
Tiefen/Lichter und Dynamik sind auf kodierte Werte abgestimmt und so abgenommen — linear
gespeichert hätten sie anders ausgesehen; die physikalischen Schritte (Dunst, Skalieren)
rechnen ohnehin intern linear. In 32 Bit ist beides gleich genau. Linear-native Schritte
kommen mit dem Dunst-Neubau (Phase 4), dann wird die Kodierung neu entschieden. Magick
liefert Q16 direkt in den Puffer (`Pixels.FromMagick`), zurück als 16-Bit-Bild
(`ToMagick16`); 8-Bit-Formate bekommen beim Kodieren einmal Dither (`DitherTo8Bit`,
exakte Stufen bleiben unverändert — unbearbeitete 8-Bit-Bilder bleiben pixelgenau).
16-Bit-Quellen bleiben bei PNG/TIFF 16 Bit, sonst Haken „16 Bit je Kanal“.

**Stufenplan** — jede Phase endet mit einer Beta, die der Benutzer mit dem Auge abnimmt,
bevor die nächste beginnt. Ab Phase 1 liegt `ImageConverter-0.4.45-Referenz.exe` neben der
aktuellen Exe, Vergleichsbilder je Version unter `vergleich/<Version>` im Build-Ordner.

| Phase | Version | Inhalt | Abnahme |
|---|---|---|---|
| 0 Sicherheitsnetz | 0.4.48 beta | Werkzeug `tools/Abnahme`: Testbilder und Fälle rechnen, messen, mit der Referenz vergleichen; Referenzlauf mit dem Code von 0.4.45 | Prüflauf gegen 0.4.45: alles ≈ |
| 1 Durchgehend 32 Bit | 0.4.49 beta | Gemeinsamer Pixelpuffer (Fließkomma, linear), Q16 direkt aus Magick; Dunst/Entstufen, Tools, Tiefen/Lichter, Schärfen umgestellt; 8 Bit nur für Anzeige und 8-Bit-Formate; Export 16-Bit-PNG/TIFF | Abweichung < 1 Tonstufe außer gewollt; Treppen ≤ Referenz |
| 2 Speicher & Tempo | 0.4.50 beta | Puffer wiederverwenden, Kacheln, SIMD; Abbrechen mitten in der Rechnung; Fortschritt auch in der Vorschau | 28,7 MP × 3: < 3 GB, < 10 s; 80 MP läuft |
| 3 Szenenkarte | 0.4.51 beta | Himmel, Sonnenhof, Landschaft einmal am Anfang; alle Schritte lesen sie | Sonne und Himmel wie 0.4.45 |
| 4 Dunst neu gegliedert | 0.4.52 beta | Stufe A Physik (Luftlicht hierarchisch), Stufe B Look an einer Stelle | kein Messwert schlechter; Auge des Benutzers |
| 5 Abschluss | Meilenstein (z. B. 0.5.0) | Linux-Bau, Doku, Zip-Sicherung | Benutzer |

**Stand (09.10.2026):**
- Phase 0 ✓ — Prüflauf gegen 0.4.45 byte-gleich.
- Phase 1 ✓ — Puffer sRGB-kodiert statt linear (siehe oben); Abweichung ≤ ΔE 0,38 im Mittel.
- Phase 2 teilweise — 28,7 MP × 3: 13,9 s / 3,7 GB (vorher 19,5 s / 6,4 GB); Ziel < 10 s / < 3 GB
  nicht erreicht. Offen: Local Laplacian (Speicherzugriff), Farbumrechnung je Pixel im Dunst,
  Entstufen (Speicherlatenz). Vorschau bricht zwischen Schritten ab und zeigt den Schritt.
- Phase 3 ✓ — `SceneMap`, Bild byte-gleich.
- Phase 4 ✓ — `DehazeLook`; Vorgaben auf 0.4.45 abgestimmt
  (ΔE 0,3–0,7 bei Dunst 100), Luftlicht aus überlappenden Fenstern; das Luftlicht war schon
  dreistufig (Bild → 8 Streifen → 6 × 4 Kacheln, jede Stufe nur begrenzte Korrektur).
- Phase 5 — **Meilenstein 0.5.0** (vom Benutzer am 09.10.2026 als Meilenstein bestimmt): Doku und
  Zip-Sicherung erledigt; der Linux-Bau (`./publish.sh linux` auf dem Debian-Rechner) steht noch aus.
  0.5.0 ist die neue Referenz für Abnahme und Vergleich.

**Abnahme-Werkzeug** (`tools/Abnahme`, nicht in der Solution): Arbeitsordner
`<LocalAppData>/imageconverter-build/abnahme` (oder `IC_ABNAHME`) mit `abnahme.json`
(Testbilder, Messbereiche), Testbilder unter `bilder/` — Bilder kommen nie ins Repo.
`abnahme referenz <name>` / `pruefen <name>` / `leistung <name>` / `raster`. Gemessen:
Kontrast, Helligkeit, Farbton/Buntheit/Detail der Ferne, Treppen im Himmel, Sonnenstrahlen
(im Verhältnis zum Original), Abweichung zur Referenz (ΔE nach 3×3-Mittel, damit Dither
nicht zählt), bei künstlichem Dunst der Abstand zur Wahrheit. ✗ = schlechter als die
Referenz, ↑/↓ = geändert, ≈ = gleich. Die Referenz wird mit dem Core der Referenzversion
gebaut (`-p:CoreProject=…` auf einen Worktree).

**Leistung (Ziel: flüssige Bearbeitung, auch bei großen Bildern):**
- **Echter Fortschritt** (umgesetzt in 0.4.46 beta): Beim Speichern ein Balken, der
  einmal von 0 bis 100 % über alle Schritte läuft (Größe, Rauschen, jeder Dunst-Durchlauf,
  Tiefen/Lichter, Licht und Farbe, Schärfen, Farbprofil, Datei schreiben), darunter der
  laufende Schritt; im Export-Fenster „Bild n von m · Schritt“. Vorher lief eine
  Endlos-Animation ohne Text. Aufwand je Schritt geschätzt (`WorkPlan`, Sekunden je MP) —
  die neue Bildkette meldet ihre Schritte genauso, auch für lange Vorschau-Rechnungen.
- Vorschau in Bildschirmauflösung rechnen, volle Auflösung nur beim Export; laufende
  Rechnung bei neuer Reglerbewegung abbrechen.
- Zwischenergebnisse je Stufe zwischenspeichern: Ein Look-Regler rechnet die Physik
  nicht neu.
- Puffer wiederverwenden statt je Schritt neu anlegen (ArrayPool), SIMD
  (`System.Numerics.Vector`/`Vector256`) in den inneren Schleifen, Kacheln, die in den
  Prozessor-Cache passen, statt ganzer Bildzeilen über 80 MP.
- Speicher: 32 Bit × 4 Kanäle = 16 Byte je Pixel; ein 80-MP-Panorama braucht so 1,3 GB
  je Zwischenbild — Kacheln und wenige gleichzeitige Puffer halten das in Grenzen.
- Erst messen, dann optimieren: Ausgangswerte 0.4.45 siehe unten; später ggf. GPU
  (Compute-Shader) für die teuren Filter.
- Sprache: C# bleibt. .NET 10 mit SIMD und Parallelisierung liegt in solchen
  Pixelschleifen nahe an C++; der große Gewinn kommt aus Algorithmus, Speicherzugriff
  und GPU, nicht aus der Sprache. Einzelne heiße Kerne ließen sich bei Bedarf als
  native Bibliothek anbinden — nur nach Messung.

**Ausgangswerte 0.4.45** (Dunst 100, Panorama, ganze Dunstkette inkl. Entstufen):
16 Kerne, Release, gemessen mit `bench-zeit` am 08.10.2026.

| Bildgröße | 1 Durchlauf | 3 Durchläufe | 4 Durchläufe | Speicher-Spitze (3×) |
|---|---|---|---|---|
| 1200 × 539 (0,6 MP, Vorschau) | 0,8 s | 0,8 s | 1,2 s | 0,4 GB |
| 4000 × 1796 (7,2 MP) | 2,2 s | 5,5 s | 6,8 s | 1,9 GB |
| 8000 × 3591 (28,7 MP) | 7,7 s | 19,7 s | 24,3 s | 8,3 GB |

Belegt werden je Lauf ein Vielfaches davon (28,7 MP, 4×: 12,7 GB über die Zeit) —
jeder Schritt legt neue Puffer an. Ein 80-MP-Panorama passt so nicht mehr sicher in
den Speicher; das ist die erste Baustelle der Leistung.

---

## 7. Roadmap

**MVP (v0.1) — der nützliche Kern**
Öffnen (Drag&Drop + CLI-Argumente) · JPEG/PNG/WebP/BMP/TIFF I/O ·
Skalieren mit Lanczos/Bicubic/Bilinear, proportional (mit Auto-Nachrechnen der
zweiten Kante) + frei · Qualitätsregler mit Größenschätzung ·
Export mit Namensschema · EXIF behalten/entfernen · Deutsch + Englisch · Linux-AppImage

**v0.2 — Zuschneiden**
Auswahlrahmen mit 8 Griffen, Verschieben, Seitenverhältnisse, numerische Felder,
Zoom/Pan, Undo/Redo

**v0.3 — Qualität**
Unsharp Mask · klassische Rauschreduktion · DPI-Modi · ICC/sRGB ·
gamma-korrektes Skalieren · Vorher/Nachher-Vergleich

**v0.4 — Durchsatz & Integration**
Batch mit Parallelisierung · Presets · „Auf Dateigröße zielen" ·
Rechtsklick-Integration Linux + Windows · Windows-Build

**v0.5 — Formatbreite**
AVIF · JPEG XL · HEIC-Import · RAW-Import · ICO · animiertes GIF/WebP

**v1.0**
Weitere Sprachen · Barrierefreiheits-Durchgang · Flatpak · Doku · verlustfreie Pfade

**v2**
KI-Entrauschung und -Upscaling · SVG-Rasterung · Wasserzeichen/Rahmen

---

## 8. Offene Entscheidungen

1. ~~**Bildbibliothek**~~ — **entschieden: Magick.NET-Q16-AnyCPU 14.17.2** als einzige
   Bibliothek (alle Formate inkl. ICO/BMP/RAW, Async-I/O mit CancellationToken).
   Gemessen: +15 MB Linux / +10 MB Windows im Paket. Erst Q8, dann auf Q16 gewechselt:
   Q8 behielt beim gamma-korrekten Skalieren nur 6 von 41 dunklen Tonstufen (Banding),
   Q16 32+; Preis: doppelter Arbeitsspeicher, 30–50 % langsamer. NetVips nur
   nachrüsten, falls Batch-Läufe mit sehr großen Bildern zu langsam werden.
2. **Mobile/Browser-Heads jetzt löschen?** (Empfehlung: ja.)
3. **Flatpak zusätzlich zum AppImage?** (Empfehlung: ja, wegen Rechtsklick.)
4. **Übersetzungs-Workflow**: `.resx` im Repo vs. gettext/Weblate für Community.
5. **Lizenz & Projektname** — „ImageConverter" ist generisch; bei Veröffentlichung
   ein eigener Name (Referenzwerkzeug heißt ja auch nicht „Converter").
