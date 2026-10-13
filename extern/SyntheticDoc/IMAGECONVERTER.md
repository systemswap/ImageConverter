# SyntheticDoc im ImageConverter

Kopie von <https://github.com/tanguymagne/SyntheticDoc> (Stand 12.10.2026, MIT-Lizenz, siehe `LICENSE`),
damit Training und Datenerzeugung ohne das Original-Repository möglich bleiben. Weggelassen: der Ordner
`media/` (nur Bilder für die README). Die Datensätze und die Assets (Meshes, Seiten, Hintergründe) liegen
weiterhin bei der ETH Zürich (Links in `README.md` und `DATASET.md`).

**Eigene Änderungen** (alle mit „ImageConverter:“ kommentiert):

- `training/train.py`: `--init-weights <ckpt>` (nur die Gewichte als Start, Epochen und Lernplan neu —
  zum Nachtrainieren mit eigenen Daten) und `--accelerator gpu|cpu|auto` (vorher fest `gpu`).
- `generation/pack_shards.py` (neu): gerenderte Beispiele zu Zip-Paketen zu je 1000 packen, wie
  `training/dataset.py` sie liest (der veröffentlichte Datensatz kommt schon so; für eigene Daten fehlte
  dieser Schritt).

Wie die App das Modell nutzt und wie man nachtrainiert: `docs/KI-BEGRADIGEN.md` im Projekt.

**Stand des Originals:** `ORIGINAL.sha256` enthält die Prüfsummen des Originals (ohne `media/`) zum Zeitpunkt
der Kopie. `tools/SyntheticDocUpdate.sh` vergleicht das heutige Original damit und bewertet, was sich geändert hat.
