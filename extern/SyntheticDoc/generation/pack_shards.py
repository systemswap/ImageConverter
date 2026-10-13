"""ImageConverter: gerenderte Beispiele zu Trainings-Paketen packen (wie der veröffentlichte Datensatz).

Nimmt die Ordner aus `rendering/renders/` (je Beispiel ein Ordner mit render.png, shadow.png, backward_map.npy,
metadata.json — backward_map.npy aus `backward_mapping/uv_to_backward_map.py`) und schreibt unkomprimierte Zip-Pakete zu je
1000 Beispielen, aufgeteilt in train/val/test, so wie `training/dataset.py` sie liest.

    python pack_shards.py --input rendering/renders --output ../training/data/eigen --val 0.05 --test 0.0

Unvollständige Beispiele (eine Datei fehlt oder metadata.json meldet keinen Erfolg) werden übersprungen.
"""
import argparse
import json
import random
import zipfile
from pathlib import Path

NEEDED = ("render.png", "shadow.png", "backward_map.npy", "metadata.json")


def complete(folder: Path) -> bool:
    if not all((folder / name).is_file() for name in NEEDED):
        return False
    try:
        return json.loads((folder / "metadata.json").read_text()).get("status", "success") == "success"
    except (OSError, ValueError):
        return False


def write_shards(samples, out_dir: Path, prefix: str, per_shard: int):
    out_dir.mkdir(parents=True, exist_ok=True)
    count = 0
    for start in range(0, len(samples), per_shard):
        index = start // per_shard
        path = out_dir / f"{prefix}_{index:03d}.zip"
        with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_STORED) as zf:
            for folder in samples[start:start + per_shard]:
                for name in NEEDED:
                    zf.write(folder / name, f"{folder.name}/{name}")
        count += 1
    if count:
        print(f"{prefix}: {len(samples)} Beispiele in {count} Paketen → {out_dir}/{prefix}_{{000..{count - 1:03d}}}.zip")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--input", required=True, help="Ordner mit den gerenderten Beispielen (je Beispiel ein Unterordner)")
    parser.add_argument("--output", required=True, help="Zielordner; darin entstehen train/, val/, test/")
    parser.add_argument("--val", type=float, default=0.05, help="Anteil für die Validierung")
    parser.add_argument("--test", type=float, default=0.0, help="Anteil für den Test")
    parser.add_argument("--per-shard", type=int, default=1000, help="Beispiele je Paket (train.py rechnet mit 1000)")
    parser.add_argument("--seed", type=int, default=0)
    args = parser.parse_args()

    folders = sorted(p for p in Path(args.input).iterdir() if p.is_dir())
    samples = [f for f in folders if complete(f)]
    print(f"{len(samples)} von {len(folders)} Beispielen vollständig")
    random.Random(args.seed).shuffle(samples)
    n_val, n_test = int(len(samples) * args.val), int(len(samples) * args.test)
    out = Path(args.output)
    write_shards(samples[n_val + n_test:], out / "train", "train", args.per_shard)
    write_shards(samples[:n_val], out / "val", "val", args.per_shard)
    write_shards(samples[n_val:n_val + n_test], out / "test", "test", args.per_shard)


if __name__ == "__main__":
    main()
