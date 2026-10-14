# PiSA-SR im ImageConverter

Kopie von <https://github.com/csslc/PiSA-SR> (Commit `9c76e12e261d1980148738a0cff41e5e27d18c9f` vom 15.03.2026, kopiert am
14.10.2026), damit der Rechenweg nachvollziehbar bleibt, auch wenn das Original verschwindet. Weggelassen: `figs/`
(Bilder der README), `.idea/`. Die README nennt die Apache-2.0-Lizenz; die dort verlinkte Datei `LICENSE` fehlt im
Repository selbst.

**Unverändert** bis auf eine Zeile: in `ram/models/utils.py` stand ein Pfad auf dem Rechner der Autoren
(`/home/…/bert-base-uncased`), ersetzt durch einen Platzhalter (der Teil „RAM“ erzeugt Beschreibungen und wird für die
leere Beschreibung nicht gebraucht; `ORIGINAL.sha256` nennt für diese Datei die Prüfsumme des Originals). Die App nutzt
diesen Code nicht — sie baut das Netz in C# nach
(`ImageConverter.Core/Ai/Upscale/PiSaSrNet.cs`, gegen dieses Original geprüft mit `tools/PiSaSrUmwandeln.cs --pruefen`).
Maßgeblich für den Nachbau: `pisasr.py`, Klasse `PiSASR_eval` im Modus „Standard“ (`--default`).

Die Gewichte liegen nicht hier: LoRA `pisa_sr.pkl` (Google Drive der Autoren) und Stable Diffusion 2.1-base
(Spiegel auf Hugging Face) lädt die App selbst — Adressen und Prüfsummen in `AiModelCatalog`, Hintergrund in
`docs/KI-UPSCALING.md`.

**Stand des Originals:** `ORIGINAL.sha256` enthält die Prüfsummen der Kopie (ohne `figs/`, `.idea/`).
