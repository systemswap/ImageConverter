# KI auf der Grafikkarte

Ist eine Grafikkarte da, rechnen alle KI-Modelle der App darauf — ohne Schalter (Wunsch des Benutzers, 14.10.2026).
Lässt sich ein Modell auf der Grafikkarte nicht öffnen, rechnet es auf der CPU; was gerade gilt, steht in den
Einstellungen › KI-Modelle (`AiViewModel.GpuStatus`).

## Stand (0.5.79 beta)

| System | Hersteller | Weg | Stand |
|---|---|---|---|
| Windows | AMD, NVIDIA, Intel | DirectML (DirectX 12) über das Paket `Microsoft.ML.OnnxRuntime.DirectML` | **eingebaut**, gemessen auf der Radeon des Notebook-Prozessor |
| Linux | AMD (RX 6650 XT im Debian-Rechner) | MIGraphX (ROCm) oder WebGPU (Vulkan) | **App vorbereitet**, Laufzeit fehlt (siehe unten) |
| Linux | NVIDIA | CUDA (`Microsoft.ML.OnnxRuntime.Gpu.Linux`) | **App vorbereitet**, Paket nicht eingebunden |
| Windows | NVIDIA, schneller als DirectML | CUDA oder TensorRT-RTX | nicht nötig, DirectML reicht für den Anfang |

**So wählt die App** (`OnnxSessions`): Sie fragt die Laufzeit, welche Anbindungen sie mitbringt
(`GetAvailableProviders`), und nimmt die erste aus DirectML, MIGraphX, ROCm, CUDA, WebGPU. Für DirectML gilt: kein
Speichermuster, Ausführung der Reihe nach (Vorgabe von DirectML). Scheitert das Öffnen, merkt sie sich den Grund
(`GpuProblem`) und öffnet auf der CPU. Die Zeitschätzung der Upscaler kennt CPU und Grafikkarte getrennt
(`Upscalers.OnGpu`).

**Laufzeit-Fassung:** DirectML pflegt Microsoft nur noch (keine neuen Funktionen), das Paket gibt es bis 1.24.4 (März
2026). Damit Programmschnittstelle (`Microsoft.ML.OnnxRuntime.Managed`) und native Laufzeit zusammenpassen, nutzt die App
überall 1.24.4 (vorher 1.30). Welches Paket, entscheidet `ImageConverter.Core.csproj` nach Zielplattform (`-r win-x64`
→ DirectML) bzw. beim Bauen ohne Plattform nach dem System. `DirectML.dll` kopiert das Projekt selbst neben die Laufzeit
(das Paket tut es nur bei fester x64-Plattform); `NativeLibraries` lädt sie vor `onnxruntime.dll` aus `Tools/OnnxRuntime`,
sonst nähme Windows die ältere aus System32.

## Messungen (Notebook-Prozessor mit Radeon Vega 8, gemeinsamer Speicher, 14.10.2026)

Panorama-Ausschnitt 500 × 300 → 2000 × 1200, mit Laden der Modelle:

| Modell | CPU (8 Kerne) | Grafikkarte (DirectML) | Ergebnis gleich? |
|---|---|---|---|
| x4v3 („KI schnell“) | 3,2 s | 2,0 s | ja (PSNR identisch) |
| PiSA-SR („KI wie Referenzwerkzeug“) | 355 s | 137 s | ja (PSNR identisch) |
| UltraSharpV2 („KI mittel“) | 120 s | 80 s | ja (PSNR identisch) |

## Was noch gebraucht wird

1. **AMD unter Linux (RX 6650 XT, RDNA2 / gfx1032):**
   - Es gibt kein fertiges NuGet-Paket: den ROCm-Weg hat ONNX Runtime seit 1.23 entfernt, für den Nachfolger MIGraphX
     liefern weder Microsoft noch AMD Pakete für .NET (nur Python-Pakete von AMD, Community-Bauten).
   - **Weg A, MIGraphX:** auf dem Debian-Rechner ROCm 7 installieren (AMD-Paketquelle; die RX 6650 XT steht nicht in
     AMDs Liste, läuft üblicherweise mit `HSA_OVERRIDE_GFX_VERSION=10.3.0`), dann ONNX Runtime 1.24.4 selbst bauen:
     `./build.sh --config Release --build_shared_lib --parallel --use_migraphx --migraphx_home /opt/rocm`. Die fertige
     `libonnxruntime.so` (+ `libonnxruntime_providers_migraphx.so`, `…_shared.so`) nach `Tools/OnnxRuntime/` neben die
     AppImage legen — die App nimmt sie von dort und erkennt MIGraphX selbst. Mehrere GB ROCm, Bauzeit rund eine Stunde.
   - **Weg B, WebGPU (Vulkan):** herstellerunabhängig, kein ROCm nötig (Mesa/RADV reicht). ONNX Runtime mit
     `--use_webgpu` bauen (Dawn wird mitgebaut). Neuer und weniger erprobt als MIGraphX, dafür auch für NVIDIA und Intel.
   - Zu entscheiden auf dem Debian-Rechner: erst B versuchen (weniger Abhängigkeiten), A, wenn B zu langsam ist.
   - Danach: Bauskript `tools/OnnxRuntimeLinuxGpu.sh` und `publish.sh` so, dass die eigene Laufzeit statt der aus dem
     Paket ins `Tools/OnnxRuntime` kommt.
2. **NVIDIA unter Linux:** Paket `Microsoft.ML.OnnxRuntime.Gpu.Linux` 1.24.4 für `linux-x64` einbinden (statt
   `Microsoft.ML.OnnxRuntime`); braucht CUDA 12 und cuDNN 9 aus der Paketverwaltung. Erst, wenn ein NVIDIA-Rechner da ist —
   das Paket ist groß und hilft der AMD-Karte nicht.
3. **Halbe Genauigkeit für die Grafikkarte:** PiSA-SR in fp16 bauen (`PiSaSrNet` schreibt die Gewichte als fp16, Cast an
   Ein- und Ausgängen) — halber Grafikspeicher (1,7 statt 3,4 GB), auf richtigen Grafikkarten deutlich schneller. Messen,
   ob das Ergebnis gleich bleibt (Referenzwerte in `~/.cache/imageconverter-build/pisa/ref/`).
4. **Größere Kacheln auf der Grafikkarte:** Kachelgrößen hängen heute an der CPU (`PiSaSrRun`: 96/64/64 Latent,
   Kachelmodelle 128 px). Auf einer Karte mit 8 GB wären größere Kacheln schneller (weniger Überlappung).
5. **Speicher prüfen:** vor dem Öffnen grob prüfen, ob der Grafikspeicher reicht; sonst gleich CPU statt Fehler.
6. **Fehler während der Rechnung** (Gerät verloren, Speicher voll): heute Meldung; besser einmal auf der CPU wiederholen.
7. **Windows später:** DirectML wird nicht weiterentwickelt. Wenn Microsoft das Paket aufgibt: Windows ML
   (`Microsoft.Windows.AI.MachineLearning`, Windows App SDK) — bringt DirectML und lädt Hersteller-Anbindungen (NVIDIA
   TensorRT-RTX, AMD MIGraphX) über Windows Update nach.

Quellen: ONNX Runtime — Execution Providers (DirectML, ROCm „removed since 1.23“, MIGraphX, CUDA), Install-Seite
(DirectML „sustained engineering“), Microsoft Learn „What is Windows ML?“; NuGet-Abfrage vom 14.10.2026
(`Microsoft.ML.OnnxRuntime.DirectML` höchstens 1.24.4, kein MIGraphX- oder WebGPU-Paket).
