# Fehlergedächtnis

Behobene Fehler und Fallen, zeitlich nach der Behebung geordnet, neue Einträge
ans Ende. Vor Änderungen an einer der genannten Stellen hier nachlesen.
Einträge bleiben stehen, auch wenn der Code inzwischen anders aussieht.

Jeder Eintrag: entdeckt / behoben (Version, Commit), was, in welcher
Situation, warum, wie und warum so, was man nicht tun sollte, wo im Code,
Test oder Nachweis.

---

## 1. Telemetrie: ONNX Runtime sendet Nutzungsdaten an Microsoft

- **Entdeckt:** 0.4.17 (Commit `123b17f`, 07.10.2026), bei der Durchsicht für
  Linux. **Behoben:** 0.4.18.
- **Was:** Die offiziellen Builds der ONNX Runtime (NuGet
  `Microsoft.ML.OnnxRuntime`, hier 1.30.0) haben Telemetrie standardmäßig an.
  Unter Linux und macOS steckt ein Uploader (1DS) an
  `mobile.events.data.microsoft.com` in `libonnxruntime.so`; beim ersten
  Zugriff legt er `~/.cache/Microsoft/DeveloperTools/.onnxruntime/deviceid`
  (dauerhafte Geräte-Kennung) und `onnxruntime.db` (Zwischenspeicher für
  Ereignisse) an. Unter Windows laufen die Ereignisse über ETW.
- **Situation:** sobald die KI-Funktionen die Laufzeit starten (`OrtEnv`,
  `InferenceSession`). Unter Windows bis 0.4.17 unbemerkt; der Hinweis steht
  nur in `Privacy.md` des NuGet-Pakets.
- **Warum:** Voreinstellung des Herstellers; abschaltbar nur zur Laufzeit über
  die Umgebungsvariable `ORT_DISABLE_TELEMETRY=1` (muss *vor* dem Start der
  Laufzeit gesetzt sein) oder über die API (`DisableTelemetryEvents`, wirkt
  erst nach einem ersten Ereignis).
- **Wie behoben:** dreifach –
  `NativeLibraries.Configure()` ganz am Anfang von `Main` setzt die Variable
  per `setenv` in der Umgebung des Prozesses; `OnnxSessions.Get` setzt sie
  vor der ersten Sitzung noch einmal und ruft `DisableTelemetryEvents`; das
  `AppRun` der AppImage exportiert sie schon vor dem Programmstart.
- **Nicht tun:** Nur `Environment.SetEnvironmentVariable` benutzen – das ändert
  unter Linux nur die Kopie von .NET, die native Bibliothek liest mit `getenv`
  die echte Umgebung und sieht den Schalter nicht. Die ONNX Runtime nicht
  aktualisieren, ohne `Privacy.md` des neuen Pakets zu lesen und den Nachweis
  unten zu wiederholen.
- **Wo:** `ImageConverter.Core/Platform/NativeLibraries.cs`
  (`DisableOnnxTelemetry`), `ImageConverter.Core/Ai/OnnxModels.cs`
  (`EnsureNoTelemetry`), `ImageConverter.Desktop/Program.cs`, `AppRun` in
  `publish.sh`.
- **Test/Nachweis:** `PlatformTests.OnnxTelemetry_SwitchReachesTheNativeEnvironment`
  (schlägt ohne `setenv` fehl – Gegenprobe gemacht). Von Hand: kleines
  Testprogramm ohne Netz (`unshare -rn`) mit eigenem `HOME` – ohne Abschaltung
  entstehen `deviceid` und `onnxruntime.db`, mit Abschaltung keine Datei.
- **Andere Projekte:** Wer die ONNX Runtime ebenso einbindet, ist genauso
  betroffen (z. B. OCR mit ONNX-Modellen).

## 2. Fremde native Bibliotheken neben dem Programm: SetNativeLibraryDirectory wirkt nicht

- **Entdeckt:** 0.4.18 in Arbeit (07.10.2026), beim Auslagern der nativen
  Bibliotheken nach `Tools/`. **Behoben:** 0.4.18, bevor es ausgeliefert wurde.
- **Was:** Das Programm brach beim ersten Zugriff auf ImageMagick mit
  `DllNotFoundException: Magick.Native-Q16-x64.dll` ab, obwohl die Bibliothek
  in `Tools/ImageMagick/` lag.
- **Situation:** Veröffentlichung mit ImageMagick und ONNX Runtime außerhalb
  der Programmdatei (Wunsch: in EXE/AppImage nur Programm, .NET und Avalonia;
  fremde Bibliotheken je in eigenem Ordner daneben).
- **Warum:** `MagickNET.SetNativeLibraryDirectory` ist für das alte .NET
  Framework gedacht; unter .NET 10 sucht der `DllImport` weiter nur im
  Programmordner und im Laufzeitordner.
- **Wie behoben:** eigener `NativeLibrary.SetDllImportResolver` für die
  Magick.NET-Assembly und für die ONNX-Runtime-Assembly (deren eingebauten
  Resolver vorher mit `OrtEnv.DisableDllImportResolver = true` abschalten,
  sonst meldet sie einen Konflikt). Fehlt der Ordner `Tools/…` (Start aus der
  IDE, Tests), liefert der Resolver nichts und es gilt die normale Suche.
  Das Build-Target `ForeignNativeLibrariesBesideProgram` in
  `ImageConverter.Desktop.csproj` legt die Dateien mit
  `ExcludeFromSingleFile` nach `Tools/ImageMagick` und `Tools/OnnxRuntime`.
- **Nicht tun:** Sich auf `SetNativeLibraryDirectory` verlassen;
  `LD_LIBRARY_PATH` setzen (erbt jedes Programm, das ImageConverter startet,
  z. B. Dateimanager über `xdg-open`, und gilt unter Windows nicht).
  Programmordner bei einer AppImage nicht über `AppContext.BaseDirectory`
  bestimmen – das ist das schreibgeschützte Innere unter `/tmp/.mount_…`.
- **Wo:** `ImageConverter.Core/Platform/NativeLibraries.cs`,
  `ImageConverter.Core/Platform/AppLocations.cs`,
  `ImageConverter.Desktop/ImageConverter.Desktop.csproj`.
- **Test/Nachweis:** `PlatformTests` (Programmordner); von Hand: Testprogramm
  mit den Bibliotheken nur in `Tools/` – `/proc/self/maps` zeigt beide aus
  `Tools/` geladen.
