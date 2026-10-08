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
- **Nicht vergrößern**-Schalter (Standard an) — verhindert versehentliches Upscaling.
- **Resampling-Filter** (Dropdown, Standard Lanczos3):
  Nearest, Bilinear, Bicubic, Catmull-Rom, Mitchell, **Lanczos3**, Spline/B-Spline,
  Box/Area (gut fürs Verkleinern).
- Skalierung in **Linear-Light** rechnen (gamma-korrekt) — sichtbar bessere Ergebnisse,
  als Option „Gamma-korrekt skalieren".

### C. Auflösung & DPI

- Pixelmaße (= eigentliche Auflösung) und **DPI/PPI** getrennt bedienbar.
- Zwei Modi, explizit im UI unterschieden:
  1. **Nur Metadaten ändern** — DPI-Tag umschreiben, Pixel bleiben (ändert Druckgröße).
  2. **Neu berechnen** — physische Zielgröße (mm/inch) × DPI ⇒ neue Pixelmaße.
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
- Zoom (Strg+Mausrad), Pan (Mittel-Taste/Leertaste), „Einpassen", 100 %.
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

---

## 2. Was ein einfacher Konverter noch braucht (von dir nicht genannt)

1. **Live-Vorschau mit Vorher/Nachher** (Trennlinie zum Schieben oder A/B-Taste).
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
| Tools | Helligkeit, Kontrast, Tiefen, Lichter (Local Laplacian Filter wie Referenzwerkzeug), Dunst (Dark Channel Prior), Dynamik, Sättigung, Färben (mit Farbton), geplant: „Gleiche Farbe“ (Referenzwerkzeug CS5), evtl. KI gegen JPEG-Artefakte — **je Funktion nur ein Regler** |
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
- **Verkettung:** Jeder Lauf wirkt auf das *aktuelle* Bild, also auf das Ergebnis des vorigen
  KI-Laufs, nicht wieder auf das Original.
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

## 6. Roadmap

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

## 7. Offene Entscheidungen

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
