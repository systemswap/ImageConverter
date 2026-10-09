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

## 8. Bildwechsel langsam: ImageMagick rechnet nur auf einem Kern

- **Entdeckt:** 0.4.24 (Commit `bb10203`, 08.10.2026), vom Benutzer beim
  Blättern zwischen Bildern bemerkt. **Behoben:** 0.4.25.
- **Was:** Das Blättern dauerte bei großen Fotos mehrere Sekunden. Gemessen
  an einem 6000 × 4000-JPEG (Release, 16 Kerne): Laden 0,4 s, Anzeige 0,1 s,
  **Verkleinern auf 2048 px gamma-korrekt 2,8 s** (Umrechnung nach linear
  1,6 s, Lanczos 1 s). Das Verkleinern läuft bei jedem Wechsel für die
  Größenschätzung und, sobald Größe/Schärfen/Rauschen eingestellt sind, für
  die Vorschau. Solange zeigte die Vorschau noch das *vorige* Bild.
- **Warum:** `Magick.NET-Q16-AnyCPU` ist ohne OpenMP gebaut
  (`ResourceLimits.Thread` = 1, `MagickNET.Features` ohne OpenMP) – jede
  Operation läuft auf einem Kern. Die Vorschau zeigte `AdjustedBitmap ??
  FullBitmap`, und `AdjustedBitmap` gehörte bis zur neuen Berechnung noch zum
  vorigen Bild.
- **Wie behoben:** `ImageOps.Resize` zerlegt Bilder ab 1 Megapixel (sRGB) in
  Zeilenstreifen (waagerecht skalieren) und dann Spaltenstreifen (senkrecht)
  und rechnet sie parallel; sRGB ↔ linear über Tabellen mit den Formeln von
  ImageMagick. 2,9 s → 0,75 s, bei normalem Verkleinern pixelgleich.
  `SetDocumentAsync` verwirft die alte Vorschau bei neuem Bild oder neuer
  Größe; die Render-Schleife verwirft Ergebnisse für ein inzwischen
  gewechseltes Bild.
- **Nicht tun:** Die Streifen über ImageMagick zerschneiden und zusammensetzen
  (`CloneArea`, `AppendVertically`) – das kostete selbst rund 1 s auf einem
  Kern; deshalb Pixel-Arrays (`ToShortArray`/`ReadPixels` mit
  `StorageType.Quantum`, `Short` wirft). Pixel der gemeinsamen Quelle nur
  unter `lock` lesen. Nicht annehmen, dass ImageMagick „sowieso parallel“
  rechnet – vor teuren Operationen messen.
- **Wo:** `ImageConverter.Core/Imaging/ImageOps.cs` (`ResizeInStrips`),
  `ImageConverter.Ui/ViewModels/MainViewModel.Preview.cs`.
- **Test:** `ImageOpsTests.Resize_LargeImage_InStrips_MatchesImageMagick`
  (Vergleich mit dem einfachen Weg, mit Alpha, Adobe-RGB-Profil, EXIF, 8 Bit),
  `ConcurrentOperations_OnSameSource_AreSafe` jetzt über der Schwelle.
  Messung mit einem Testprogramm unter `%LOCALAPPDATA%\imageconverter-build\bench`.

## 9. Tiefen hellte Gesichter mit auf

- **Entdeckt:** 0.4.27 (Commit `06baede`, 08.10.2026), vom Benutzer an einem
  Familienfoto (Innenraum, dunkler Flur hinten). **Behoben:** 0.4.28.
- **Was:** „Tiefen“ sollte nur den dunklen Flur aufhellen, hellte aber auch
  Gesichter, Haare und Augenpartie merklich auf.
- **Warum:** Zwei Gründe. (1) Die Wirkung reichte bis Umgebungshelligkeit 0,6 –
  gemessen lagen Flur 0,08, Türrahmen 0,15, Haare/Augenpartie 0,36–0,40,
  Wangen 0,6; bei +80 bekam die Augenpartie rund ein Drittel mehr Helligkeit.
  (2) Der kantenerhaltende Filter (Guided Filter, eingeführt gegen Lichthöfe)
  folgt auch kleinen dunklen Stellen – Pupillen, Nasenlöcher, Haarschatten
  galten als „dunkle Umgebung“.
- **Wie behoben:** Tiefen wirkt voll im Dunklen und endet bei 0,35, Lichter
  beginnt erst bei 0,65. Zusätzlich zählt eine Stelle nur als dunkel, wenn auch
  eine weite Umgebung (Gauß, 3 % der Kante) dunkel ist – für Tiefen gilt der
  hellere, für Lichter der dunklere der beiden Werte. Am Foto: Flur 0,08 → 0,21,
  Türrahmen 0,15 → 0,31, Gesichter unverändert.
- **Nicht tun:** Den Bereich wieder bis in die Mitteltöne ziehen oder nur den
  Guided Filter als Umgebung nehmen. Wirkung an echten Fotos mit Personen
  prüfen, nicht nur an Verläufen.
- **Wo:** `ImageConverter.Core/Imaging/Adjustments.cs` (`ShadowsHighlights`,
  `Apply`).
- **Test:** `AdjustTests.Shadows_LeaveMidtonesLikeFaces`,
  `Shadows_LeaveSmallDarkSpotsInBrightAreas`, `Shadows_NoHaloAtHardEdges`.

## 10. Umlaute zerstört durch PowerShell 5.1 (Get-Content/Set-Content)

- **Entdeckt:** 0.4.27 (08.10.2026), beim Lesen von `Adjustments.cs`.
  **Behoben:** 0.4.28. **Eingeführt:** 0.4.26 (Commit `2aae62a`).
- **Was:** In Kommentaren stand „FlÃ¤che“ statt „Fläche“, „â€”“ statt „—“.
- **Warum:** Eine Ersetzung mit `(Get-Content -Raw) -replace … | Set-Content
  -Encoding utf8` unter Windows PowerShell 5.1: `Get-Content` liest UTF-8 ohne
  BOM als ANSI (Windows-1252), `Set-Content` schreibt das als UTF-8 (mit BOM)
  zurück – jedes Nicht-ASCII-Zeichen wird doppelt kodiert.
- **Wie behoben:** Rückwandlung: Text als UTF-8 lesen, als Windows-1252 in
  Bytes, diese als UTF-8 lesen; ohne BOM geschrieben. Nur diese Datei betroffen
  (`git grep "Ã¤"`).
- **Nicht tun:** Quelltext mit `Get-Content`/`Set-Content` unter PowerShell 5.1
  bearbeiten. Stattdessen Editor-Werkzeuge, `sed`/`perl` in Git Bash oder
  `[IO.File]::ReadAllText/WriteAllText` mit ausdrücklichem UTF-8 ohne BOM.
- **Prüfen:** `git grep -n "Ã\|â€" -- ":!docs"` muss leer sein (hier im Eintrag
  stehen die Zeichen absichtlich).

## 11. Tiefen-Kurve kippte: dunklere Töne wurden heller als hellere

- **Entdeckt:** 0.4.28 (Commit `b205056`, 08.10.2026), beim Umstieg auf den
  Local Laplacian Filter (Test `Shadows_LeaveMidtonesLikeFaces` wurde dunkler).
  **Behoben:** 0.4.29. **Eingeführt:** 0.4.26.
- **Was:** Die Tiefen-Kurve `x · 2^(Stufen · Regler · Gewicht(x))` war bei starken
  Werten nicht monoton: Bei +100 wurde 0,25 zu 0,34, aber 0,30 nur zu 0,33.
  Tonwerte konnten sich umdrehen (Streifen, flaue Stellen); der Local Laplacian
  Filter schnitt die negative Steigung ab und dunkelte Mitteltöne leicht ab.
- **Warum:** Das Gewicht fällt zur Reichweite hin schneller, als die Belichtung
  steigt – die Ableitung wird negativ.
- **Wie behoben:** Aufhellen über `x + Regler · 2,5 · x · (1 − x/0,4)²`; die
  Steigung ist mindestens `1 − 2,5/3 > 0`. Abdunkeln bleibt Belichtung (dort
  monoton). Lichter gespiegelt. Wirkung etwas sanfter: 40 → ~70 bei +80.
- **Nicht tun:** Tonkurven als „Gewicht × Gain“ bauen, ohne die Monotonie zu
  prüfen.
- **Wo:** `ImageConverter.Core/Imaging/Adjustments.cs` (`ShadowCurve`,
  `HighlightCurve`, `Raise`).
- **Test:** `AdjustTests.ShadowAndHighlightCurves_AreMonotonic` (±50, ±100).

## 12. Alle Regler ohne sichtbare Spur (Stil-Ressourcen gelten global)

- **Entdeckt:** 0.4.31 (08.10.2026), auf einem Bildschirmfoto: beim Regler
  „Qualität“ war nur der Knopf zu sehen. **Behoben:** 0.4.32.
  **Eingeführt:** 0.4.28 (Commit `b205056`).
- **Was:** Für den Farbton-Regler (Regenbogen dahinter) wurden die
  Spur-Farben `SliderTrackFill` … in `<Style Selector="Slider.hue">
  <Style.Resources>` durchsichtig gesetzt – dadurch waren die Spuren *aller*
  Regler der Anwendung unsichtbar.
- **Warum:** In Avalonia sind Ressourcen in `Style.Resources` nicht an den
  Selektor gebunden; sie landen in der Ressourcensuche des Stil-Besitzers
  (hier die Anwendung) und gelten für alle Steuerelemente.
- **Wie behoben:** Die Ressourcen direkt am Regler (`<Slider.Resources>`) –
  dort gelten sie nur für ihn.
- **Nicht tun:** Theme-Ressourcen per `Style.Resources` „für eine Klasse“
  überschreiben. Nach Stiländerungen ein Bildschirmfoto der ganzen Oberfläche
  ansehen, nicht nur der geänderten Stelle.
- **Wo:** `ImageConverter.Ui/Views/MainView.axaml` (Farbton-Regler),
  `ImageConverter.Ui/Styles/Controls.axaml`.

## 13. publish.ps1: halb geleerter published-Ordner, ZIP scheitert an gesperrter EXE

- **Entdeckt:** 0.4.33 (08.10.2026), beim Veröffentlichen unter Windows.
  **Behoben:** 0.4.34.
- **Was:** Zwei Fehlerbilder. (1) Der Bau lief, während ImageConverter aus
  `published\windows-x64` gestartet war: `Clear-ProgramFolder` löschte
  `licenses\` und brach dann an der geladenen `Magick.Native-Q16-x64.dll` ab –
  Ordner halb leer, ZIP weg. (2) Auch ohne laufendes Programm schlug
  `Compress-Archive` fehl: Die eben kopierte `ImageConverter.exe` war „von einem
  anderen Prozess verwendet“.
- **Warum:** (1) `publish.ps1` prüfte – anders als `publish.sh` – nicht, ob das
  Programm aus `published\` läuft; die Prüfung von außen vor dem Aufruf reicht
  nicht, weil der Benutzer es während der Tests starten kann. (2) Ein anderer
  Prozess hält neue Programmdateien kurz fest, vermutlich der Virenscanner.
- **Wie behoben:** `Assert-NotRunning` vor dem Bau und noch einmal unmittelbar
  vor `Clear-ProgramFolder` (bricht mit Meldung ab, beendet nichts). Das Packen
  versucht es bis zu sechsmal im Abstand von 5 s.
- **Nicht tun:** Das laufende Programm beenden, um bauen zu können. Den
  Wiederholungsversuch als Erklärung für (1) nehmen – eine geladene DLL wird
  nicht nach 5 s frei.
- **Wo:** `publish.ps1`.
- **Nachweis:** Bau nach dem Fix: „ZIP: Datei noch gesperrt, neuer Versuch …
  (1/5)“, danach fertig; `licenses\` und ZIP wieder da, `KI-Modelle\` unberührt.

## 14. Dunst entfernen: Bild dunkel, Himmel türkis/violett, heller Streifen am Horizont

- **Entdeckt:** 0.4.34 (08.10.2026), vom Benutzer an zwei Panoramen mit
  Dunst (Landschaft mit Sonne im Bild, Gegenlicht am Abend). **Behoben:** 0.4.35.
- **Was:** Mit der ersten Fassung (0.4.30) wurde das Bild dunkler, der Himmel
  kippte ins Türkis bzw. bekam neben der Sonne einen violetten Stich; Zwischen-
  stände erzeugten einen hellen Streifen über dem Horizont.
- **Warum:**
  - Gerechnet wurde in gamma-kodierten Werten; das Streumodell gilt für
    lineares Licht.
  - Luftlicht = Mittel der hellsten 0,1 % des dunklen Kanals – bei Sonne im
    Bild kommt es aus deren Umgebung.
  - Der dunkle Kanal hält Himmel für Dunst (bekannte Schwäche des Dark
    Channel Prior). Erste Gegenmittel griffen nicht: Helligkeit relativ zum
    Luftlicht (blauer Himmel erreicht nur ein Drittel davon) und die einfache
    Helligkeitsschwankung (ein Himmel mit Verlauf schwankt auch; dichter Nebel
    drückt die Schwankung der Landschaft – Nebel galt als Himmel).
  - Kein Helligkeitsausgleich → Ergebnis dunkel. Ein Ausgleich über einen
    einfachen Weichzeichner trug Aufhellung vom Land in den Himmel (Streifen).
- **Wie behoben:** Lineares Licht; Luftlicht wie im Adobe-Patent US 9 305 339 B2
  (hellste 1 %, davon die mittleren); Durchlässigkeit auf einer Fassung
  ≤ 1024 px, verfeinert mit farbigem Guided Filter, kantentreu auf volle Größe
  (Fast Guided Filter); Himmelsschutz über **Bandpass-Struktur** (Differenz
  zweier Glättungen – Verläufe heben sich auf; gemessen Himmel 0,002–0,005,
  Landschaft 0,04–0,38, dichter Testnebel 0,03); Toleranz nahe am Luftlicht;
  Helligkeitsausgleich wie Adobe („guided brightness scaling“) mit Guided
  Filter und nie heller als das Original. Ein einziger Durchlauf über das große
  Bild: 78-MP-Panorama in 2,5 s.
- **Nicht tun:** Schwellen für den Himmelsschutz ohne Messwerte an echten
  Fotos und am Testnebel ändern. Zwischenkarten (Durchlässigkeit, Schutz)
  ansehen statt Ergebnisse zu raten. Helligkeitsverhältnisse nicht mit
  einfachem Weichzeichner glätten.
- **Wo:** `ImageConverter.Core/Imaging/Dehaze.cs`.
- **Test:** `DehazeTests` (Kontrast und Farbe kommen zurück, Helligkeit bleibt;
  klares Bild bleibt; Luftlicht aus dem Himmel; Nebel hinzufügen).
  Sichtprüfung an zwei Panoramen bei Dunst +60 und +100.

## 15. Dunst über 100: Bänder, Sonnensaum – dann Regler wirkungslos

- **Entdeckt:** 0.4.35 (08.10.2026), Regler auf Wunsch des Benutzers bis 300
  erweitert (bei Gegenlicht und leichtem Dunst wirkte 100 zu dezent).
  **Behoben:** 0.4.36.
- **Was:** Die Dunstmenge einfach zu vervielfachen ergab schwarze und rote
  Bänder am Horizont, einen violetten Saum um die Sonne, einen dunklen Bogen
  im Übergang Himmel/Land. Die ersten Gegenmittel machten den Regler über 100
  dann nahezu wirkungslos oder ließen ihn ab 150 stillstehen.
- **Warum:**
  - Über 100 rechnet das Modell über das Ziel hinaus, Kanäle fallen unter
    null bzw. über Weiß und werden abgeschnitten.
  - Die Sonne und ihr Lichthof sind kanalweise heller als das Luftlicht – eine
    Lichtquelle, kein Dunst.
  - Eine Untergrenze je Pixel („jeder Kanal behält 30 %“) hielt dunkle Stellen
    fest, die helleren daneben sanken weiter – der Kontrast nahm ab. Das
    Maximum der Untergrenze in der Umgebung liegt dagegen immer über der
    Wirkung bei 100 (der dunkle Kanal rechnet das dunkelste Pixel dort schon
    auf ~5 %) – dann wirkte der Regler nicht mehr. Eine harte Grenze ließ ihn
    ab etwa 150 stillstehen.
  - Eine Helligkeitsschwelle am hellsten Kanal schloss auch Dunst über
    kräftigen Farben aus.
- **Wie behoben:** Bis 100 unverändert; darüber eine zweite, verstärkte
  Durchlässigkeit (nur in der Landschaft, nicht im Himmel), die sich **weich**
  der **im Mittel der Umgebung** gesetzten 30-%-Grenze nähert und nie
  schwächer als 100 wirkt. Lichtquellen (kanalweise heller als das Luftlicht)
  und sehr Helles (nach Helligkeit, nicht hellstem Kanal) bleiben unberührt.
- **Nicht tun:** Begrenzungen je Pixel für eine Verstärkung, die je Umgebung
  geschätzt wird. Grenzen hart setzen, wenn ein Regler dagegen läuft.
- **Wo:** `ImageConverter.Core/Imaging/Dehaze.cs` (`LimitBoost`, `Tolerate`).
- **Test:** `DehazeTests.UpTo300_GetsStrongerSteadily_ButNothingCrushes`
  (Kontrast steigt in jedem Schritt, kein Sprung, < 3 % abgesoffen).
  Sichtprüfung an beiden Panoramen bei 100, 200, 300.

## 16. Dunst: ein Luftlicht für das ganze Bild – Gegenlicht-Panorama blieb fast unverändert

- **Entdeckt:** 0.4.36 (08.10.2026), vom Benutzer: „Panorama wird deutlich
  besser, Turmlandschaft leider noch nicht.“ **Behoben:** 0.4.37.
- **Was:** Bei einem Panorama mit hoch stehender Sonne erkannte das Verfahren
  links kaum Dunst, und am Horizont unter der Sonne blieb der dichteste Dunst
  ganz stehen.
- **Warum:**
  - Die Farbe und Helligkeit des Dunstes hängen von der Richtung zur Sonne ab.
    Gemessen am Horizont (linear): links 0,33–0,51, unter der Sonne bis 0,97.
    Das eine Luftlicht kam aus der Gegend der Sonne (0,78–0,88) – abseits
    davon wurde der Dunst auf etwa die Hälfte unterschätzt.
  - Die Regel „heller als das Luftlicht = Lichtquelle“ (aus Eintrag 15, gegen
    den Sonnensaum) griff deshalb am ganzen Horizont unter der Sonne.
  - Die Regel „sehr hell ab 70 % Helligkeit“ schloss hellen Dunst ebenfalls aus.
- **Wie behoben:** Luftlicht je senkrechtem Streifen (8, zwischen den Mitten
  gemittelt); Streifen ohne Himmel (nur Bäume, dunkler Kanal 0,01) fallen weich
  auf den Gesamtwert zurück, jeder Kanal bleibt bei 30–125 % davon.
  Lichtquelle erst ab 5 % über dem Luftlicht dieser Richtung, „fast weiß“ erst
  ab 85 % Helligkeit. Erkannter Dunst am Horizont: 0,13 → 0,27 und 0,33 → 0,54.
- **Bleibt offen:** Dünner Dunst über sonniger Vegetation in mittlerer Ferne
  (0,02–0,05 erkannt) – der dunkle Kanal findet dort fast schwarze
  Baumschatten. Abhilfe bräuchte ein zweites Merkmal (Kontrast-/
  Sättigungsverlust), siehe Analyse vom 08.10.2026.
- **Nicht tun:** Lichtquellen am Verhältnis zu einem einzigen Luftlicht
  erkennen; absolute Helligkeitsschwellen, die hellen Dunst mit erfassen.
- **Wo:** `ImageConverter.Core/Imaging/Dehaze.cs` (`AirLightColumns`, `Tolerate`).
- **Test:** `DehazeTests.AirLight_FollowsTheDirection_AndIgnoresStripsWithoutSky`.
  Zwischenkarten und Zeilenmittel der Durchlässigkeit an beiden Panoramen.

## 17. Dunst: Grün wurde ocker und braun, Smog blieb fast unverändert

- **Entdeckt:** 0.4.37 (08.10.2026), vom Benutzer: „aus grünen Flächen werden eher
  rötlich-braune … es wird alles sehr unnatürlich“; dazu zwei Testbilder (Berge
  mit Seen, Stadt im Smog). **Behoben:** 0.4.38.
- **Was:** Felder in mittlerer Ferne kippten ins Ocker/Braun (bei 300 deutlich);
  ein Bild mit dichtem Smog über einer Stadt änderte sich kaum.
- **Warum:**
  - Das Modell rechnet den dunkelsten Kanal gegen null – bei Vegetation ist das
    Blau. Gemessen an einem Feld (linear): Blau 0,12 → 0,07 (100) → 0,02 (300),
    Rot fast gleich: Olivgrün wird bunter und dunkler, also braun. Eine
    gedämpfte Farbe des Luftlichts half nicht (eher schlimmer).
  - Smog: Das Luftlicht kam vom überbelichteten Himmel (≈ 1,0), der Smog über
    der Stadt liegt bei 0,3–0,45 – an „Weiß“ gemessen wirkte er dünn (erkannt
    1–12 %). Dazu findet der dunkle Kanal zwischen Häusern und Bäumen immer
    Schwarzes; das Modell allein bleibt bei dünnem Dunst und Smog zahm.
- **Wie behoben:**
  - Ausgabe in Oklab: Helligkeit aus dem Modell, **Farbton aus dem Original**,
    Buntheit höchstens 1 + (1/t − 1) + 0,15 je Regler-Einheit (Dunst nimmt die
    Buntheit um t – so viel zurück ist berechtigt).
  - Luftlicht nur aus der Landschaft (Himmelskarte < 0,5), nicht aus dem Himmel.
  - Wie Referenzwerkzeug zusätzlich Klarheit (lokaler Kontrast, kantentreu über Guided
    Filter), feine Schärfe (3×3), sanfte S-Kurve, Dynamik (blasse Farben mehr);
    Klarheit und Schärfe nicht im Himmel (Rauschen in Wolken).
- **Nicht tun:** Die physikalische Rückrechnung direkt als Farbe ausgeben;
  Luftlicht aus dem hellsten Bereich des ganzen Bildes nehmen.
- **Wo:** `ImageConverter.Core/Imaging/Dehaze.cs` (`Apply`, `AirLightColumns`,
  `SkyMap`).
- **Test:** `DehazeTests.ThinHaze_KeepsTheHueOfVegetation`,
  `ClearImage_GetsOnlyModeratelyCrisper`. Sichtprüfung an vier Bildern bei 100,
  200, 300. Rechenzeit 78 MP: etwa 4 s (vorher 2,5 s); Vorschau ≈ 0,2 s.

## 18. Dunst: Messumgebung mit bekannter Wahrheit – Himmelsschutz hielt den Dunst fest

- **Entdeckt:** 0.4.38 (08.10.2026), Benutzer: „als ob der Nebel gar nicht da
  gewesen wäre“, Wiesen hinten (Turmlandschaft) noch bräunlich. **Behoben:** 0.4.39.
- **Messumgebung** (`%LOCALAPPDATA%\imageconverter-build\bench`,
  `Program.labor.cs.txt`, `labor.sh`): Aus dem klaren Vordergrund von
  Turmlandschaft und Berglandschaft wird künstlicher Dunst erzeugt (Dichte nach oben,
  Luftlicht links bläulich → rechts warm, wahlweise Himmelsstreifen). Gemessen:
  ΔE (Oklab ×100) gegen die Wahrheit, Farbtonfehler auf Grün, Buntheit und
  Helligkeit relativ zur Wahrheit, geschätzte gegen wahre Dichte je Höhe,
  Luftlicht. Dazu am echten Turmlandschaft: Farbton/Buntheit der Wiese hinten
  gegen die Wiese vorn.
- **Befunde (Fehler oben, Turmlandschaft mit Himmel, Regler 100):** Start 36 →
  12,5 nach Horizontregel → 11,8 nach niedrigem Fenster → 13 mit halbem
  Helligkeitsausgleich. Wiese hinten: Farbton 93° → 95–96° (vorn 98°),
  Buntheit 0,051 → 0,063 (vorn 0,064).
  - Farbton aus dem **dunstigen** Pixel trug den Stich des Dunstes
    (Farbtonfehler blieb −25° bei jeder Stärke) → Farbton aus der
    ungeklemmten Rückrechnung, im Himmel aus dem Original.
  - **Himmelsschutz** griff für dichten Dunst über Land (genauso glatt und
    hell) → nur, was vom oberen Rand herabhängt, ist Himmel (Horizontsuche je
    Spalte, waagerecht 12 % ausgedehnt, Wolken überbrückt, wenn darunter ein
    langer, ähnlich heller Himmelsstreifen folgt).
  - **Quadratisches Fenster** des dunklen Kanals griff nach unten in Zeilen
    mit weniger Dunst (Dichte um 0,1 unterschätzt) → senkrecht nur ein Viertel.
  - Luftlicht ohne Himmel wird unterschätzt (Mittel der Kandidaten liegt im
    endlichen Dunst) → hellste 30 % der Kandidaten; bleibt eine Schwäche.
  - Helligkeitsausgleich: 85 % → 0 % halbiert den Fehler; 50 % als Mittelweg.
- **Fehlversuche (nicht wiederholen):** Himmel über Farbe/Helligkeit gegen das
  Luftlicht erkennen (Abendhimmel abseits der Sonne ist dunkler, ähnlich warm
  → türkis); absolute Helligkeit als Himmelsmerkmal (heller dichter Dunst);
  Grau-Regel für den Farbton (dichter Dunst ist grau); höhere Struktur-Schwelle
  für Wolken (dichter Dunst hat dieselbe Struktur 0,02–0,06); Fortsetzung unter
  Wolken ab 60 % Helligkeit ohne Mindestlänge (helle Felder im Dunst).
- **Wo:** `Dehaze.cs` (`SkyMap`, `ReachFromTop`, `AirLightOf`, `Apply`).
- **Test:** DehazeTests, Sichtprüfung an 4 Bildern (Sonne, Wolken, Smog, Äste).
  Rechenzeit 78 MP: 6–7 s (vorher 4 s).
- **Zurückgenommen in 0.4.40:** Der Benutzer fand 0.4.39 „deutlich schlechter“
  als 0.4.38 – zu Recht: Die Ferne wurde ein dunkles graubraunes Band
  (Turmlandschaft), das Tal kippte ins Türkis (Panorama), das Bild wurde dunkler.
  Ursache: Das Laborziel (klarer Vordergrund als „Wahrheit“, Luftperspektive
  null) ist nicht das Sehziel. Ein Foto soll die Belichtung behalten und die
  Ferne weich lassen; der Farbton aus der Rückrechnung ist in dichtem Dunst
  Zufall (Luftlicht abgezogen → Blaugrün). `Dehaze.cs` und `DehazeTests.cs`
  wieder auf 0.4.38 (`2ded64c`); Messumgebung bleibt als Werkzeug, aber mit
  Sichtprüfung als Maß, nicht ΔE gegen die künstliche Wahrheit.
- **Lehre:** Jede Laborverbesserung erst am echten Bild gegen den letzten
  Stand ansehen (`cropstack`), dann erst übernehmen.

## 19. Dunst (Beta 0.4.41): Durchläufe statt Stärke, Luftlicht in Bereichen, Feineinstellung

- **Anlass:** Benutzer (08.10.2026): So denkt sich das Auge den Dunst weg — in
  Runden (Farbe erkennen, Kontrast, Sättigung, feine Schärfe, dann Kanten),
  jede Runde auf dem Ergebnis der vorigen; und „wo er weg ist, nicht noch mal“.
  Außerdem: Ring um die Sonne, Farben kippen je Bereich in andere Richtungen.
- **Messungen (Labor, Turmlandschaft und Panorama, volle Auflösung):**
  - Ungebremste Schleife 3 × 40 schlug den Einmal-Filter 100 klar (Wiese
    hinten: Farbton 93° → 95°, Buntheit 0,060 → 0,065 = Wiese vorn), 3 × 70
    ≈ das Rezept des Benutzers (Dunst 211). Ab 4 Runden kippte es: Himmel
    körnig, Vordergrund heller und bunter, Ferne magenta — Kontrast, Dynamik
    und Schärfe summierten sich je Runde, und die Schätzung fand im Himmel
    immer wieder „Dunst“.
  - Harte Bremse (Dunstkarte nur einmal schätzen, je Runde den Rest anwenden):
    kippt nicht, aber zu schwach — 3 Runden schwächer als ungebremst.
  - Luftlicht je Kachel (6 × 4, bilinear) beseitigt den Ring um die Sonne;
    senkrecht nur abweichen, wo die Kachel so dichten Dunst enthält wie ihre
    Spalte am Horizont (sonst halbe Farbrückgewinnung im Labor: Kacheln im
    Tal sehen Szenenfarbe, kein Luftlicht).
- **Umgesetzt:** Durchläufe 1–10 (Regler): jeder schätzt am Zwischenstand neu;
  Luftlicht und Himmel aus dem ersten; Himmel ab dem zweiten unverändert;
  Klarheit, Schärfe, Kurve, Dynamik einmal am Schluss. Dazu die bisher festen
  Werte als Regler (Vorgaben = alte Konstanten, Durchläufe 1 → Bild wie
  0.4.40), neu „Kanten“ (Unscharfmaskierung σ 2 nach allen Durchläufen). Die
  Feinregler erscheinen nur, wenn Dunst > 0; „Vorgaben“ setzt sie zurück.
  Versionszusatz: `<Version>0.4.41-beta</Version>` → Fenstertitel aus der
  InformationalVersion („0.4.41 beta“), AssemblyVersion bleibt 0.4.41.
- **Offen:** rosa Horizont (Farbton in dichtem Dunst); Standardwerte der
  Feineinstellung sucht der Benutzer in der Software; Luftlicht ohne Himmel
  (Haze-Lines) noch nicht gebaut.
- **Nicht tun:** Wahrnehmungsanteile je Durchlauf anwenden; Himmel in jedem
  Durchlauf neu schätzen; Luftlicht je Kachel ohne Rückfall auf die Spalte.
- **Wo:** `Dehaze.cs` (`Apply` mit `Pass`/`Settings`, `AirLightField`),
  `Adjustments.cs` (`DehazePasses` …, `PrepareKey`), `MainViewModel.Tools.cs`,
  `MainView.axaml` (Gruppe „DUNST · BETA“), `MainWindow.axaml.cs` (Titel).
- **Test:** `DehazeTests.Passes_RemoveMoreHaze_AndDetailsOnlyCountWithDehaze`.
  Vergleichsbilder: `%LOCALAPPDATA%\imageconverter-build\vergleich`.
- **Nachtrag 0.4.42-beta (Ring um die Sonne):** Gemessen entlang einer Linie
  durch die Sonne: Die Himmelskarte war im Lichthof 0 (Strahlen = Struktur),
  der Hof galt als Landschaft mit roher Durchlässigkeit 0,2–0,6; die
  Pixelregeln der Toleranz verdoppelten dort die Buntheit (0,023 → 0,045) —
  der pinke Ring. Abhilfe: Lichtquelle mit Hof zählt als Himmel (ausgefressene
  Pixel, Gauß σ 5 % der Kante, ab 1 %); im Himmel gelten nur die Grenzen, nicht
  die Pixelregeln; im Himmel keine zusätzliche Buntheit und keine Dynamik.
  Feinregler immer sichtbar (Wunsch). Der Block rechnet bei jeder
  Reglerbewegung vom Original — nichts kumuliert.
- **Nachtrag 0.4.43-beta (Durchläufe wirkten nicht, Sonne ohne Strahlen):**
  Abnahme gegen die Laborschleife (Stapel `Abnahme*` im Vergleichsordner):
  App 3 × 40 war flacher als das Labor (Wiese hinten 93° / 0,058 statt 95° /
  0,072), 6 Durchläufe brachten fast nichts. Ursache (Ausschluss-Messung über
  fünf Schleifen-Varianten): Das Labor hat je Runde Kontrast +8, Dynamik +8 und
  Schärfe über die **Tools-Kette** angewendet, die App alles nur einmal am
  Schluss. Jetzt gilt bei Durchläufen > 1 jeder Feinregler „je Durchlauf“ über
  die Tools-Kurven (Vorgaben = Laborrezept), die Dunst-eigene Wahrnehmung nur im
  ersten. Messung danach: 3 × 40 → 94° / 0,063, 6 × 40 → 97° / 0,069.
  Sonne (Strahlen weich): Messung der Schwankung über den Winkel je Radius
  (= Strahlenstärke) für Original, App 1×, App 3× und Labor. Befund: 0.4.38
  lässt die Sonne bei 1× unverändert (±13,4 wie das Original) — der Hof ist
  dort glatter Himmel ohne Klarheit, Schärfe, Dynamik; die Regel „heller als
  das Luftlicht“ gibt t = 1. Die App hatte ±7,7 (1×) / ±6,3 (3×). Ursachen und
  Abhilfen: (1) Hof galt als Landschaft → Himmelskarte trägt den Lichthof als
  Wert über 1 (auf den Scheitel normiert, damit er bei jeder Bildgröße gleich
  weit reicht; Gate gegen einzelne heiße Pixel); dort t = 1, keine Klarheit,
  Schärfe, Dynamik, kein Helligkeitsausgleich. (2) Die S-Kurve staucht die
  Strahlen um 11 % (Schulter) → nicht im Lichthof. Danach 1× = Original
  (±13,6), 3× ±11,1 (Labor ±11,3). Fehlversuche: Klarheit/Dynamik/Schärfe in
  den Hof wie vermeintlich 0.4.38 (±8,6 — falsche Annahme), Helligkeits-
  ausgleich im Hof (±7,4), Kanten-Regler (wirkungslos an der Sonne).
  Konsumenten der Himmelskarte klemmen auf 0–1 (ProtectSky, Einfrieren).
- **Lehre (Abnahme):** Vergleiche gegen das Labor immer mit demselben Code-
  stand rechnen, den das Labor hatte (`bench-0438` im Worktree `wt-0438`), und
  Strukturen (Strahlen, Wiese) messen statt Ringmittel — Ringmittel glätten
  genau das weg, was das Auge sieht. Bei Durchläufen > 1 wirkt jeder
  Feinregler je Durchlauf (Semantik des Benutzers); 6 Durchläufe mit den
  Vorgaben kippen (L 0,44, Buntheit 0,085) — das ist Reglersache, 3–4 sind gut.
- **Nachtrag (Hof-Grenze):** Ein Hof nach Radius (Gauß um die ausgefressene
  Scheibe, auf den Scheitel normiert) ergab im glatten Himmel eine sichtbar
  hellere Scheibe um die Sonne – innen unverändert, außen entdunstet. Jetzt
  folgt der Hof zusätzlich der Helligkeit (SmoothStep 0,45–0,75 linear), wie
  0.4.38 pixelweise schonte; die Grenze liegt damit im eigenen Verlauf des
  Himmels und ist unsichtbar. Messung danach: 1× = Original bis r 120, bei
  r 200 ±16,6 statt 19,5; 3× ±10,7 (Labor ±11,3).
- **Treppen im Himmel (0.4.43, 0.4.44-beta):** Ursache: Jeder Durchlauf und
  jeder Schritt darin (Dunst → Tools-Kontrast/Dynamik → Unscharfmaskierung)
  rundete auf 8 Bit, und der Kontrast spreizte die übrigen Stufen — bei 3
  Durchläufen fehlten im Himmel der Turmlandschaft 18 % der Tonwerte (Original
  0,4 %), sichtbar als Konturen. Eine „Beta 2“ mit der genauen Laborschleife
  auf dem 0.4.40-Filter hatte dieselben Treppen (5–7 %) und gefiel auch sonst
  nicht – verworfen. Abhilfe 0.4.44: die ganze Dunstkette in 32-Bit-Fließkomma
  (`Pixels`), einmal am Schluss mit TPDF-Dither gerundet; dazu Entstufen
  (`Deband`, f3kdb/libplacebo-Verfahren mit strengerem Kriterium: jede der vier
  Proben unter der Schwelle) an der Quelle und nach jedem Durchlauf, vor der
  Schärfe. Messung 100 × 3: 1,1 % ohne, 0,6 % mit Entstufen; Detail der Wiese
  unverändert (1,36).
- **Zu kräftig je Durchlauf (0.4.43):** Volle Stärke plus Kontrast +8 und
  Dynamik +8 je Durchlauf — bei Dunst 60 waren schon 2 Durchläufe zu viel, die
  Dynamik wirkte doppelt (im Filter und über die Tools). 0.4.44: je Durchlauf
  Dunst 0,3 × Regler, Kontrast +4, Dynamik +2 bei den Vorgaben, 3 Durchläufe
  als Vorgabe. 100 × 3 liegt zwischen 0.4.43 60 × 1 und 60 × 2 (Kontrast 62,1,
  Buntheit Wiese 0,059). Die Tests für den einzelnen Schritt (bis 300, klares
  Bild) laufen deshalb ausdrücklich mit einem Durchlauf.
- **Meilenstein 0.4.45 (Referenz für den Neubau):** Stand 0.4.44 beta, vom
  Benutzer abgenommen („3–4 Durchläufe sehen sehr gut aus, das Abschwächen hat
  viel gebracht“). Exe gesichert unter
  `%LOCALAPPDATA%\imageconverter-build\publish-0445\`. Folgende Versionen bis
  zum nächsten Meilenstein tragen wieder `-beta`. Leistung als Ausgangswert in
  KONZEPT.md §6 — Speicher ist der Engpass (28,7 MP, 3 Durchläufe: 8,3 GB Spitze).
- **Tooltips unter dem Mauszeiger (bis 0.4.46):** Avalonias Vorgabe ist
  `PlacementMode.Pointer` — der Tooltip lag auf Regler oder Knopf, man klickte
  ihn an statt des Elements. Seit 0.4.47 beta global `Top` mit 6 px Abstand
  (`App.Initialize`). `OverrideDefaultValue<Control>` wirft („Metadata is
  already set for Placement on Control“, Absturz beim Start!) — darum für
  `TemplatedControl`, `TextBlock`, `Border`, `Panel`, `Image` einzeln. Neue
  Tooltip-Träger anderer Art dort ergänzen.
- **Phase 1, 32-Bit-Kette (0.4.49 beta):** Magick erwartet beim Einlesen von
  `ushort`-Pixeln `StorageType.Quantum`, nicht `Short` („Storage type should be
  Quantum“). Im Speicher erzeugte Testbilder haben 16 Bit; `Depth = 8` setzt nur
  die Angabe, die Pixel bleiben dazwischen — echte 8-Bit-Testbilder über
  `ImageOps.FromBgra`. Die Abnahme muss messen, was gespeichert wird (über
  `Encode`), seit erst das Kodieren mit Dither rundet; sonst meldet das
  Lückenmaß „Treppen“ falsche Verschlechterungen. Dieses Maß ist mit Dither
  ohnehin unzuverlässig (Dither füllt Lücken) — geurteilt wird über die
  Stufenhöhe. Ergebnis gegen 0.4.45: mittleres ΔE ≤ 0,38, alle Messwerte ≈.
- **Phase 2, Speicher und Tempo (0.4.50 beta):** Gemessen (Panorama 28,7 MP, je
  Fall frischer Prozess ohne Aufwärmlauf — ein Aufwärmlauf ließ Puffer im Pool
  liegen und verfälschte die Spitze): Dunst 100 × 3 19,5 s / 6,4 GB → 13,9 s /
  3,7 GB; Kombi 25,1 / 7,7 → 18,4 / 5,2. Maßnahmen: `PixelPool` (Rückgabe nur
  eigener Zwischenstände, nie des Eingangs — Test `PooledBuffers_NeverTouchTheInput`;
  höchstens 2 freie je Größe: mit 1 stieg die Spitze, weil weggeworfene Puffer
  neu angelegt wurden), Local Laplacian ohne neue Vollbilder je Umgebungswert,
  Entstufen mit Richtungstabelle statt sin/cos je Pixel und in Kacheln,
  Dunst-Auslesen über `Cell` (Lage einmal je Pixel statt je Karte), 16 Bit von/zu
  Magick in Streifen, `PixelPool.Trim(release)` nach dem Export. Vorschau bricht
  zwischen zwei Schritten ab (`WorkPlan` mit CancellationToken) und zeigt den
  Schritt. Wirkungslos: Exponentialfunktion im Local Laplacian als Tabelle
  (Speicherzugriff bremst, nicht die Rechnung), Helligkeitsebene für die feine
  Schärfe. Offen: Local Laplacian (6 s bei 28 MP), Farbumrechnung je Pixel im
  Dunst (1,8 s je Durchlauf), Entstufen (1 s je Aufruf, Speicherlatenz).
- **Phase 3, Szenenkarte (0.4.51 beta):** Himmel und Lichthof als eigener
  Baustein `SceneMap` (Kodierung unverändert: 0 Land, 1 Himmel, über 1 Hof),
  einmal im ersten Durchlauf bestimmt und über `Dehaze.Pass.Scene` für alle
  weiteren Schritte zugänglich. Reine Umstrukturierung — alle 48 Abnahmebilder
  byte-gleich mit 0.4.50. Was die Karte zusätzlich schonen soll (Kontrast und
  Schärfe je Durchlauf im Himmel), ändert das Bild und gehört zu Phase 4.
- **Phase 4, Physik und Look getrennt (0.4.52 beta):** Der Dunstfilter rechnet je
  Durchlauf nur noch das Modell; der Look (`DehazeLook`: Klarheit, S-Kurve,
  Dynamik vor dem Entstufen, Schärfe/Kanten danach) folgt an einer Stelle, je
  Feinregler ein Mechanismus. Abgestimmt in fünf Messrunden auf die Referenz
  0.4.45. Lehren: (1) Schärfe nur in der Landschaft nahm den Sonnenstrahlen die
  Struktur (Strahlen 1,05 statt 1,18) — die Strahlen reichen in den Himmel; voll im
  Himmel betonte sie JPEG-Blöcke (Stufen 0,79 statt 0,59) → im Himmel ein Anteil
  (0,4). (2) Die alte Tools-Kurve drehte je Kanal um sRGB 0,5 (Oklab-L ≈ 0,6), die
  Sinus-Kurve um L 0,5 — mit nur der zweiten wurden Bilder gut eine Stufe heller;
  Drehpunkt jetzt 0,56. (3) Im Lichthof halbe Kurve, ganz ohne fehlte den Strahlen
  der Kontrast. (4) Klarheit auf dem entdunsteten Bild wirkt stärker — 0,2 statt
  0,3. Luftlicht: Kacheln schätzen jetzt aus überlappenden Fenstern (halbe Kachel
  in jede Richtung) — künstlicher Dunst oben näher an der Wahrheit (27,7 → 27,4).
  Die Abnahme wertete eine Farbtonänderung beim künstlichen Dunst als schlechter,
  auch wenn sie zur Wahrheit hin ging — korrigiert (nur |neu| − |alt| > 2°).
  Nachtrag: Mehr Dynamik im Himmel (0,35), um im Smog (Photo175) die fehlende Farbe
  auszugleichen, ließ blasse bläuliche Hügel ins Türkis kippen (Berglandschaft, Kombi
  Panorama: Farbton 149° statt 115°). Die Ursache der fehlenden Farbe war eine
  andere: Die alte Tools-Kurve je Kanal hob nebenbei die Buntheit, die Kurve auf der
  Helligkeit nicht → Kopplung Buntheit an die Kurve (0,3), Dynamik im Himmel wieder
  0,12. Verbleibende Ausnahme der Abnahme: Berglandschaft mit der Panorama-Kombi
  (Dunst 211, Schärfen 1,4) 139° statt 115° — beide Fassungen dort gleich
  überzeichnet, gemessen an wenigen grünen Pixeln; im Bild kein sichtbarer Unterschied.
- **Vergleichsbilder nie als gestapeltes PNG aus dem Stapel-Hilfswerkzeug**
  (`bench-stapel`, außerhalb des Repos): Beim Schreiben als PNG reduzierte es die
  Farben — der Himmel sah nach starken Treppen aus, in beiden Fassungen, obwohl
  die Einzelbilder glatt waren. Stapel als JPEG schreiben; bei Treppen im Vergleich
  immer zuerst das Einzelbild ansehen.
- **Meilenstein 0.5.0 (09.10.2026):** Neubau der Bildkette Phasen 0–4 (32 Bit,
  Pufferpool, Szenenkarte, Dunst in Physik und Look getrennt), vom Benutzer als
  Meilenstein bestimmt. Im Bild bei den Vorgaben bewusst wie 0.4.45. Neue Referenz:
  Exe unter `%LOCALAPPDATA%\imageconverter-build\publish-050\`, Abnahme-Referenz
  `abnahme/0.5.0`. Linux-Bau noch offen. Hinweis zum Hook: Er zählt bei „-beta“ nur
  die Zahl hoch und verliert den Zusatz — Version immer von Hand setzen.
- **Meilenstein 0.5.2 (09.10.2026):** Vorschau-Bedienung — Knopf für das
  Auswahlwerkzeug (Vorgabe an, blau; aus = Ziehen verschiebt), Strg + Ziehen
  verschiebt immer, Leertaste + Ziehen entfernt (vom Benutzer gewünscht, Strg
  reicht). Bildkette unverändert gegenüber 0.5.0. Hinweis: `dotnet test` baut die
  Desktop-Exe nicht neu — für Bildschirmfotos erst die ganze Lösung bauen (sonst
  zeigte der Testlauf noch 0.4.51).
- **Meilenstein 0.5.4 (09.10.2026):** Verschieben im Zoom nur noch mit Strg + Ziehen
  (oder Ziehen bei ausgeschaltetem Auswahlrahmen) — mittlere Maustaste und Mausrad
  entfernt (vom Benutzer gewünscht). Strg + Mausrad zoomt weiter. Bildkette unverändert.
- **Dunstfarbe (0.5.5 beta):** Bis 0.5.4 blieb der Farbton des Originals stehen — samt
  Farbstich des Dunstes (künstlicher Dunst: Wiesen 16° zu blau). Der volle Farbton des
  Modells machte Felder braun (dunkelster Kanal gegen null); jetzt wird nur der Anteil
  der Luftlicht-Farbe abgezogen: (1 − t) · Y(Luft) / Y(Pixel), höchstens 0,8, je
  Durchlauf mit der Dunstmenge, nur in der Landschaft. Neuer Feinregler „Dunstfarbe“,
  Vorgabe 50 %: Smog verliert seinen Gelbstich, im Abendlicht (Panorama) nimmt viel
  davon auch Wärme weg (92° → 107° bei 100 %, 97° bei 50 %). Ein Test mit überall
  gleich dichtem Dunst zeigte keine Wirkung — ohne dunstfreien Bezug schätzt der Filter
  die Dunstfarbe aus dem Bild selbst; Testbilder brauchen nach oben zunehmenden Dunst.
- **Regler „Dunstfarbe“ mit festem Verlauf (0.5.7 beta):** 0.5.6 zeigte die geschätzte
  Dunstfarbe des Bildes und ihre Gegenfarbe — der Verlauf blieb grau, bis eine Vorschau
  mit Dunst gerechnet war, und war für den Benutzer meist grau. Auf Wunsch jetzt fest wie
  beim Regler „Farbe“: links orange-rötlich, Mitte grau, rechts bläulich. Bei bläulichem
  Dunst wirkt der Regler umgekehrt — bewusst in Kauf genommen, die Richtung ist so sofort
  verständlich. Dichten Dunst/Smog nicht weiter verfolgen (Benutzer: dort ist keine
  Information mehr herzustellen). Abnahme: neuer Fall „NutzerPanorama“ (Dunst 54 × 7,
  Schärfe fein 29 %, Kanten 13 %, Dunstfarbe 48 % — vom Benutzer als sehr gut befunden).
- **Meilenstein 0.5.8 (09.10.2026):** Stand 0.5.7 beta als Meilenstein — Dunst mit
  Physik/Look getrennt, Regler Dunstfarbe (Vorgabe 50 %, fester Verlauf warm → kühl),
  Auswahlwerkzeug-Knopf, Verschieben mit Strg + Ziehen. Danach: KI live mischen
  (Stärke/Hautstruktur ohne neues Rechnen), Natürlichkeit für CodeFormer, weitere
  Gesichtsmodelle; DiffBIR/SUPIR als Versuch für später mit Grafikkarte.

## 20. KI: Stärke rechnete das Modell neu, Läufe häuften sich, Vorher/Nachher gesperrt

- **Entdeckt:** 0.5.8 (09.10.2026), Fragen des Benutzers („funktioniert Stärke nur als
  Overlay?“, „beginnt KI anwenden immer vom Original?“) und Test mit offenem Ergebnis.
  **Behoben:** 0.5.9 beta.
- **Was:** (1) „Stärke“ war nur eine Mischung mit dem Bild davor, wurde aber vor dem
  Lauf festgelegt — jeder andere Wert hieß: Modell noch einmal rechnen (Sekunden bis
  Minuten). (2) Jeder Lauf wirkte auf das aktuelle Bild, also auf das Ergebnis der
  vorigen KI — zweimal CodeFormer machte Gesichter immer glatter. (3) Während ein
  Ergebnis offen war (noch nicht übernommen), blieb „Vorher / Nachher“ gesperrt: der
  Vergleich kannte nur Stände aus dem Verlauf.
- **Wie:** Das Modell rechnet einmal mit voller Wirkung (`AiFaces.Run` → `FaceRun`,
  `AiEnhancer.Run` → `EnhanceRun`, beide `IAiRun`); Stärke und neue Hautstruktur mischen
  danach live (`Preview` verkleinert, `Compose` in voller Größe beim Übernehmen).
  „KI berechnen“ zeigt das Ergebnis, „Übernehmen“ macht einen Schritt daraus,
  „Verwerfen“ lässt das Bild. Ist das aktuelle Bild das Ergebnis des letzten KI-Schritts,
  rechnet der nächste Lauf auf dem Bild davor (`_lastAi` in `AiViewModel`) — das neue
  Ergebnis ersetzt es, statt darauf aufzubauen. Die Natürlichkeit (CodeFormer `weight`)
  geht ins Modell: nach 0,6 s Ruhe am Regler neu rechnen, die Gesichtersuche bleibt.
  Vorher-Seite eines offenen Ergebnisses: `MainViewModel.OverrideBeforeBitmap`, gefüllt
  mit `Preview(0, 0)` (Stärke 0 = Bild vor der KI, gleiche Größe wie die Vorschau).
- **Hautstruktur:** tauscht die feine Struktur des Modells (Ergebnis minus über etwa
  ein Bildpixel geglättet) gegen die des Bildes davor; Augen, Mund und Form bleiben vom
  Modell. Vorgabe 30 %. Beide Hochpässe müssen über dieselbe Weite im *Bild* gehen
  (Ausschnitt: Sigma = Maßstab × 1 px), sonst nimmt der Regler mehr Modelldetail weg,
  als er Original zurückbringt — das Gesicht würde weicher statt körniger.
- **Nicht tun:** Die Stärke wieder ins Modell oder in den Lauf geben; einen Lauf auf
  einem KI-Ergebnis starten; beim Übernehmen das ganze Bild neu rechnen.
- **Nachweis:** `AiTests.FaceRun_Texture_BringsFineDetailBack_AndZeroMatchesRestore`,
  `AiTests.EnhanceRun_StrengthLive_MatchesEnhance`; UI-Test an Familienfoto (lokal):
  Stärke 0 = Original (Abweichung 0), Übernommen = Vorschau, zweiter Lauf + Übernehmen
  = erster (nichts häuft sich), Vorher/Nachher bei offenem und übernommenem Ergebnis.

## 21. KI und Einstellungen: Reihenfolge wählbar, Vorschau sprang beim Übernehmen

- **Entdeckt:** 0.5.9 beta (09.10.2026). Wunsch des Benutzers: der KI wahlweise das fertig
  eingestellte Bild geben (Gesichter zuletzt) oder erst die KI und danach die Einstellungen —
  „doppelt soll nichts angewendet werden“. **Behoben:** 0.5.10 beta.
- **Was:** (1) Ein offenes KI-Ergebnis wurde ohne Einstellungen gezeigt — Dunst, Helligkeit usw.
  fehlten, bis man übernahm. (2) Beim Übernehmen sprang die Vorschau sichtbar (Laub, ~10 Stufen
  im Mittel): das offene Ergebnis war mit Flächenmittel auf höchstens 2048 px verkleinert, die normale
  Vorschau mit dem gewählten Filter (Lanczos) auf die Zielgröße — bzw. ohne Look das Bild selbst.
- **Wie:** Reihenfolge im KI-Bereich. „Erst KI“: Vorschau = Ergebnis + aktueller Look
  (`MainViewModel.RenderLookPreview`), Regler bleiben. „Erst Einstellungen“: Look in voller Größe
  (`ImageExporter.RenderLook`, dieselbe Kette wie im Export, ohne Skalieren/Profil/Metadaten) ins
  Eingangsbild, beim Übernehmen `ApplyImageEditAsync(…, lookAfter: Neutral)` — Regler im *selben*
  Rückgängig-Schritt auf null (Snapshot hat den Look davor). Für einen neuen Lauf merkt sich
  `_lastAi`, welcher Look eingerechnet ist: Lauf vom Bild davor mit diesem Look; bei „erst KI“ danach
  kommt er beim Übernehmen in die Regler zurück. Vorschaugröße und Filter wie die normale Vorschau
  (`AiPreviewSize`, `MainViewModel.PreviewSize`; ohne Look und bei Zielgröße = Bildgröße volle Größe).
- **Nicht tun:** Den Look ins Bild rechnen und die Regler stehen lassen (doppelt); bei „für alle
  Bilder“ einzeln zurücksetzen (träfe alle Bilder — deshalb gesperrt); die KI-Vorschau anders
  verkleinern als die normale Vorschau.
- **Nachweis:** `LookTests` (RenderLook = Export-Kette pixelgleich; Neutral ohne Wirkung, Feinwerte
  bleiben; Schärferadius skaliert mit der Breite). UI-Test (Foto 2012, Dunst +184, Helligkeit +54,
  CodeFormer): Hintergrund vorher/offen 0,01, offen/übernommen 0,15 („erst KI“) bzw. 0,14
  („erst Einstellungen“), Regler danach 0 / nach Strg+Z wieder +54 / +184.

## 22. Neue Bilder übernahmen die Regler des vorigen Bildes

- **Entdeckt:** 0.5.11 beta (09.10.2026), vom Benutzer: nach dem Hineinziehen eines neuen Bildes
  standen Dunst, Helligkeit usw. noch vom vorigen Bild. **Behoben:** 0.5.12 beta.
- **Was/Warum:** `OpenAsync` ersetzt Dateien und Verlauf, ließ aber die Reglerwerte stehen — das
  erste neue Bild bekam sie wie ein „für alle“-Stand.
- **Wie:** `MainViewModel.ResetImageSliders()` in `OpenAsync`: Licht, Dunst samt Feineinstellung,
  Farbe, Schärfe (Radius 1,0), Entrauschen, Ecken auf die Vorgaben, ohne Rückgängig-Schritt; Ereignis
  `FilesReplaced` → KI: Stärke 100 %, Hautstruktur 30 %, Natürlichkeit 60 %, offenes Ergebnis weg.
  Bleiben: Format, Qualität, Größe, DPI, Metadaten, Farbraum, Ordner, Name, KI-Modell, Reihenfolge
  (Vorlieben, keine Bearbeitung). Blättern zwischen den geöffneten Bildern ändert nichts.
- **Nachweis:** `NewImagesTests`.
- **Nachtrag 0.5.13 beta:** Auf Wunsch des Benutzers beginnen neu geladene Bilder jetzt *ganz* wie
  nach dem Programmstart — auch Format, Qualität, Größe (Originalauflösung), DPI aus der Datei,
  Metadaten, Farbraum, Ordner, Name, „für alle“, Auswahlwerkzeug, KI-Modell und Reihenfolge.
  `MainViewModel.ResetToStart()` stellt den beim Start gemerkten Stand (`_startSettings`) wieder her;
  gespeichert wird zwischen Programmstarts ohnehin nur die Sprache. Blättern zwischen schon
  geöffneten Bildern ändert weiterhin nichts. UI-Test über den Dateidialog: Qualität 36 → 82,
  Dunst +184 → 0, Größe 1200 × 900 (Original).
- **Nachtrag 0.5.14 beta:** „Gleiche Einstellungen für alle Bilder“ wird beim Laden neuer Bilder
  *ausgeschaltet* (nicht wie beim Start eingeschaltet) — Wunsch des Benutzers: sonst überschreibt
  eine Änderung an einem Bild, wenn man den Haken übersieht, unbemerkt die anderen. Jedes neue Bild
  beginnt beim ersten Anzeigen mit dem Startstand (`_individualTemplate = _startSettings`, DPI aus
  der Datei) — vorher hätte es im Einzelmodus den Stand des zuletzt gezeigten Bildes übernommen.
  UI-Test mit zwei Bildern: Bild 1 Qualität 36 → Bild 2 zeigt 82, 2592 × 1944, 72 dpi → zurück
  bei Bild 1 wieder 36.
