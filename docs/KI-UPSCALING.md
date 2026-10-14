# Upscaling mit KI

Gruppe **„Upscaling“** im allgemeinen Tab, direkt unter „Größe“. Wirkt nur beim Vergrößern: Statt die fehlenden Pixel
nur mit dem Filter (Lanczos …) zu rechnen, erzeugt eine KI das Bild in vierfacher Größe; daraus verkleinert der Filter auf
die eingestellte Zielgröße. Gerechnet wird nur auf Knopfdruck oder nach Rückfrage beim Speichern — nie bei jeder
Reglerbewegung. Konzept und Messungen: `KONZEPT.md` §1 N.

## Verfahren

| Auswahl | Modell | Zeit (CPU, je Megapixel des Originals) | Lizenz | Download |
|---|---|---|---|---|
| Nur Filter | – | sofort | – | – |
| KI schnell | Real-ESRGAN general x4v3 (= Modell „Allround“ im KI-Tab) | ≈ 25 s | BSD-3-Clause | 4,5 MB |
| KI mittel | 4x-UltraSharpV2 (Kim2091, DAT) | ≈ 13 min | CC BY-NC-SA 4.0 (nur nicht-kommerziell) | 52 MB |
| KI wie Referenzwerkzeug | PiSA-SR (CVPR 2025) auf Stable Diffusion 2.1-base | ≈ 40 min | Code Apache 2.0, SD 2.1 CreativeML Open RAIL++-M | 2,6 GB |

Die Zeiten schätzt die App vorab (`Upscalers.Estimate`) und verfeinert sie nach jedem Lauf mit der gemessenen Zeit
dieses Rechners.

## Bedienung

- **Verfahren** wählen. Fehlt das Modell, steht dort „Herunterladen (…)“ — geladen wird nur auf Klick, mit allen Teilen
  (PiSA-SR: LoRA von Google Drive, UNet, VAE und Text-Encoder von SD 2.1 vom Spiegel). Verwalten wie alle Modelle in den
  Einstellungen › KI-Modelle (Gruppen „Upscaling“ und „Stable Diffusion 2.1“).
- **Upscalen** rechnet. Dauert es länger als 45 s, fragt die App: ganzes Bild oder nur der sichtbare Ausschnitt (vorher
  hineinzoomen) — zum Ausprobieren reicht der Ausschnitt.
- **Prüfansicht** zeigt das Ergebnis in voller Auflösung (so groß, wie die Zielgröße braucht, höchstens ×4 und rund
  64 Megapixel), damit man bis 100 % und weiter hineinzoomen kann. Fertige Kacheln erscheinen sofort, schon während der
  Rechnung. Mit **Vorher / Nachher**: links der Filter, rechts die KI, pixelgenau übereinander; die Zoom-Anzeige bezieht
  sich auf das Ergebnis. Die Prüfansicht zeigt das Upscaling allein, ohne Licht, Farbe und Schärfe; ausgeschaltet zeigt
  die normale Vorschau die ganze Kette — dann aus dem Bild ×4 verkleinert.
- **Treue** (Vorgabe 100 %): Rückprojektion E ← E + U(O − V(E)), drei Runden (V = Lanczos ÷4, U = bikubisch ×4). Danach
  ergibt das Ergebnis verkleinert wieder das Original — die KI darf Feines ergänzen, aber nichts verschieben. Hebt die
  Prüfung „verkleinert gegen Original“ von 22–35 dB auf 49–65 dB.
- **Details**: Mischung KI ↔ Lanczos-Vergrößerung. Beide Regler wirken ohne neue Rechnung (`UpscaleResult.Compose`).
- **Speichern**: Ist das Upscaling (für das ganze Bild) gerechnet, wird es verwendet. Sonst fragt die App: jetzt
  berechnen, ohne KI speichern (nur Filter) oder abbrechen; beim Export mehrerer Bilder einmal für alle, mit der
  geschätzten Gesamtzeit. Abbrechen im Export-Fenster stoppt auch ein laufendes Upscaling.
- Je Bild gemerkt (wie alle Einstellungen, mit Rückgängig): Verfahren, Treue, Details. Das Ergebnis selbst nur für das
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
3. `ImageConverter.Ui/ViewModels/MainViewModel.Upscale.cs` und `Models/UpscaleOption.cs` löschen; Verweise in
   `MainViewModel.cs` (`OnUpscaleRelevantChange`), `.History.cs` (drei Felder in `SettingsState`, `TrackedSettings`,
   `CaptureSettings`, `ApplySettings`), `.Preview.cs` (`PreviewJob.Upscaled`, `ResizeKey.Up`, `EmptyUpscaleTrash`),
   `.Export.cs` (`UpscaleBeforeExportAsync`), `.PerImage.cs` (`UpscaleForBatchAsync`), `ExportDialogViewModel.cs`
   (Rückfrage, `UpscaleEstimate`), `Ai/AiViewModel.cs` (`IsBusy`, zwei Gruppen).
4. `ImageConverter.Ui/Views/MainView.axaml`: Gruppe „UPSCALING“. `ChoiceWindow`, `CropView.RedrawToken` und
   `VisibleImageRect` dürfen bleiben.
5. `ImageConverter.Core/Imaging/ImageExporter.cs`: `ExportOptions.Upscaled` und `ResizeBasis` (ohne Upscaler harmlos).
6. `ImageConverter.Core/Localization/English.cs`: Abschnitt „Upscaling“ (`LocalizationTests` meldet Reste).
