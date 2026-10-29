# Hintergrund entfernen (Konzept, in Arbeit)

Stand 29.10.2026. Eigene Rubrik „Hintergrund“ in der Leiste links. Das Modell stellt das Motiv frei; der Hintergrund wird
**transparent** oder **unscharf** (Porträt-Effekt, Regler für die Stärke). Nachbessern mit dem Pinsel (behalten / weg).
Ein Bildschritt in „Bearbeitungen“ (ausschaltbar, Rückgängig), vor Größe/Upscaling und allen Filtern.

## Modelle

Die App liefert keine Modelle mit; jeder Nutzer lädt sie selbst (Download-Manager, Gruppe „HINTERGRUND“). Darum gelten
die Lizenzen für den jeweiligen Nutzer — die App darf kostenlos weitergegeben werden.

| Modell | Datei | Größe | SHA-256 | Eingang | Lizenz | Quelle |
|---|---|---|---|---|---|---|
| BiRefNet general (Standard) | `BiRefNet-general-epoch_244.onnx` | 972 666 916 | `58f621f00f5d756097615970a88a791584600dcf7c45b18a0a6267535a1ebd3c` | 1024², ImageNet-normiert | MIT | GitHub-Release `ZhengPeng7/BiRefNet` v1 |
| BiRefNet lite (Swin tiny) | `BiRefNet-general-bb_swin_v1_tiny-epoch_232.onnx` | 224 005 088 | `5600024376f572a557870a5eb0afb1e5961636bef4e1e22132025467d0f03333` | 1024², ImageNet-normiert | MIT | dito |
| BiRefNet HR-matting | `BiRefNet_HR-matting-epoch_135.onnx` | 1 098 928 867 | `45b7d92ce0e2e75e1c8a564a9e84c87ac9da7f4315d2deeef3674864e809aef3` | 2048², ImageNet-normiert | MIT | dito |
| BEN2 base | `BEN2_Base.onnx` | 222 932 053 | `22cea62108ff53b7ccc20f7a008bf30494228d84b1687f29ecbe76936a998101` | 1024², 0…1 **ohne** Normierung | MIT | Hugging Face `PramaLLC/BEN2` |
| RMBG-2.0 (Bria) | `onnx/model.onnx` | 1 024 331 469 | `5b486f08200f513f460da46dd701db5fbb47d79b4be4b708a19444bcd4e79958` | beliebig (gerechnet 1024²), ImageNet-normiert, Eingang `pixel_values`, Ausgang `alphas` | CC BY-NC 4.0 — **nur nicht-kommerziell** | Hugging Face `briaai/RMBG-2.0` (Anmeldung nötig) |

Nicht genommen: `BiRefNet_lite-general-2K` — fester Eingang 1440 × 2560 (Hochformat), Querformate würden verzerrt.

Ausgänge: je nach Modell Logits oder schon 0…1 — der Messplatz prüft den Wertebereich und rechnet Logits mit dem Sigmoid um.

### RMBG-2.0 von Hand laden

Bria gibt das Modell nur nach Zustimmung frei; die App kann es deshalb nicht selbst laden.

1. Bei Hugging Face anmelden (kostenloses Konto) und https://huggingface.co/briaai/RMBG-2.0 öffnen.
2. Im Formular Name und E-Mail angeben und den Haken setzen („Non commercial use … Personal use / Academy / Non
   profit“). Damit gehen Name und E-Mail an Bria; der Zugang ist sofort frei.
3. Im selben Browser https://huggingface.co/briaai/RMBG-2.0/resolve/main/onnx/model.onnx öffnen (1,02 GB, volle
   Genauigkeit; die anderen Dateien im Ordner `onnx` sind kleinere, ungenauere Fassungen).
4. Die Datei in den Modellordner der App legen (`KI-Modelle/` neben dem Programm, Unterordner wie im
   Download-Manager angegeben) — die App erkennt sie an Größe und Prüfsumme.

Wer die App weitergibt: Jeder Nutzer muss diese Zustimmung selbst geben.

## Rechnung

1. Bild auf die Kante des Modells (1024 bzw. 2048), rechnen — Windows auf der Grafikkarte (DirectML) wie die Upscaler,
   sonst CPU.
2. Maske kantentreu auf die volle Auflösung (Guided Filter, Führung = Originalfoto) — keine Treppen auch bei 24 MP.
3. Farbsaum entfernen: Vordergrundfarbe der halbtransparenten Pixel schätzen (Germer u. a. 2020, „Fast Multi-Level
   Foreground Estimation“), sonst bleibt an Haaren ein Rand in der alten Hintergrundfarbe.
4. Ergebnis: Alpha (16 Bit) zum Bild. Speichern: PNG, WebP, TIFF, AVIF, JPEG XL mit Transparenz; JPEG/BMP mit Weiß
   (Hinweis in „Format“). Drucken: Weiß. Upscaling vergrößert die Maske mit.

## Messplatz

`~/.cache/imageconverter-build/hintergrund/` (`messplatz.py`: CPU-Zeiten, Masken, Vergleichsbögen; `gpu.py`: Zeit mit
DirectML und Abweichung zur CPU). Acht Testbilder: lange Haare, Zuckerwatte, Zopf vor Palmen, ganze Person mit Blumen,
Schildkröte mit zwei Personen, Gruppe am Strand, Fliegenpilz, kahle Äste vor Himmel. Ergebnis folgt.
