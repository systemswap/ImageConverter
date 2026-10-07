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
  Linux. **Behoben:** 0.4.18 (Commit `62cd553`).
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
  Bibliotheken nach `Tools/`. **Behoben:** 0.4.18 (Commit `62cd553`), bevor es
  ausgeliefert wurde.
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

## 3. Telemetrie: Avalonia meldet jeden Build (Avalonia.BuildServices)

- **Entdeckt:** 0.4.18 (Commit `62cd553`, 07.10.2026). **Behoben:** 0.4.19
  (Commit `21a8cd2`).
- **Was:** Das Paket `Avalonia.BuildServices` (kommt automatisch mit Avalonia)
  führt vor jedem Kompilieren die `AvaloniaStatsTask` aus und meldet Projekt,
  Avalonia-Version, Zielsystem und System an den Server von Avalonia.
- **Situation:** jeder Build in Rider, Visual Studio oder mit `dotnet` – auch
  auf dem Windows-Rechner, auf dem bis 0.4.17 entwickelt wurde. Dort war
  `AVALONIA_TELEMETRY_OPTOUT` vermutlich nicht gesetzt, die Builds bis 0.4.17
  wurden also wahrscheinlich gemeldet (nicht mehr nachprüfbar).
- **Warum:** Voreinstellung des Herstellers; abschaltbar nur über die
  Umgebungsvariable `AVALONIA_TELEMETRY_OPTOUT=1`, die auf einem neuen Rechner
  oder in der IDE leicht fehlt.
- **Wie behoben:** `Directory.Build.targets` definiert ein leeres Target
  `AvaloniaStats`. Diese Datei lädt MSBuild nach den Paket-Targets, die letzte
  Definition gilt – die Meldung entfällt auf jedem Rechner, unabhängig von der
  Umgebung. Die Build-Skripte setzen die Variable zusätzlich.
  Verworfen: nur die Variable (hängt am Rechner); das Paket mit
  `ExcludeAssets` ausschließen (bei einer neuen Avalonia-Fassung mit anderem
  Paketnamen wirkungslos, das Target heißt seit Jahren gleich).
- **Nicht tun:** `Directory.Build.targets` löschen oder umbenennen. Nach einem
  Avalonia-Update prüfen, ob das Target noch `AvaloniaStats` heißt
  (`~/.nuget/packages/avalonia.buildservices/<version>/buildTransitive/*.targets`).
- **Wo:** `Directory.Build.targets`.
- **Nachweis:** Build ohne Netz (`unshare -rn`) und ohne die Variable mit
  `-v:d`: MSBuild meldet „Das Ziel "AvaloniaStats" … wird durch das Ziel …
  aus Directory.Build.targets außer Kraft gesetzt“, die `AvaloniaStatsTask`
  läuft nicht. Gegenprobe ohne die Datei (mit gesetzter Variable): die Aufgabe
  läuft.

## 4. ImageMagick: Netzzugriff und externe Programme über einen Pfad wie „https://…“

- **Entdeckt:** 0.4.20 (Commit `029962b`, 07.10.2026), bei der Prüfung auf
  Netzzugriffe. **Behoben:** 0.4.21.
- **Was:** Bekommt ImageMagick einen Pfad wie `https://…` (oder `http:`,
  `ftp:`, `url:`), stellt es eine DNS-Anfrage und ruft das externe Programm
  `curl` auf, um die Datei zu holen. Die Standardrichtlinie von Magick.NET
  sperrt nur MVG, MSL, EPHEMERAL und `@`/`|`-Pfade, nicht das Netz.
- **Situation:** Dateinamen kommen aus Dateidialog, „Öffnen mit …“, Drag &
  Drop und der Kommandozeile – ein präparierter Aufruf hätte eine Verbindung
  oder ein fremdes Programm auslösen können. Ein SVG mit eingebettetem
  fremdem Bild lädt dagegen nichts nach (librsvg holt keine externen Adressen
  – geprüft).
- **Warum:** ImageMagick behandelt solche Pfade als „Coder“ und holt sie über
  Delegates (externe Programme).
- **Wie behoben:** `ImageMagickPolicy.Apply()` ergänzt die Standardrichtlinie
  um die Coder `HTTP, HTTPS, FTP, URL` und sperrt alle Delegates (externe
  Programme – ImageConverter braucht keine, alle Formate sind eingebaut).
  Die Konfiguration schreibt Magick.NET nach
  `~/.local/share/ImageConverter/ImageMagick` (Windows `%LOCALAPPDATA%`).
- **Nicht tun:** Die Richtlinie erst nach dem ersten Zugriff auf ImageMagick
  setzen – dann greift sie nur teilweise (im Test: Delegate-Regel ja,
  Coder-Regel nein). Deshalb in `NativeLibraries.Configure()` ganz am Anfang
  von `Main` und in den Tests über einen Modul-Initialisierer (`TestSetup`).
- **Wo:** `ImageConverter.Core/Platform/ImageMagickPolicy.cs`,
  `NativeLibraries.Configure`, `ImageConverter.Tests/TestSetup.cs`.
- **Test:** `PlatformTests.ImageMagick_RefusesNetworkAddresses` (Gegenprobe
  ohne Richtlinie: `MagickDelegateErrorException`, also curl-Versuch),
  `ImageMagick_PolicyBlocksExternalPrograms`. Von Hand ohne Netz
  (`unshare -rn`) mit protokollierten `getaddrinfo`/`connect`-Aufrufen: ohne
  Richtlinie DNS-Anfrage, mit Richtlinie keine.

## 5. „Ordner öffnen“ stürzte ohne xdg-open ab

- **Entdeckt:** 0.4.17 (Commit `123b17f`, 07.10.2026), bei der Durchsicht für
  Linux (nicht beim Benutzer aufgetreten). **Behoben:** 0.4.22.
- **Was:** Die Knöpfe „Ordner öffnen“ (nach dem Export) und „Modellordner
  öffnen“ (KI) riefen `xdg-open` direkt mit `Process.Start` auf, ohne
  Fehlerbehandlung. Fehlt `xdg-open` (schlankes Linux, manche Container),
  wirft das eine `Win32Exception` im Klick-Handler – das Programm stürzt ab.
- **Wie behoben:** `FolderOpener.TryOpen` probiert `xdg-open`, dann
  `gio open` (Windows: Explorer, macOS: `open`) und liefert `false`, statt zu
  werfen; die Oberfläche zeigt dann „Kein Dateimanager gefunden“ mit dem Pfad.
- **Nicht tun:** wieder `Process.Start` für den Dateimanager ohne
  Fehlerbehandlung direkt im Klick-Handler.
- **Wo:** `ImageConverter.Ui/Services/FolderOpener.cs`, Aufrufe in
  `Views/MainView.axaml.cs` (`OnOpenExportFolderClick`) und
  `Ai/AiViewModel.cs` (`OpenFolder`).
- **Test:** `SystemToolsTests.OpenFolder_WithoutFileManager_ReportsInsteadOfCrashing`
  (leerer `PATH`; zeigt im selben Test, dass der alte direkte Aufruf wirft).

## 6. Druckdialog: konnte bei hängendem Druckdienst einfrieren

- **Entdeckt:** 0.4.17 (Commit `123b17f`, 07.10.2026), bei der Durchsicht für
  Linux (nicht beim Benutzer aufgetreten). **Behoben:** 0.4.22.
- **Was:** Der Druckdialog holte die Druckerliste (`lpstat`, unter Windows
  der Druckdienst) synchron im Konstruktor – die Oberfläche stand, bis die
  Antwort kam. Zusätzlich las `SystemPrinters.RunWithExit` die Ausgabe mit
  `ReadToEnd()` *vor* `WaitForExit(15000)`: Hängt CUPS, wartete es
  unbegrenzt, die 15 Sekunden griffen nie.
- **Wie behoben:** Druckerliste im Hintergrund (`LoadPrintersAsync`); bis dahin
  steht „Als PDF speichern“ da, der Standarddrucker wird danach nur
  vorgewählt, wenn der Benutzer noch nichts anderes gewählt hat. Ausgabe
  parallel lesen, begrenzt warten, bei Zeitüberschreitung den Prozess beenden
  und `TimeoutException` werfen (die Aufrufer fangen sie ab).
- **Nicht tun:** `ReadToEnd()` vor dem begrenzten Warten; Systemabfragen
  synchron im Konstruktor eines Dialogs.
- **Wo:** `ImageConverter.Ui/ViewModels/PrintDialogViewModel.cs`
  (`LoadPrintersAsync`), `ImageConverter.Core/Printing/SystemPrinters.cs`
  (`RunWithExit`).
- **Test:** `SystemToolsTests.PrintService_Hanging_TimesOutAndIsStopped`
  (`sleep 30`, Grenze 300 ms; Gegenprobe mit der alten Reihenfolge: hängt
  30 s, schlägt fehl).

## 7. publish.ps1 veröffentlichte unter Windows nichts: zwei Tests nur für Linux

- **Entdeckt:** 0.4.22 (Commit `8259513`, 07.10.2026), beim ersten Bau unter
  Windows nach der Weiterentwicklung unter Linux. **Behoben:** 0.4.23.
- **Was:** `published\` blieb leer. `publish.ps1` führt vorher alle Tests aus
  und bricht bei einem Fehler ab („Tests fehlgeschlagen - nichts
  veroeffentlicht“); zwei Tests schlugen unter Windows fehl.
- **Situation:** Tests, die unter Linux geschrieben und nur dort ausgeführt
  wurden.
- **Warum:**
  `ProgramFolder_AppImage_IsFolderOfTheAppImageFile` – `Path.GetDirectoryName`
  macht unter Windows aus `/home/user/Programme` den Pfad
  `\home\user\Programme`. `OpenFolder_WithoutFileManager_ReportsInsteadOfCrashing`
  – ein leerer `PATH` hilft unter Windows nicht: `CreateProcess` sucht immer
  auch im Systemordner und findet `explorer.exe`. `TryOpen` liefert dort
  `true` und öffnet bei jedem Testlauf ein Explorer-Fenster.
- **Wie behoben:** Beide Tests kehren unter Windows sofort zurück, wie
  `PrintService_Hanging_TimesOutAndIsStopped`. Eine AppImage gibt es unter
  Windows nicht, und `explorer.exe` fehlt dort nie – die Fälle treten nicht auf.
- **Nicht tun:** Tests, die Unix-Pfade erwarten oder Programme über `PATH`
  verstecken, ohne Weiche für Windows schreiben. Nach Arbeit unter Linux vor
  dem Windows-Bau `dotnet test` unter Windows laufen lassen.
- **Wo:** `ImageConverter.Tests/PlatformTests.cs`,
  `ImageConverter.Tests/SystemToolsTests.cs`.
- **Nachweis:** `publish.ps1` unter Windows: vorher 2 von 169 Tests
  fehlgeschlagen, nichts veröffentlicht; danach 169 bestanden,
  `published\windows-x64\ImageConverter.exe` startet (Titel
  „ImageConverter 0.4.22“).
