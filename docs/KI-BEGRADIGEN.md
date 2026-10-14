| `Onnx/OnnxGraph.cs` | Schreibt ONNX-Modelle (Protobuf, nur die nötigen Felder, ohne Abhängigkeit; große Gewichte auf Wunsch in eine Datei daneben, für Modelle über 2 GB). |Alles, was nur SyntheticDoc betrifft, liegt in `ImageConverter.Core/Ai/SyntheticDoc/`; die zwei Bausteine zum Lesen von
Checkpoints und Schreiben von ONNX teilt es seit 0.5.76 mit dem Upscaling (PiSA-SR) in `ImageConverter.Core/Ai/Onnx/`:

| Datei | Aufgabe |
|---|---|
| `Onnx/TorchCheckpoint.cs` |# KI begradigen (SyntheticDoc)

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
| Papiersimulation ARCSim 0.2.1 (nur zum Erzeugen eigener Daten) | Berkeley, `arcsim-0.2.1.tar.gz` (14,8 MB, SHA-256 `053239c4…a26e`); eine Kopie liegt in `KI-Modelle/syntheticdoc/` und damit in jeder Sicherung, nicht im Git | **nur nicht-kommerziell**, Lizenztext im Archiv — für den privaten Gebrauch dieser App in Ordnung |

Die zweite Fassung der Autoren (`original_31x45.ckpt`, Gitter 31 × 45, Eingabe 720 × 512) ist in unseren
Messungen durchweg schlechter und wird nicht genutzt; der Umwandler kann sie aber auch.

## So arbeitet die App

Alles, was nur SyntheticDoc betrifft, liegt in `ImageConverter.Core/Ai/SyntheticDoc/`:

| Datei | Aufgabe |
|---|---|
| `TorchCheckpoint.cs` | Liest PyTorch-Checkpoints (Zip mit Pickle und rohen Speichern) ohne Python. Ein schmaler Unpickler: führt nichts aus, unbekannte Klassen werden zu Platzhaltern. |
| `OnnxGraph.cs` | Schreibt ONNX-Modelle (Protobuf, nur die nötigen Felder, ohne Abhängigkeit). |
| `SyntheticDocNet.cs` | Baut das Netz aus `extern/SyntheticDoc/training/model.py` nach (UVDocNet + Schatten-Decoder), BatchNorm in die Faltungen gerechnet, Eingabegröße offen. Ausgänge `bm_map` (1 × 2 × 121 × 177, −1 … 1, align_corners = True) und auf Wunsch `shadow_map`. |
| `SyntheticDocModel.cs` | Checkpoint → `syntheticdoc.onnx` (nur Gitter) und `syntheticdoc-schatten.onnx` (mit Schattenkarte) daneben, beim ersten Gebrauch (gut 2 s) und neu, sobald der Checkpoint jünger ist. |
| `SyntheticDocDewarp.cs` | Zwei Läufe: grob auf 720 × 512 ohne Schatten (nur um das Blatt zu finden), dann genau auf 1440 × 1024 auf dem Blattausschnitt + 4 % (so wurde trainiert), mit Schattenkarte. Daraus das Gitter, Seitenverhältnis über unser Blattmodell mit den Gitterlinien (DIN-Einrasten), und die Schattenkarte gerade gezogen (256 × 360, `DewarpShading`). |

Geprüft: Die C#-Umwandlung weicht von einem Export mit PyTorch höchstens um 5 · 10⁻⁷ ab. Rechenzeit auf
dem Entwicklungsrechner (Notebook-Prozessor, CPU): grober Lauf 0,3 s, genauer 0,6 s, mit Bildaufbereitung
insgesamt 1,2–1,4 s je Bild ohne Schatten, 1,8–1,9 s mit. Den Schatten-Zweig rechnet ONNX Runtime immer
mit, auch wenn man nur das Gitter abfragt (verdoppelt die Zeit) — deshalb zwei Dateien: der grobe Lauf nimmt
die ohne. `OnnxSessions` hält beide offen (Dateien desselben Modells schließen sich nicht gegenseitig).

Im Rezept ist die KI-Begradigung dieselbe Stufe wie „Gewölbtes Blatt“ (`DewarpSpec.Grid` statt Linien,
`GeometryRecipe.AiDewarpedInView` dreht das Gitter der Ansicht ins Pixelbild zurück, die Schattenkarte mit).

### Schatten entfernen

Das Netz sagt neben dem Gitter eine **Schattenkarte** voraus: wie viel Licht jede Stelle des Blatts bekam
(Knitter, Falten, Hand- und Handyschatten, ungleichmäßige Lampe). Die App rechnet sie wie die Autoren
(`training/inference.py`) heraus: Farbwert ÷ (Licht · Stärke + 1 − Stärke), Teiler mindestens 0,1; Stärke
= min(1, 0,4 / mittlere Helligkeit des Ausschnitts), helle Fotos also schwächer, damit nichts ausbrennt.

- Die Karte wird immer mitgerechnet (+0,55 s) und gerade gezogen im Rezept gespeichert (256 × 360 × RGB,
  `DewarpShading` in `DewarpGrid`); herausgerechnet wird beim Rendern in `DewarpMap.Render`.
- Ein- und ausschalten: **Rechtsklick auf das KI-Symbol → „Schatten entfernen“**. Die Wahl merkt sich die App
  (`AppSettings.AiRemoveShadows`, voreingestellt an); ist die KI-Begradigung schon im Bild, schaltet der Klick
  sie ohne neuen KI-Lauf um (ein Schritt, Strg+Z).
- Gemessen: künstliches Blatt, rechte Hälfte mit 60 % Licht → Karte links 0,94, rechts 0,62; Papier rechts
  von 0,57 auf 0,83 aufgehellt (`SyntheticDocTests`). Echte Fotos (63_2, 54_2, 58_2, Arbeitsblatt): Papier
  gleichmäßig hell, Knitterschatten deutlich schwächer, graue Kästen bleiben grau; Vergleich ohne | mit in
  `TestBilder/Merge/schatten/` (nicht im Git). Ein leichter Farbstich des Lichts wird mit entfernt.

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

1. **Papier simulieren** (`generation/simulation/`, ARCSim kompilieren — Anleitung im README; statt `wget` das
   Archiv aus `KI-Modelle/syntheticdoc/arcsim-0.2.1.tar.gz` nehmen, falls die Berkeley-Seite fehlt):
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
   `KI-Modelle/syntheticdoc/highres_121x177.ckpt` ablegen (den alten vorher wegsichern). Die App baut beide
   ONNX-Dateien beim nächsten Klick neu, weil der Checkpoint jünger ist. Zum Prüfen ohne App:
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

## Updates des Originals

Unsere Kopie in `extern/SyntheticDoc/` ändert sich nicht von selbst. Ob es beim Original etwas Neues gibt, zeigt

    tools/SyntheticDocUpdate.sh

(Linux oder Git Bash; ändert nichts). Es lädt das Repository von GitHub, vergleicht es mit dem Stand unserer
Kopie (`extern/SyntheticDoc/ORIGINAL.sha256`, Prüfsummen des Originals vom 12.10.2026, also ohne unsere
Ergänzungen), prüft Größe und Datum der Gewichte bei der ETH und sucht im README nach neuen Checkpoint-Namen.
Rückgabe 0 = nichts Neues. Stand 13.10.2026: nichts Neues (letzter Commit 9.9., nur ein Link im README;
Gewichte vom 6.8.).

**Bewerten**, je nachdem, was sich geändert hat:

| Geändert | Brauchen wir es? |
|---|---|
| README, `generation/`, `training/` außer den drei unten | Für die App nicht. Nur übernehmen, wenn wir selbst trainieren (Dateien kopieren, `train.py` mit unseren „ImageConverter:“-Stellen zusammenführen). |
| `training/inference.py`, `unwarp.py` | Vorverarbeitung oder Schattenformel: mit `SyntheticDocDewarp.cs` und `DewarpShading` vergleichen; übernehmen, wenn es bei den Testbildern besser aussieht. |
| `training/model.py` | Nur zusammen mit neuen Gewichten wichtig: `SyntheticDocNet.cs` nachziehen, dann muss `SyntheticDocTests` (echter Checkpoint) wieder grün sein. Mit den alten Gewichten bleibt alles, wie es ist. |
| **Gewichte** (Größe/Datum anders oder neuer Name im README) | Der eigentliche Fall — siehe unten. |

**Neue Gewichte prüfen:**

1. In einen eigenen Ordner laden, nicht über unseren Checkpoint. Lizenz nachsehen (bisher MIT).
2. Vergleichen, links alt, rechts neu:

       dotnet run tools/SyntheticDocVergleich.cs --property:UseArtifactsOutput=true --property:ArtifactsPath=<ordner außerhalb> -- \
           KI-Modelle/syntheticdoc/highres_121x177.ckpt <neu.ckpt> <ausgabe> <bilder …>

   Bilder: die schwierigen DocUNet-Fotos 63_2, 58_2, 60_1, 54_2, dazu einige weitere aus `Downloads/crop/crop`
   und die Generator-Bilder `TestBilder/*-schraeg.jpg`. Das Werkzeug meldet je Bild, wie weit die Gitter
   auseinanderliegen. Unter 2 px ändert sich praktisch nichts, dann lohnt der Wechsel nicht.
3. Ansehen: Sind Textzeilen und Blattkanten im neuen Ergebnis gerader, Ränder vollständiger, Schatten besser
   entfernt? Übernehmen nur, wenn die schwierigen Fotos klar besser werden und kein anderes schlechter.
4. Übernehmen: Checkpoint nach `KI-Modelle/syntheticdoc/` (die App baut die ONNX-Dateien selbst neu),
   Größe und SHA-256 in `AiModelCatalog.SyntheticDoc` und in `tools/SyntheticDocUpdate.sh` eintragen, bei
   neuem Code `tools/SyntheticDocUpdate.sh --stand-merken` und die Dateien nach `extern/SyntheticDoc/` kopieren.
   Ein anderes Gitter als 121 × 177 braucht Anpassungen in `SyntheticDocDewarp` (das Werkzeug lehnt es ab).

## SyntheticDoc wieder entfernen

1. Ordner löschen: `ImageConverter.Core/Ai/SyntheticDoc/`, `extern/SyntheticDoc/`,
   `ImageConverter.Tests/SyntheticDocTests.cs`, auf der Platte `KI-Modelle/syntheticdoc/`.
   `ImageConverter.Core/Ai/Onnx/` bleibt — PiSA-SR (Upscaling) braucht es auch.
2. `ImageConverter.Core/Ai/AiModels.cs`: Eintrag `SyntheticDoc` und seine Erwähnung in `All`
   (dazu nach Wunsch `AiModelKind.Dewarp`).
3. `ImageConverter.Ui/ViewModels/MainViewModel.Dewarp.cs`: den Abschnitt „KI begradigen (SyntheticDoc)“
   (`AiStraightenActive`, `IsStraighteningWithAi`, `AiRemoveShadows`, `SwitchAiShadowsAsync`, `StraightenWithAi`).
4. `ImageConverter.Ui/ViewModels/MainViewModel.Edits.cs`: in der Bearbeitungsliste die Zweige für
   `spec.Grid` („KI begradigt“, „Schatten entfernt“) und `OnPropertyChanged(nameof(AiStraightenActive))`.
5. `ImageConverter.Ui/Views/MainView.axaml`: den Rahmen um die zwei Symbole und das Symbol „KI begradigen“
   samt Rechtsklickmenü.
6. `ImageConverter.Core/Localization/English.cs`: die Texte zu SyntheticDoc, „KI begradig…“ und „Schatten …“
   (`LocalizationTests` meldet, was übrig ist).
7. `ImageConverter.Core/Platform/AppSettings.cs`: `AiRemoveShadows` (darf auch bleiben).

Allgemein und ohne SyntheticDoc harmlos (darf bleiben): `DewarpGrid`, `DewarpShading`, `DewarpSpec.Grid`,
`DewarpMap.Ai` und `.Shading`, `GeometryRecipe.AiDewarpedInView` samt `AiDewarpTests`, `AiModelItem.ShowDownload`
ohne Adresse, `OnnxSessions.Run` mit Ausgabenamen und das Offenhalten mehrerer Dateien eines Modells.

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

## Was für ein Training von außen kommt

Im Projekt (`extern/SyntheticDoc/`) liegt der ganze Code: Papiersimulation, Rendern, Rückabbildung, Packen,
Training — und das Training lernt Gitter **und** Schattenkarte zusammen (`shadow.png` beim Rendern), eigene
Daten taugen also auch fürs Schatten-Entfernen. Von außen kommen:

| Was | Woher | Aufgehoben |
|---|---|---|
| Checkpoint `highres_121x177.ckpt` | ETH (Katalog der App) | `KI-Modelle/syntheticdoc/`, Sicherung |
| ARCSim 0.2.1 | Berkeley | `KI-Modelle/syntheticdoc/`, Sicherung |
| Blender 4.5 LTS | blender.org (Programm) | — |
| Assets der Autoren: Meshes, Seiten, Hintergründe | ETH Research Collection, DOI 10.3929/ethz-c-000804058 | — (Größe noch nicht geprüft) |
| Datensatz SyntheticDoc (1 Mio. Beispiele) | ETH Research Collection, DOI 10.3929/ethz-c-000801994 | — (sehr groß) |

Ohne die Assets: Meshes selbst simulieren (Schritt 1), eigene Seiten als Dokumente, Hintergründe als freie
PBR-Materialien (z. B. ambientCG, CC0) in `generation/rendering/backgrounds/` — je Material ein Ordner mit
den Karten (Farbe, Rauheit, Normalen …).
