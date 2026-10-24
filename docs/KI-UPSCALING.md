# Upscaling mit KI

Gruppe **„Größe“** im allgemeinen Tab (seit 0.5.77 beta eine Gruppe für Einheit, Maße, DPI, Verfahren, Treue/Details,
„Skalieren“ und Druckgröße). Die Liste **„Verfahren“** richtet sich nach der Richtung: beim Verkleinern nur Filter, die dafür
taugen (Lanczos, Bikubisch, Catmull-Rom, Mitchell, Spline, Bilinear, Box, Nearest); beim Vergrößern oben die Filter ohne
Bilinear und Box, unter einer Trennlinie die drei KI-Verfahren; ohne Größenänderung alles (so lässt sich das Verfahren
schon vor den Maßen wählen, Treue und Details ebenso). Mit einem KI-Scaler erzeugt die
KI das Bild in vierfacher Größe; daraus bringt der Filter (Lanczos) es auf die eingestellte Zielgröße — größer als ×4
vergrößert der Filter den Rest. Gerechnet wird immer in einem Schritt aus dem Bild selbst, KI nur auf Knopfdruck oder nach
Rückfrage beim Speichern. Konzept und Messungen: `KONZEPT.md` §1 N.

## Verfahren

| Auswahl | Modell | Zeit (CPU, je Megapixel des Originals) | Lizenz | Download |
|---|---|---|---|---|
| Nur Filter | – | sofort | – | – |
| KI Real-ESRGAN x4v3 · schnell | Real-ESRGAN general x4v3 (= Modell „Allround“ im KI-Tab) | ≈ 25 s | BSD-3-Clause | 4,5 MB |
| KI UltraSharpV2 · mittel | 4x-UltraSharpV2 (Kim2091, DAT) | ≈ 13 min | CC BY-NC-SA 4.0 (nur nicht-kommerziell) | 52 MB |
| KI PiSA-SR · langsam | PiSA-SR (CVPR 2025) auf Stable Diffusion 2.1-base | ≈ 40 min | Code Apache 2.0, SD 2.1 CreativeML Open RAIL++-M | 2,6 GB |

Die Zeiten schätzt die App vorab (`Upscalers.Estimate`) und verfeinert sie nach jedem Lauf mit der gemessenen Zeit
dieses Rechners.

## Bedienung

- **Verfahren** wählen. Fehlt das Modell, steht dort „Herunterladen (…)“ — geladen wird nur auf Klick, mit allen Teilen
  (PiSA-SR: LoRA von Google Drive, UNet, VAE und Text-Encoder von SD 2.1 vom Spiegel). Verwalten wie alle Modelle in den
  Einstellungen › KI-Modelle (Gruppen „Upscaling“ und „Stable Diffusion 2.1“).
- **Skalieren** (bei KI mit geschätzter Zeit, z. B. „Skalieren (≈ 6 min)“) rechnet jetzt und zeigt das Ergebnis in der
  Vorschau in voller Größe (so groß, wie die Zielgröße braucht, höchstens rund 64 Megapixel), damit man bis 100 % und weiter
  hineinzoomen kann. Mit einem Filter dauert das Sekunden, Vorher ist dann das Bild selbst. Mit KI: dauert es länger als
  45 s, fragt die App — ganzes Bild oder nur der sichtbare Ausschnitt (vorher hineinzoomen); fertige Kacheln erscheinen
  sofort, Vorher ist der Filter (Lanczos), pixelgenau übereinander. Ist das Ergebnis schon gerechnet, zeigt „Skalieren“
  es nur wieder an. Die Ansicht zeigt die Skalierung allein, ohne Licht, Farbe und Schärfe; jede andere Einstellung führt
  zurück zur normalen Vorschau mit der ganzen Kette — dann aus dem Bild ×4 verkleinert.
- **Fortschritt** unten in der Vorschau und in der Gruppe: Prozent, Restzeit und ab einer Minute die Uhrzeit, wann es
  fertig ist („noch ca. 12 min, fertig gegen 15:42“) — von Anfang an aus der Schätzung, ab 3 % aus dem Fortschritt. Die
  Rückfragen nennen die Uhrzeit ebenso.
- **Während des Upscalings** ist alles andere gesperrt (Einstellungen, Werkzeuge, Rückgängig, Bildwechsel, Speichern,
  Drucken) — das Ergebnis gehört zu genau diesem Stand. Frei bleiben Zoom, Verschieben, Vorher/Nachher und Abbrechen.
- **Reihenfolge:** Bildschritte (Zuschnitt, Drehen, Entzerren, Gewölbtes Blatt, KI-Tab, Entfernen) → **Skalieren/Upscaling**
  → Rauschen → Licht, Farbe, Dunst … (Tools) → Schärfen → Körnung → Farbprofil und Metadaten. Der Upscaler bekommt also das
  Bild ohne Look, alles danach läuft auf den neuen Pixeln. Beim Filter-Vergrößern wird sonst erst entrauscht, dann
  vergrößert; mit KI-Scaler danach (die Modelle entrauschen selbst).
- **Treue** und **Details** sind nur bei KI-Scalern aktiv (sonst ausgegraut).
- **Treue** (Vorgabe 100 %): Rückprojektion E ← E + U(O − V(E)), drei Runden (V = Lanczos ÷4, U = bikubisch ×4). Danach
  ergibt das Ergebnis verkleinert wieder das Original — die KI darf Feines ergänzen, aber nichts verschieben. Hebt die
  Prüfung „verkleinert gegen Original“ von 22–35 dB auf 49–65 dB.
- **Details**: Mischung KI ↔ Lanczos-Vergrößerung. Beide Regler wirken ohne neue Rechnung (`UpscaleResult.Compose`).
- **Speichern**: Ist das Upscaling (für das ganze Bild) gerechnet, wird es verwendet. Sonst fragt die App: jetzt
  berechnen, ohne KI speichern (nur Filter) oder abbrechen; beim Export mehrerer Bilder einmal für alle, mit der
  geschätzten Gesamtzeit. Abbrechen im Export-Fenster stoppt auch ein laufendes Upscaling.
- Je Bild gemerkt (wie alle Einstellungen, mit Rückgängig): Filter und KI-Scaler getrennt (die KI-Wahl bleibt beim
  Verkleinern erhalten und gilt wieder beim Vergrößern), Treue, Details. Das Ergebnis selbst nur für das
  zuletzt gerechnete Bild (Speicher); es gilt, solange Pixelbild, Ausrichten und Verfahren gleich sind.

## So arbeitet die App

`ImageConverter.Core/Ai/Upscale/`:

| Datei | Aufgabe |
|---|---|
| `Upscalers.cs` | Verfahren, Modell dazu, Zeitschätzung. |
| `UpscaleResult.cs` | Ergebnis ×4 roh (16 Bit) plus Original; `Compose`/`ComposeRegion` rechnen Treue und Details in Kacheln von 128 px mit 16 px Rand ein. Alpha kommt immer per Lanczos aus dem Original. |
| `Resample4.cs` | Bikubisch/Lanczos ×4 und Lanczos ÷4 auf Ausschnitten, Rückprojektion. |
| `TileAxis.cs` | Kacheln einer Achse mit Gewichten, die überall genau 1 ergeben — Kacheln werden direkt ins 16-Bit-Ergebnis addiert, ohne Puffer in voller Größe. |
| `UpscaleEngine.cs` | ×4-Kachelmodelle (x4v3, UltraSharpV2): Kachel 128 mit Rand, fertige Flächen werden gemeldet. |
| `PiSaSrNet.cs` | Baut aus den Gewichten drei ONNX-Graphen (opset 17): VAE-Encoder, UNet (über 2 GB → Gewichte in `pisasr-unet.onnx.data`), VAE-Decoder. LoRA (pix + sem, je α/r = 2) wird eingerechnet, die Zeit-Einbettung (t = 1) als Bias in die Faltungen, Schlüssel/Werte der Kreuz-Aufmerksamkeit für die leere Beschreibung als Konstanten. |
| `ClipText.cs` | Text-Encoder von SD 2.1 (23 Schichten) in C#, nur für die leere Beschreibung (1 × 77 × 1024), einmal beim Umwandeln. |
| `PiSaSrModel.cs` | Quellen finden, ONNX-Dateien beim ersten Gebrauch bauen (knapp 2 min samt Beschreibung, 3,6 GB, neben `pisa_sr.pkl`). |
| `PiSaSrRun.cs` | Ablauf wie `PiSASR_eval` (Modus „Standard“): bikubisch ×4 → Encoder (Kacheln 96 Latent, Rand 16) → UNet (64/16) → Latent − Vorhersage → Decoder (64/12) → AdaIN-Farbangleich. Fertige Rechtecke werden gemeldet. |

Dazu `Ai/Onnx/` (gemeinsam mit SyntheticDoc): `SafeTensors.cs` (lesen F32/F16/BF16, schreiben F32),
`TorchCheckpoint.cs`, `OnnxGraph.cs`. In der Oberfläche: `MainViewModel.Upscale.cs`, `Models/UpscaleOption.cs`,
`Views/ChoiceWindow` (Rückfrage), Gruppe in `MainView.axaml`; Export über `ExportOptions.Upscaled`
(`ImageExporter.ResizeBasis`: beim Vergrößern ist das Bild ×4 die Grundlage), Vorschau über `PreviewJob.Upscaled`.

**Geprüft:** Die drei ONNX-Teile weichen vom PyTorch-Original höchstens um 1,5 · 10⁻⁵ ab (relativ 4 · 10⁻⁶;
`tools/PiSaSrUmwandeln.cs --pruefen` mit Referenzwerten aus dem Messplatz). Die leere Beschreibung aus C# weicht von der
mit PyTorch gerechneten um höchstens 0,0075 ab (bei Werten bis 13; Quelle fp16 statt fp32). Ganzer Lauf am
Panorama-Ausschnitt 500 × 300 → 2000 × 1200: 355 s (Python 445 s), PSNR mit Treue 23,4 dB (Python 23,2 dB), keine
sichtbaren Kachelnähte.

**Halbe Genauigkeit:** Die Gewichte von SD 2.1 kommen als fp16 (halb so groß); gerechnet wird in fp32. Gemessen gegen
die fp32-Gewichte: im Mittel 0,03 von 255 Stufen Unterschied, höchstens 3 — nicht zu sehen.

## Mehr als 4×: zwei Durchgänge

Die Modelle vergrößern je Durchgang 4×. Ist das Ziel größer (z. B. Ausschnitt 300 px → 4000 px, 13,3×), fragt die App
vor dem Rechnen und beim Speichern, jeweils mit Dauer und Uhrzeit:

- **Zwei KI-Durchgänge** (vorgewählt): Ergebnis ×4 mit Treue/Details zusammensetzen, auf ein Viertel des Ziels verkleinern
  (höchstens ×4 des Bildes), noch einmal ×4 — landet genau beim Ziel. Über 16× macht der Filter den Rest.
- **Ein KI-Durchgang:** ×4, den Rest vergrößert der Filter (wie bis 0.5.86).

Die Wahl gehört zum Stand des Bildes (`UpscaleTwice`, Rückgängig, Speichern mehrerer Bilder); die Zeile „Größe“ der
Bearbeitungen zeigt „· 2 Durchgänge“. Planung in `Upscalers.Plan`, Rechnung in `ComputePlanned`; der Fortschritt läuft über
beide („Durchgang 1 von 2“), Kacheln erscheinen im zweiten.

**Gemessen** (Panorama-Ausschnitt 4000 × 2400 auf 300 × 180 verkleinert, wieder auf 4000 × 2400, Grafikkarte; Messplatz
`werkzeug/dreizehn.sh`, `dreizehn4.sh`, `dreizehn_auswertung.py`):

| Weg | x4v3 | UltraSharpV2 | PiSA-SR | Bild |
|---|---|---|---|---|
| 1 KI ×4, dann Lanczos | 2 s | 43 s | 36 s | weich |
| 2 erst Lanczos auf 1000, dann KI ×4 | 10 s | 4,4 min | 6,2 min | am schlechtesten: verwaschen, UltraSharpV2 mit Milchglas-Muster |
| 3 KI ×4 zweimal (1200 → 4800), dann ↓ | 13 s | 7,8 min | 9,4 min | am schärfsten |
| 4 KI ×4, ↓ auf 1000, KI ×4 (**eingebaut**) | – | ≈ 5 min | ≈ 7 min | wie Weg 3 |

Feinstruktur (Streuung des Hochpasses, echtes Bild 0,0104): Lanczos 0,0018, UltraSharpV2 Weg 1 0,0020 / Weg 3 0,0068 /
Weg 4 0,0066, PiSA-SR Weg 1 0,0026 / Weg 3 0,0123 / Weg 4 0,0129. Die PSNR zum echten Bild liegt bei allen um 26–29 dB
und bevorzugt weiche Bilder — bei ×13 ist ohnehin das meiste erfunden: PiSA-SR zeichnet glaubhafte Fiederblätter, aber
nicht an denselben Stellen wie im Foto. Die Zeiten von Weg 4 sind aus der Eingangsgröße geschätzt (der Lauf lief neben
anderen Rechnungen).

## Werkzeuge

- `tools/PiSaSrUmwandeln.cs <quellordner> <zielordner> [--pruefen <ref>] [--nur-text] [--ohne-text]`: umwandeln wie die
  App, prüfen gegen PyTorch.
- `tools/UpscaleMessen.cs <pisasr|onnx> <ordner|-> <bild> <ausgabe> [<wahres-bild>]`: Zeit und PSNR (roh und mit Treue).
- Original-Code: `extern/PiSA-SR/` (unverändert, siehe `IMAGECONVERTER.md` dort).
- Sicherung der Gewichte: `Desktop\ImageC Backup\ImageConverter-KI-Modelle-<Datum>.zip` mit `HERKUNFT.md`.

## Modell tauschen

Kommt ein deutlich besseres ×4-Kachelmodell (ONNX, Eingang RGB 0…1 NCHW): neuen Eintrag in `AiModelCatalog`
(`AiModelKind.Upscale`, `Scale: 4`, Kachelgröße), in `Upscalers.ModelOf` und `UpscaleMethod` zuordnen, Zeit je
Megapixel in `Upscalers` eintragen, Text in `UpscaleOption`. Ein anderes Diffusionsmodell braucht einen eigenen Nachbau
wie `PiSaSrNet`.

## Upscaling wieder entfernen

1. Ordner löschen: `ImageConverter.Core/Ai/Upscale/`, `extern/PiSA-SR/`, `ImageConverter.Tests/UpscaleTests.cs`,
   `tools/PiSaSrUmwandeln.cs`, `tools/UpscaleMessen.cs`, auf der Platte `KI-Modelle/{ultrasharpv2,pisasr,sd21-*}`.
   `Ai/Onnx/SafeTensors.cs` darf bleiben (`OnnxGraph`/`TorchCheckpoint` braucht SyntheticDoc).
2. `ImageConverter.Core/Ai/AiModels.cs`: `UltraSharpV2`, `PiSaSr`, `Sd21Unet`, `Sd21Vae`, `Sd21Text` und ihre Einträge in
   `All`, die Kinds `Upscale` und `Part`.
3. `ImageConverter.Ui/ViewModels/MainViewModel.Upscale.cs` und `Models/UpscaleOption.cs` löschen, in
   `Models/ScaleChoice.cs` die KI-Einträge und die Überschrift weglassen (die Liste „Verfahren“ bleibt für die Filter); Verweise in
   `MainViewModel.cs` (`OnUpscaleRelevantChange`), `.History.cs` (drei Felder in `SettingsState`, `TrackedSettings`,
   `CaptureSettings`, `ApplySettings`), `.Preview.cs` (`PreviewJob.Upscaled`, `ResizeKey.Up`, `EmptyUpscaleTrash`),
   `.Export.cs` (`UpscaleBeforeExportAsync`), `.PerImage.cs` (`UpscaleForBatchAsync`), `ExportDialogViewModel.cs`
   (Rückfrage, `UpscaleEstimate`), `Ai/AiViewModel.cs` (`IsBusy`, zwei Gruppen).
4. `ImageConverter.Ui/Views/MainView.axaml`: in der Gruppe „Größe“ Modell-Download, Treue, Details (der Knopf
   „Skalieren“ mit Filter darf bleiben). `ChoiceWindow`, `CropView.RedrawToken` und `VisibleImageRect` dürfen bleiben.
5. `ImageConverter.Core/Imaging/ImageExporter.cs`: `ExportOptions.Upscaled` und `ResizeBasis` (ohne Upscaler harmlos).
6. `ImageConverter.Core/Localization/English.cs`: Abschnitt „Upscaling“ (`LocalizationTests` meldet Reste).
