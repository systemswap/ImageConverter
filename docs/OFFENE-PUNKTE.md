# Offene Punkte – ImageConverter

Was als Nächstes ansteht, was bewusst zurückgestellt ist und was nicht wieder
vorgeschlagen werden soll. Erledigtes wird gestrichen (die Geschichte steht in Git und
im Fehlergedächtnis). Weitere offene Entscheidungen: `KONZEPT.md`, Abschnitt 8.

## Zu prüfen: vermutete Fehler im Code

Gefunden am 24.10.2026 beim Überarbeiten der Kommentare (Version 0.6.25-beta). **Nichts
davon ist geändert** – die Überarbeitung durfte nur Kommentare ändern. Jeden Punkt erst
nachstellen (Test, der mit dem heutigen Code fehlschlägt), dann beheben und ins
Fehlergedächtnis eintragen.

### Sicherheit gegen präparierte Dateien (zuerst)

- **`SafeTensors.Get`:** Byte-Bereiche aus dem Dateikopf werden nicht gegen Form und Typ
  geprüft; negative oder zu große Offsets und ein überlaufendes Formprodukt können zu
  falschen Ausnahmen oder riesigen Speicheranforderungen führen.
- **`SafeTensors.Write`:** Die 8-Byte-Ausrichtung zählt die Kopflänge in Zeichen statt in
  UTF-8-Bytes (falsch bei Namen mit Nicht-ASCII-Zeichen); Namen werden nicht für JSON
  maskiert.
- **`TorchCheckpoint.RebuildTensor`:** Das Formprodukt kann als `int` ungeprüft überlaufen;
  kaputte Pickle-Daten lösen `IndexOutOfRange`, `InvalidCast` oder `KeyNotFound` statt
  `InvalidDataException` aus; `BINUNICODE8` wird auf `int` gekürzt.
- **`Inpainter.Rasterize`:** `new byte[width * height]` rechnet in `int` (bei den übrigen
  Grenzen wohl unkritisch).

### Nebenläufigkeit

- **`OnnxSessions`:** `Get` schließt andere große Modelle, die Setter `UseGpu` und
  `Measured` rufen `Dispose` auf – läuft gerade ein Auftrag mit einer dieser Sitzungen, wird
  sie ihm geschlossen. Beim Ändern der CPU-Obergrenze vermeidet der Konstruktor genau das.
  Die beiden Setter laufen ohne Sperre.
- **`FaceRun`:** Der Vorschau-Zwischenspeicher `_small` ist nicht gesperrt (anders als
  `_grain`) – Wettlauf, wenn Vorschau und Map gleichzeitig laufen.
- **`ImageExporter`, privates `Plan()`:** liest `source.Width/Height` ohne `lock (source)`,
  anders als der übrige Code.

### Fehlverhalten

- **`GeometryRecipe.IsNone`:** `this == None with { Turns = 0 } && Turns % 4 == 0`
  vergleicht das unveränderte Rezept mit `None`; bei `Turns = 4` (oder −4) ist das Ergebnis
  `false`, obwohl nichts zu tun ist. Gemeint war vermutlich
  `this with { Turns = 0 } == None && Turns % 4 == 0`.
- **`SystemPrinters.PrintWithWindows`:** Bei leerer `layout.Pages` greifen
  `QueryPageSettings` und `PrintPage` auf `Pages[0]` zu (Absturz), falls der Aufrufer das
  nicht abfängt.
- **`CanCropToSelection` (MainViewModel.Editing):** Wegen der Operator-Rangfolge schützt
  `!IsUpscaling` nur den Fall „Auswahl“; Perspektive und gewölbte Seite bleiben während des
  Hochskalierens möglich.
- **`ResetTools` (MainViewModel.Tools):** ruft `ResetDehazeDetails()` auf, das mit
  `CommitPendingSettings()` beginnt – ein Zurücksetzen kann zwei Rückgängig-Schritte anlegen
  statt einem.
- **`ExportFileAsync` (MainViewModel.PerImage):** Der Abbruch wirkt nur auf das
  Hochskalieren, nicht auf das Kodieren.
- **`ComputeLimits.LowerPriority`:** Unter Linux wird der Rückgabewert von `setpriority`
  ignoriert; `LowPriority` steht auch dann auf `true`, wenn kein Strang umgestellt wurde.
- **`AppLog.WritableFolder`:** Liefert `LocalApplicationData` einen leeren Text, entsteht der
  Ordner relativ zum Arbeitsverzeichnis (`ImageMagickPolicy.UserDataRoot` weicht dann auf den
  Temp-Ordner aus).
- **`SavingsText` (MainViewModel.Export):** beginnt mit dem unübersetzten Text „Größe nach
  dem Export“ ohne `Tr.T`.

### Aufräumen und Speicher

- **`ImageStep.Replay`** ist immer `null` – der Zweig zum Wiederholen ist toter Code; ein
  KI-Schritt lässt sich deshalb nur abschalten, wenn ihm nur KI-Schritte folgen.
- **`Render` (MainViewModel.Preview):** Mit Schärfen und ohne Körnung wird der Puffer aus
  `UnsharpMask` eventuell nicht an `PixelPool` zurückgegeben (Herkunft des Puffers prüfen).
- **`LocalLaplacian.Pyramid.Collapse`:** Zwischenpuffer aus `Up` kommen aus `PixelPool`, werden
  aber nicht zurückgegeben (kein Fehler, kostet Speicher).
- **`Grain.SizeFor`:** fest `0.25` statt `GrainSettings.PixelSigma` (gleicher Wert, doppelte
  Konstante).
- **`AiModelStore.ExtractFlat`:** legt nur `.onnx`-Dateien zuletzt an – setzt voraus, dass
  `MainFile` eine `.onnx` ist (für das einzige Zip-Modell derzeit wahr).
- **`TileAxis`:** Mit `align > 1` kann die Mindestüberlappung um bis zu `align − 1`
  unterschritten werden (`align` benutzt bisher nur ein Test).
- **Tests:** `DehazeTests` (etwa Zeile 173) verwendet die Szenenfarbe (0.05, 0.20, 0.16) als
  R, G, B – blaugrün statt des vermutlich gemeinten olivgrünen Falls.
- **Werkzeuge:** Die Aufrufhilfe von `tools/UpscaleMessen` nennt `--gpu` und `--fp16` nicht,
  die von `tools/Abnahme` den Befehl `einzeln` nicht (nur die Kopfkommentare).
- **Retusche:** Der Kommentar zu `RetouchViewModel.OnIsTabActiveChanged` sagt, gemalt werde
  auf dem ungedrehten Bild; `ToPixels` bildet Striche aber vom angezeigten (gedrehten,
  beschnittenen) Bild ab – Absicht oder Fehler klären.

## Als Nächstes

- **Vorschau gegen Export bei verkleinerter Vorschau:** Mit Dunst 45 weichen auch opake Bilder sichtbar ab (6-MP-Bild, Blöcke 16 × 16: Mittel
  1,6, einzelne Stellen bis 44 Stufen; Bild, das in die Vorschau passt: 0,3). Nicht durch den Zuschnitt entstanden, Ursache nicht
  untersucht. Verdacht: Die Schätzkopie des Dunstes hat je nach Bildgröße eine andere Kante (`Dehaze.Apply`).

- **PNG mit niedrigerer zlib-Stufe schreiben (Entscheidung offen):** `Encode` schreibt mit Quality 95 (Stufe 9). Gemessen an zwei Bildern
  (5,5 MP), Quality 95 gegen 55: 16 Bit transparent 8,5 → 1,5 s, 8 Bit opak 7,3–8,4 → 1,4 s, 16 Bit opak 2,3–2,8 → 2,0 s; Größe 12,7 → 12,7,
  7,2 → 7,3, 25,3 → 25,3 MB. Verlustfrei, beträfe aber alle PNG-Exporte; an mehr Bildern prüfen.

- **Speicher-Pool der ONNX Runtime bei anderen Modellen messen:** Für die Hintergrund-Modelle ist er aus
  (Fehlergedächtnis #88); ob Verbessern (GFPGAN, CodeFormer …) und Entfernen (LaMa, MI-GAN) ebenfalls Speicher
  festhalten, ist nicht gemessen.

- **Fenstertests im Testprojekt:** Abläufe mit geladenen Bildern (Blättern während einer KI-Berechnung,
  Rückgängig über mehrere Bilder, Fehlergedächtnis #85/#86) lassen sich bisher nur im unsichtbaren Fenster
  außerhalb des Projekts prüfen. Mit `Avalonia.Headless` im Testprojekt würden sie zu festen Tests – neues
  Paket, nur nach Zustimmung.

- Code-Wächter (Analyzer, `.editorconfig`): erst an einer Kopie messen, dem Nutzer vorlegen
  (gemeinsame Regeln, `Code-Qualitaet.md` §3). Einschalten ändert Code – nur mit Zustimmung.
