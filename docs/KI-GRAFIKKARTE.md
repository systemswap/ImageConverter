# KI auf der Grafikkarte

Ist eine Grafikkarte da, rechnen alle KI-Modelle der App darauf — ohne Schalter (Wunsch des Benutzers, 14.10.2026).
Lässt sich ein Modell auf der Grafikkarte nicht öffnen, rechnet es auf der CPU; was gerade gilt, steht in den
Einstellungen › KI-Modelle (`AiViewModel.GpuStatus`).

## Stand (0.5.79 beta)

| System | Hersteller | Weg | Stand |
|---|---|---|---|
| Windows | AMD, NVIDIA, Intel | DirectML (DirectX 12) über das Paket `Microsoft.ML.OnnxRuntime.DirectML` | **eingebaut**, gemessen auf einer integrierten Grafikkarte (Laptop) |
| Linux | AMD (eigene Karte im Debian-Rechner) | MIGraphX (ROCm) oder WebGPU (Vulkan) | **App vorbereitet**, Laufzeit fehlt; WebGPU gebaut und gemessen (0.5.96): lohnt hier nicht, siehe „WebGPU unter Linux“ |
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

## Messungen (Laptop mit integrierter Grafik, gemeinsamer Speicher, 14.10.2026)

Panorama-Ausschnitt 500 × 300 → 2000 × 1200, mit Laden der Modelle:

| Modell | CPU (8 Kerne) | Grafikkarte (DirectML) | Ergebnis gleich? |
|---|---|---|---|
| x4v3 („KI schnell“) | 3,2 s | 2,0 s | ja (PSNR identisch) |
| PiSA-SR („KI langsam“) | 355 s | 137 s (fp32), **97 s (fp16)** | fp32 identisch; fp16 gegen fp32 53 dB, im Mittel 0,2 von 255 Stufen, zum wahren Bild 23,41 statt 23,43 dB |
| UltraSharpV2 („KI mittel“) | 120 s | 80 s | ja (PSNR identisch) |

## Was noch gebraucht wird

1. **AMD unter Linux (eigene Karte des Debian-Rechners, RDNA2 / gfx1032):**
   - Es gibt kein fertiges NuGet-Paket: den ROCm-Weg hat ONNX Runtime seit 1.23 entfernt, für den Nachfolger MIGraphX
     liefern weder Microsoft noch AMD Pakete für .NET (nur Python-Pakete von AMD, Community-Bauten).
   - **Weg A, MIGraphX:** auf dem Debian-Rechner ROCm 7 installieren (AMD-Paketquelle; die Karte steht nicht in
     AMDs Liste, läuft üblicherweise mit `HSA_OVERRIDE_GFX_VERSION=10.3.0`), dann ONNX Runtime 1.24.4 selbst bauen:
     `./build.sh --config Release --build_shared_lib --parallel --use_migraphx --migraphx_home /opt/rocm`. Die fertige
     `libonnxruntime.so` (+ `libonnxruntime_providers_migraphx.so`, `…_shared.so`) nach `Tools/OnnxRuntime/` neben die
     AppImage legen — die App nimmt sie von dort und erkennt MIGraphX selbst. Mehrere GB ROCm, Bauzeit rund eine Stunde.
   - **Weg B, WebGPU (Vulkan):** herstellerunabhängig, kein ROCm nötig (Mesa/RADV reicht). ONNX Runtime mit
     `--use_webgpu` bauen (Dawn wird mitgebaut). Neuer und weniger erprobt als MIGraphX, dafür auch für NVIDIA und Intel.
   - Zu entscheiden auf dem Debian-Rechner: erst B versuchen (weniger Abhängigkeiten), A, wenn B zu langsam ist.
     **B versucht (18.10.2026), zu langsam** — Messungen im Abschnitt „WebGPU unter Linux“; offen ist A.
   - Danach: Bauskript `tools/OnnxRuntimeLinuxGpu.sh` und `publish.sh` so, dass die eigene Laufzeit statt der aus dem
     Paket ins `Tools/OnnxRuntime` kommt.
2. **NVIDIA unter Linux:** Paket `Microsoft.ML.OnnxRuntime.Gpu.Linux` 1.24.4 für `linux-x64` einbinden (statt
   `Microsoft.ML.OnnxRuntime`); braucht CUDA 12 und cuDNN 9 aus der Paketverwaltung. Erst, wenn ein NVIDIA-Rechner da ist —
   das Paket ist groß und hilft der AMD-Karte nicht.
3. ~~Halbe Genauigkeit für die Grafikkarte~~ — **erledigt in 0.5.81 beta** für PiSA-SR, automatisch (keine Auswahl):
   auf der Grafikkarte fp16, auf der CPU fp32 (die rechnet fp16 nicht schneller). `PiSaSrNet` mit `half`: Gewichte und
   Rechnung fp16, GroupNorm/LayerNorm/Softmax in fp32, Ein-/Ausgänge fp32; eigene Dateien `pisasr-*-fp16.onnx` (1,8 GB),
   gebaut beim ersten Gebrauch auf der Grafikkarte. Teile gegen PyTorch: Encoder/Decoder 0,3–0,7 %, UNet 1,4 % relativ;
   am ganzen Bild nicht zu sehen (siehe Tabelle). Öffnet die Grafikkarte doch nicht, nimmt die App die fp32-Dateien.
   Offen: x4v3 und UltraSharpV2 in fp16 (fremde ONNX-Dateien — bräuchte einen Umwandler für beliebige Graphen).
4. **Größere Kacheln auf der Grafikkarte:** Kachelgrößen hängen heute an der CPU (`PiSaSrRun`: 96/64/64 Latent,
   Kachelmodelle 128 px). Auf einer Karte mit 8 GB wären größere Kacheln schneller (weniger Überlappung).
5. **Speicher prüfen:** vor dem Öffnen grob prüfen, ob der Grafikspeicher reicht; sonst gleich CPU statt Fehler.
6. **Fehler während der Rechnung** (Gerät verloren, Speicher voll): heute Meldung; besser einmal auf der CPU wiederholen.
7. **Windows später:** DirectML wird nicht weiterentwickelt. Wenn Microsoft das Paket aufgibt: Windows ML
   (`Microsoft.Windows.AI.MachineLearning`, Windows App SDK) — bringt DirectML und lädt Hersteller-Anbindungen (NVIDIA
   TensorRT-RTX, AMD MIGraphX) über Windows Update nach.

## Geschwindigkeitstest: Grafikkarte oder CPU (0.5.100 beta)

Wunsch des Benutzers (18.10.2026): Nicht annehmen, dass die Grafikkarte schneller ist, sondern messen und das
Schnellere nehmen — auf dem Debian-Rechner mit 16-Kern-Prozessor ist die CPU oft gleich schnell oder schneller (siehe
unten), auf dem Laptop die Grafikkarte deutlich schneller.

- **Wo:** Einstellungen › Leistung (seit 0.5.103 eigene Seite links ganz oben; vorher unter „KI-Modelle“), Knopf
  „Geschwindigkeit testen“; dort stehen auch, worauf gerade gerechnet wird, und das letzte Ergebnis je Bereich (Zeiten,
  Wahl, Datum).
- **Was gemessen wird** (`Core/Platform/SpeedTest.cs`), je Gerät ein Aufwärmlauf, dann die beste von zwei Zeiten:
  - **Bildfilter:** Entrauschen und Dunst an einem Testbild von 1200 × 900 (ILGPU/OpenCL gegen die CPU-Schleifen).
  - **KI:** Real-ESRGAN x4v3 („Allround“, 5 MB) an 300 × 300 in eigenen Sitzungen — die geöffneten Modelle der App bleiben
    unberührt. Ohne Grafikkarten-Anbindung der Laufzeit (Linux heute) wird nichts verglichen; fehlt x4v3, bittet der Test
    darum. x4v3 steht stellvertretend für alle KI-Modelle (auf dem Laptop verhielten sich x4v3, UltraSharpV2 und PiSA-SR
    gleich).
- **Wirkung:** Ergebnis in `settings.json` (`FilterSpeed`, `AiSpeed`); beim Start übernommen (`GpuFilters.Measured`,
  `OnnxSessions.Measured`). War die CPU schneller oder gleich schnell, rechnet der Bereich auf der CPU
  (`SpeedMeasure.PreferGpu`). Ohne Test gilt wie bisher: Grafikkarte, wenn vorhanden.
- **Neues System:** Jedes Ergebnis trägt eine Kennung des Rechners (Grafikkarte bzw. Laufzeit-Anbindung und Zahl der
  Kerne). Passt sie nicht mehr, gilt das Ergebnis nicht (wieder Grafikkarte, wenn vorhanden), und die Anzeige bittet um
  einen neuen Test.
- **Test:** `SpeedTestTests` (Auswahl, anderer Rechner, Speichern, Messung der Filter; Gegenprobe: mit immer
  „Grafikkarte“ schlägt `PreferGpu_TakesTheFasterOne_OnTheMeasuredComputer` fehl).
- **Beobachtung auf dem Debian-Rechner:** Es ist kein OpenCL-Treiber installiert (`/etc/OpenCL/vendors` fehlt) —
  Entrauschen und Dunst rechnen dort deshalb auf der CPU (1,2 s für das Testbild). Für AMD: Mesa-OpenCL (Debian
  `mesa-opencl-icd`, Fedora `mesa-libOpenCL`) — Kandidat für die Prüfung beim Start.
- **Mesa-OpenCL reicht nicht** (nachgeprüft nach `apt install mesa-opencl-icd`, 18.10.2026): Clover bietet OpenCL 1.1,
  Rusticl (nur mit `RUSTICL_ENABLE=radeonsi`) OpenCL 3.0 mit OpenCL C 1.2 — ILGPU braucht OpenCL 2.0 und verwirft die
  Karte („Keine Grafikkarte mit OpenCL oder CUDA gefunden“). Für AMD stattdessen ROCm-OpenCL: Debian
  `rocm-opencl-icd` (aus Debian selbst, 5.7), Fedora `rocm-opencl`.
- **Mehrere Grafikkarten** (eigene Karte und die im Prozessor): `GpuFilters.Open` nimmt seit 0.5.102 die mit den meisten
  Recheneinheiten (vorher die erste gefundene). Nicht nach Speicher: mit ROCm-OpenCL meldet die eingebaute Grafik des
  Prozessors (gfx1036) 31 GB geteilten Arbeitsspeicher, die eigene Karte (gfx1032) 8 GB — 0.5.101 wählte deshalb die schwache.
  Welche Karte gewählt wurde und welche es gab, steht im Log neben dem Programm.
- **Falle beim Messen mit eigenen Testprogrammen:** In einer dateibasierten App (`dotnet run Datei.cs`) wirft
  `ILGPU.Context` einen Fehler im Typinitialisierer, weil dort `PublishAot` voreingestellt ist — `#:property
  PublishAot=false` setzen. Das Programm selbst ist nicht betroffen.

## Rechner bedienbar halten, Upscaler im Test (0.5.105 beta)

Wunsch des Benutzers (18.10.2026): Bei stundenlangem Rechnen auf 100 % ist der Rechner nicht mehr benutzbar; die
Zeitschätzung soll schon vor dem ersten Lauf stimmen.

- **Niedrige Priorität** (Einstellungen › Leistung, Vorgabe an; `ComputeLimits.LowerPriority`): andere Programme haben
  Vorrang, ist der Rechner frei, wird trotzdem voll gerechnet. Linux: `setpriority` auf nice 10 für jeden Strang unter
  `/proc/self/task` — „nice“ gilt je Strang, neue erben ihn (nachgeprüft: nach `Parallel.For` 41 Stränge, alle nice 10;
  die Stränge der KI-Laufzeit entstehen ebenso). Zurück auf normal geht unter Linux nur mit Administratorrechten — das
  Ausschalten wirkt dort nach einem Neustart (Hinweis in den Einstellungen). Windows: `BelowNormal` für den Prozess.
- **Obergrenze „CPU höchstens … %“** (10–100 %, Vorgabe 100): `ComputeLimits.Options` in allen 107 parallelen Schleifen
  (`MaxDegreeOfParallelism`), Stränge der KI-Laufzeit (`IntraOpNumThreads`, nach echten Kernen — Linux aus
  `/sys/devices/system/cpu/*/topology`, Windows `GetLogicalProcessorInformation`) und ImageMagick
  (`ResourceLimits.Thread`). Die Kennung des Rechners für Test und Zeitschätzung enthält die Grenze — nach einer Änderung
  gelten alte Messungen nicht.
- **Upscaler im Geschwindigkeitstest:** nach Bildfiltern und KI je installiertem Upscaler ein Testbild 256 × 256 (vorher
  64 × 64 zum Laden), mit der gewählten Recheneinheit und Grenze; PiSA-SR nur, wenn seine ONNX-Dateien schon gebaut sind.
  Auf dem Debian-Rechner 86 s für den ganzen Test.
- **Schätzung nach Kachelaufwand statt Fläche** (`Upscalers.Work`): Kachelmodelle nach Zahl der Kacheln, PiSA-SR nach
  Kacheln × Fläche × Kosten je Teil (`PiSaSrRun.Work`). Kleine Bilder überlappen anteilig mehr; mit Sekunden je Megapixel
  lag ein Test an 256 × 256 für große Bilder deutlich daneben. Gespeichert in `settings.json` als `UpscaleWork`
  (Sekunden je Aufwandseinheit; der Eintrag `UpscaleTimes` aus 0.5.104 in Sekunden je Megapixel wird nicht mehr gelesen).

### CPU-Feinschliff für PiSA-SR — gemessen, verworfen

Gemessen auf dem Debian-Rechner (16 Kerne, zwei Gruppen zu 8: eine mit zusätzlichem Cache, eine höher getaktet — keine
Effizienzkerne), PiSA-SR fp32 auf der CPU:

| Versuch | Zeit | Ergebnis |
|---|---|---|
| Decoder-Kachel 64 (Vorgabe), 600 × 360 | 101,7 s | Referenz |
| Decoder-Kachel 96 | 89,9 s | PSNR 36 dB, einzelne Stellen bis 107/255 anders |
| Decoder-Kachel 128 (auch Encoder 128/160) | 106–109 s | PSNR 33–34,5 dB |
| 8 statt 16 Stränge | 134 s | gleich |
| 32 Stränge | 108 s | gleich |
| zwei Kacheln gleichzeitig, eine Sitzung (400 × 240, zwei Bilder) | 90,8 s statt 98,5 s | gleich |
| zwei Kacheln gleichzeitig, zwei Sitzungen je 8 Stränge | 85,6 s (Messung gestört, eher ~90) | gleich, doppelter Speicher |

- **Kachelgröße bleibt fest:** Der VAE-Decoder normiert innerhalb jeder Kachel (GroupNorm) — das Ergebnis hängt
  sichtbar von der Kachelgröße ab, und schneller wird es kaum. Bedingung des Benutzers: Qualität darf nicht schlechter
  werden. **Nicht tun:** Kachelgrößen je Gerät „optimieren“, ohne das Ergebnis zu vergleichen.
- **Mehr Stränge bringen nichts** (16 → 32), weniger kosten wenig (8 Stränge: +26 %) — Grundlage der Obergrenze.
- **Zwei Kacheln gleichzeitig:** nur 8 % — lohnt den Umbau der Reihenfolge nicht; unter DirectML sind gleichzeitige
  Aufrufe einer Sitzung ohnehin nicht erlaubt.

## WebGPU unter Linux (gebaut und gemessen, 18.10.2026)

Messplatz: Debian 13, Desktop-Prozessor mit 16 Kernen, eigene AMD-Grafikkarte (RDNA2, 8 GB, Mesa/RADV), Stand 0.5.96 beta.
Panorama-Ausschnitt 500 × 300 → 2000 × 1200 (Kachelmodelle zusätzlich 1800 × 800 → 7200 × 3200), Zeit ohne Laden.

| Modell | CPU | WebGPU | Bemerkung |
|---|---|---|---|
| PiSA-SR fp32 | **58 s** | 69 s | je Teil allein auf der Karte 55–65 s — kein Teil profitiert |
| PiSA-SR fp16 | — | 57,5 s, mit `validationMode=disabled` **52 s** | Grafikspeicher dauernd voll (8,4 GB), Karte 90 % ausgelastet |
| PiSA-SR fp16, `preferredLayout=NCHW` | — | 152 s | NHWC (Vorgabe) ist viel schneller |
| PiSA-SR fp16, `storageBufferCacheMode=disabled` | — | 76 s | lagert bis 11,8 GB in den Arbeitsspeicher aus |
| x4v3 („KI schnell“), 7200 × 3200 | **13 s** | 29 s | |
| UltraSharpV2 („KI mittel“) | **56 s** | Abbruch | `Conv`: Ausgabe ließ sich nicht anlegen (Grafikspeicher) |

**Ergebnis:** Auf diesem Rechner ist die CPU so schnell wie oder schneller als WebGPU; die Laufzeit mit WebGPU wird
deshalb **nicht** ausgeliefert, die App rechnet unter Linux weiter auf der CPU. Der Laptop (8 Kerne, 355 s auf der
CPU) profitiert unter Windows weiter von DirectML. Die WebGPU-Anbindung von ONNX Runtime ist für Faltungen und die
Aufmerksamkeit großer Kacheln (noch) nicht schnell genug; schneller wäre nur MIGraphX (Weg A, braucht ROCm).

**Was beim Bauen auffiel** (falls es später neu versucht wird, z. B. auf einem Rechner mit schwächerer CPU):

- Bau: `python3 tools/ci_build/build.py --build_dir ../build --config Release --build_shared_lib --parallel
  --use_webgpu --skip_tests --compile_no_warning_as_error --cmake_generator Ninja --cmake_extra_defines
  onnxruntime_BUILD_UNIT_TESTS=OFF` aus den Quellen von `v1.24.4`; rund 20 Minuten auf 32 Threads. Die WebGPU-Anbindung
  steckt dann in der einen `libonnxruntime.so` (40 MB, Dawn statisch, braucht nur libstdc++/libc; Vulkan wird zur
  Laufzeit geladen). Telemetrie ist in eigenen Bauten aus (`onnxruntime_USE_TELEMETRY` ist nur in den offiziellen an).
- **Node.js ab 20 nötig** (erzeugt die WGSL-Vorlagen über `npm ci`) — ohne bricht CMake mit „Could not find
  NODE_EXECUTABLE“ ab. Das offizielle Archiv von nodejs.org in den Bauordner entpacken reicht, keine Installation.
- **Baupfade landen in der Bibliothek** (`__FILE__` in Fehlermeldungen, mit Benutzernamen). Vor einer Auslieferung mit
  `-ffile-prefix-map=<bauordner>=/ort` bauen (`CMAKE_C_FLAGS`/`CMAKE_CXX_FLAGS`).
- **Absturz beim Schließen** (Speicherzugriffsfehler, Exitcode 139), sobald zwei Sitzungen gleichzeitig auf der Karte
  liegen (PiSA-SR: Encoder, UNet, Decoder): Die Rechnung läuft durch, das Schließen der Sitzungen gibt das Gerät doppelt
  frei. Abhilfe: Anbieter-Option `preserveDevice=1` (in `OnnxSessions.GpuOptions` mitgeben, falls WebGPU je genutzt
  wird). Zuerst sah es nach vollem Grafikspeicher aus — war es nicht (eine Sitzung allein belegt ebenso 8,4 GB und läuft).
- Anbieter-Optionen werden ohne Präfix übergeben (`AppendExecutionProvider("WebGPU", { ["preserveDevice"] = "1" })`);
  Liste in `onnxruntime/core/providers/webgpu/webgpu_provider_options.h`.
- Es rechnet die eigene Karte (`gpu_busy_percent` der Karte 90 %, die im Prozessor eingebaute 0 %).

Quellen: ONNX Runtime — Execution Providers (DirectML, ROCm „removed since 1.23“, MIGraphX, CUDA), Install-Seite
(DirectML „sustained engineering“), Microsoft Learn „What is Windows ML?“; NuGet-Abfrage vom 14.10.2026
(`Microsoft.ML.OnnxRuntime.DirectML` höchstens 1.24.4, kein MIGraphX- oder WebGPU-Paket).
