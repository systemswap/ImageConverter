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
