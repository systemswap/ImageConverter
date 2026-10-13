# KI begradigen (SyntheticDoc)

Zweites Werkzeug neben „Gewölbtes Blatt“ (Linien + Blattmodell): ein Klick, keine Linien. Ein neuronales Netz
sagt für ein fotografiertes Blatt voraus, wo jeder Punkt der flachen Seite im Foto liegt, und das Foto wird
danach umgerechnet. Stark bei zerknitterten und gefalteten Blättern, Bons in der Hand und Büchern; bei glatten
Arbeitsblättern ist „Gewölbtes Blatt“ 10- bis 30-mal genauer (Messungen unten).

Alles ohne Python: die App lädt den Original-Checkpoint der Autoren und baut die ONNX-Datei selbst.

## Herkunft und Lizenzen

| Teil | Quelle | Lizenz |
|---|---|---|
| Netz, Training, Datenerzeugung | SyntheticDoc (Woortmann, Magne, Sorkine-Hornung, ECCV 2026, ETH Zürich), Kopie in `extern/SyntheticDoc/` | MIT |
| Gewichte `highres_121x177.ckpt` (97 MB) | `https://igl.ethz.ch/projects/SyntheticDoc/highres_121x177.ckpt`, SHA-256 `a9b69148…a91d` | ohne eigene Angabe, wie das Repository MIT |
| Datensatz (1 Mio. Beispiele) und Assets | ETH Research Collection, Links in `extern/SyntheticDoc/README.md` | siehe dort |
| Papiersimulation ARCSim 0.2.1 (nur zum Erzeugen eigener Daten) | Berkeley, nicht mitgeliefert | **nur nicht-kommerziell** — für den privaten Gebrauch dieser App in Ordnung |

Die zweite Fassung der Autoren (`original_31x45.ckpt`, Gitter 31 × 45, Eingabe 720 × 512) ist in unseren
Messungen durchweg schlechter und wird nicht genutzt; der Umwandler kann sie aber auch.

## So arbeitet die App

Alles, was nur SyntheticDoc betrifft, liegt in `ImageConverter.Core/Ai/SyntheticDoc/`:

| Datei | Aufgabe |
|---|---|
| `TorchCheckpoint.cs` | Liest PyTorch-Checkpoints (Zip mit Pickle und rohen Speichern) ohne Python. Ein schmaler Unpickler: führt nichts aus, unbekannte Klassen werden zu Platzhaltern. |
| `OnnxGraph.cs` | Schreibt ONNX-Modelle (Protobuf, nur die nötigen Felder, ohne Abhängigkeit). |
| `SyntheticDocNet.cs` | Baut das Netz aus `extern/SyntheticDoc/training/model.py` nach (UVDocNet + Schatten-Decoder), BatchNorm in die Faltungen gerechnet, Eingabegröße offen. Ausgänge `bm_map` (1 × 2 × 121 × 177, −1 … 1, align_corners = True) und auf Wunsch `shadow_map`. |
| `SyntheticDocModel.cs` | Checkpoint → `syntheticdoc.onnx` daneben, beim ersten Gebrauch (gut 1 s) und neu, sobald der Checkpoint jünger ist. |
| `SyntheticDocDewarp.cs` | Zwei Läufe: grob auf 720 × 512 (nur um das Blatt zu finden), dann genau auf 1440 × 1024 auf dem Blattausschnitt + 4 % (so wurde trainiert). Daraus das Gitter, Seitenverhältnis über unser Blattmodell mit den Gitterlinien (DIN-Einrasten). |

Geprüft: Die C#-Umwandlung weicht von einem Export mit PyTorch höchstens um 5 · 10⁻⁷ ab. Rechenzeit auf
dem Entwicklungsrechner (Notebook-Prozessor, CPU): grober Lauf 0,3 s, genauer 0,6 s, mit Bildaufbereitung
insgesamt 1,2–1,4 s je Bild. Den Schatten-Zweig rechnet ONNX Runtime immer mit, auch wenn man nur das
Gitter abfragt (verdoppelt die Zeit) — deshalb baut die App die Datei ohne ihn.

Im Rezept ist die KI-Begradigung dieselbe Stufe wie „Gewölbtes Blatt“ (`DewarpSpec.Grid` statt Linien,
`GeometryRecipe.AiDewarpedInView` dreht das Gitter der Ansicht ins Pixelbild zurück).

### Was SyntheticDoc kann und was nicht

Trainiert wurde nur auf **A4 hochkant**, ein Blatt, Inhalt **arXiv-Seiten** (fast nur Text), Kamera
5–28° aus der Senkrechten, immer auf das Blatt zugeschnitten (+ 1–3 % Rand). Deshalb:

- gut: Knitter, Falten, eingerollte Ränder, Bons, Bücher, Notenblätter;
- schwächer: Doppelseiten, Fotobücher, Arbeitsblätter mit Kästen und Raster (nie gesehen), andere Formate.

Am Rechenweg lässt sich kaum noch etwas holen (gemessen: Rand 2 % statt 4 %, dritter Lauf, gespiegelt
gemittelt — alles ±1–3 px, im Rauschen). Die Kombination „erst KI, dann unsere Linien im Ergebnis“ bringt
nichts Verlässliches (nur Gitter: ±0,5 px; mit Blattmodell teils besser, teils kaputt). Der Hebel ist das
**Nachtrainieren** mit Seiten, wie wir sie begradigen wollen.

## Nachtrainieren

### Was man an Daten braucht

Nur **flache Originale**: Scans, PDF-Seiten, eigene Arbeitsblätter, Doppelseiten — als PNG. Zerknitterte
Fotos allein (oder Paare aus Original und Foto) genügen nicht: das Netz lernt aus der *Rückabbildung* (wo
liegt jeder Punkt der Seite im Foto), und die kennt bei echten Fotos niemand genau. Deshalb wird das
Zerknittern simuliert, und die Rückabbildung fällt exakt mit ab. Echte Fotos wie die 130 von DocUNet taugen
nur zum Prüfen.

### Ablauf (auf einem Linux-Rechner mit NVIDIA-GPU)

Alle Werkzeuge liegen in `extern/SyntheticDoc/`; die englischen READMEs dort beschreiben jeden Schritt genau.

1. **Papier simulieren** (`generation/simulation/`, ARCSim kompilieren — Anleitung im README):
   `python generate_configs.py --config-type fall_on_many --num 2000` und `python simulate.py --scenario
   fall_on_many --ids …` — sechs Szenarien (ziehen, auf Kugel/Zylinder/Dach/viele Kugeln fallen, falten).
   Oder die fertigen Meshes der Autoren aus den Assets nehmen.
2. **Rendern** (`generation/rendering/`, Blender 4.5 LTS): eigene Seiten nach `documents/`, Meshes nach
   `meshes/`, Hintergründe (PBR-Materialien, aus den Assets) nach `backgrounds/`, dann
   `blender --background --python dataset_generator.py -- --start 0 --end 20000 --output-dir ./renders`.
3. **Rückabbildungen** (`generation/backward_mapping/`): `python uv_to_backward_map.py --input ../rendering/renders`.
4. **Packen** (unsere Ergänzung): `python generation/pack_shards.py --input generation/rendering/renders
   --output training/data/eigen --val 0.05`.
5. **Trainieren** (`training/`, PyTorch ≥ 2.10 mit CUDA, `pip install -r requirements.txt`):
   Konfiguration aus `configs/uvdoc_121x177.yaml` kopieren, `train_shard_pattern` auf die eigenen Pakete
   (`data/eigen/train/train_{000..019}.zip`), dazu am besten einige Pakete des Original-Datensatzes mischen
   (sonst verlernt das Netz, was es kann), `num_epochs` klein (5–10), `learning_rate` 3e-5. Dann
   `python train.py --config configs/eigen.yaml --init-weights model/highres_121x177.ckpt`
   (`--init-weights` ist unsere Ergänzung: nur die Gewichte als Start). Fortsetzen nach Abbruch: `--resume auto`.
6. **In die App**: den neuen Checkpoint (`checkpoints/<name>/last.ckpt`) als
   `KI-Modelle/syntheticdoc/highres_121x177.ckpt` ablegen (den alten vorher wegsichern). Die App baut die
   ONNX-Datei beim nächsten Klick neu, weil der Checkpoint jünger ist. Zum Prüfen ohne App:
   `tools/SyntheticDocUmwandeln.cs` (Aufruf im Kopf der Datei; wandelt um und macht einen Probelauf).

### Wie lange dauert das?

Gemessen auf dem Entwicklungsrechner (Notebook-Prozessor, 8 Kerne, ohne NVIDIA-GPU; Zufallsdaten, die Rechenzeit
hängt nicht vom Inhalt ab), ein Trainingsschritt mit Vorwärts, Rückwärts und Optimierer:

| Auflösung | Stapel | je Bild |
|---|---|---|
| 1440 × 1024 (Gitter 121 × 177) | 2 | 5,6 s |
| 1440 × 1024 | 1 | 7,1 s |
| 720 × 512 (Gitter 31 × 45) | 2 | 1,9 s |

Hochgerechnet (Bild × Epochen × Zeit je Bild):

| Vorhaben | Lernschritte | Entwicklungsrechner (CPU) | NVIDIA-GPU (Schätzung) |
|---|---|---|---|
| Probe: 2 000 eigene Seiten × 3 Epochen | 6 000 | ≈ 9 Stunden | RTX 4090: ≈ 5–10 min · RTX 3060: ≈ 20–30 min |
| Nachtrainieren: 20 000 Beispiele × 5 Epochen | 100 000 | ≈ 6,5 Tage | RTX 4090: ≈ 1,5–2,5 h · RTX 3060: ≈ 5–8 h |
| Wie die Autoren (35 Epochen über 1 Mio.) | ≈ 9 Mio. | ≈ 600 Tage | mehrere GPU-Tage |

Die GPU-Werte sind geschätzt (Faltungsnetze rechnen auf einer GPU typischerweise 50–100-mal schneller als
auf dieser CPU), nicht gemessen. Der größere Aufwand ist oft das **Rendern** der Daten: Blender Cycles mit
128 Strahlen je Pixel braucht auf einer GPU einige Sekunden je Bild (20 000 Bilder: ein bis drei Tage auf
einer GPU), auf dieser CPU eine bis zwei Minuten. Die Papiersimulation (ARCSim, CPU) ist kleiner: ein Mesh
in Minuten, und jedes Mesh wird für vier Beispiele genutzt. Speicher: in voller Auflösung etwa 1,4 GB je
Bild im Stapel — die Vorgabe `batch_size: 16` braucht eine GPU mit 24 GB, bei weniger den Stapel
verkleinern.

## SyntheticDoc wieder entfernen

1. Ordner löschen: `ImageConverter.Core/Ai/SyntheticDoc/`, `extern/SyntheticDoc/`,
   `ImageConverter.Tests/SyntheticDocTests.cs`, auf der Platte `KI-Modelle/syntheticdoc/`.
2. `ImageConverter.Core/Ai/AiModels.cs`: Eintrag `SyntheticDoc` und seine Erwähnung in `All`
   (dazu nach Wunsch `AiModelKind.Dewarp`).
3. `ImageConverter.Ui/ViewModels/MainViewModel.Dewarp.cs`: den Abschnitt „KI begradigen (SyntheticDoc)“
   (`AiStraightenActive`, `IsStraighteningWithAi`, `StraightenWithAi`).
4. `ImageConverter.Ui/ViewModels/MainViewModel.Edits.cs`: in der Bearbeitungsliste die Zweige für
   `spec.Grid` („KI begradigt“) und `OnPropertyChanged(nameof(AiStraightenActive))`.
5. `ImageConverter.Ui/Views/MainView.axaml`: den Rahmen um die zwei Symbole und das Symbol „KI begradigen“.
6. `ImageConverter.Core/Localization/English.cs`: die Texte zu SyntheticDoc und „KI begradig…“
   (`LocalizationTests` meldet, was übrig ist).

Allgemein und ohne SyntheticDoc harmlos (darf bleiben): `DewarpGrid`, `DewarpSpec.Grid`, `DewarpMap.Ai`,
`GeometryRecipe.AiDewarpedInView` samt `AiDewarpTests`, `AiModelItem.ShowDownload` ohne Adresse.

## Messungen (12./13.10.2026)

Echter Fehler gegen die bekannte Form der Generator-Testbilder (Mittel px):

| Bild | Linien + Blattmodell | SyntheticDoc direkt |
|---|---|---|
| Schulbuch-Blatt gewölbt | 0,5 | 13,2 |
| Arbeitsblatt-Blatt gewölbt | 1,9 | 18,0 |
| Schulbuch / Arbeitsblatt wellig | 5,9 / 10,0 | 19,7 / 25,8 |
| Schulbuch / Arbeitsblatt Doppelseite | 1,4 / 1,9 | 31,5 / 32,1 |

Echte Fotos (DocUNet 63_2 gefaltet, 58_2 und 54_2 zerknittert, 60_1 Faltblatt, 2_1 Bon in der Hand):
SyntheticDoc sichtbar besser als das Linienverfahren; Flattener (ONNX, Apache-2.0) knapp dahinter, UVDoc
etwas schwächer. Vergleichsbilder in `TestBilder/Merge/schwer-*.jpg` (nicht im Git).

Andere Modelle (Stand 13.10.2026): UVDoc und Flattener getestet (schwächer), DvD (AGPL, ~800 MB, nur
PyTorch) und D2Dewarp (CC BY-NC-ND, DocUNet MS-SSIM 0,50 — schlechter als SyntheticDoc mit 0,558) nicht,
AADD noch ohne Gewichte.
