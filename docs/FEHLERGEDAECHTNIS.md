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

## 23. Nie angezeigte Bilder: DPI der Datei ging beim Export und Druck verloren

- **Entdeckt:** 0.5.14 beta (09.10.2026), bei der Durchsicht nach dem Umstellen auf „je Bild“.
  **Behoben:** 0.5.15 beta.
- **Was:** Mehrere Bilder exportieren oder drucken, ohne jedes einmal anzuzeigen: diese Bilder bekamen
  die DPI der Vorlage (72 bzw. die des gezeigten Bildes) statt der aus ihrer Datei — falsche DPI im
  Ergebnis und falsche Druckgröße.
- **Warum:** Die DPI liest `ImageLoader` aus der Datei, aber nur `ShowFileAsync` übernahm sie (über
  `EditHistory.Dpi`); `GetImageAsync` gab sie nicht weiter, `SettingsFor` kannte sie nicht.
- **Wie:** `GetImageAsync` liefert die DPI mit, `SettingsFor(path, w, h, fileDpi)`; dieselbe Regel wie
  beim Blättern in `WithFileDpi`: aus der Datei, wenn die Vorlage der Startstand ist oder ihre DPI noch
  die der Datei des Bildes davor — selbst eingestellte DPI bleiben.
- **Durchgesehen, ohne Befund:** Drehung (EXIF) und Farbprofil macht der Loader für beide Wege gleich;
  Größe wird übergeben; Dateigröße und Formatname dienen nur der Anzeige.
- **Nachweis:** `NewImagesTests.UnseenImage_GetsTheDpiOfItsFile`.
- **Meilenstein 0.5.16 (09.10.2026):** Stand 0.5.15 beta als Meilenstein — KI live mischen
  (Stärke, Hautstruktur, Natürlichkeit), Übernehmen/Verwerfen, kein Aufhäufen, Reihenfolge KI ↔
  Einstellungen, GFPGAN 1.3 und RestoreFormer++, neue Bilder beginnen wie nach dem Start und je Bild,
  DPI aus der Datei auch für nie angezeigte Bilder. Dunst-Filter unverändert seit 0.5.8.
- **Meilenstein 0.5.18 (09.10.2026):** Stand 0.5.17 beta (Stärke-Tooltip als Deckkraft) als Meilenstein
  vor den Detailreglern „Falten & Züge“ und „Körnung angleichen“.

## 24. KI-Detailregler: Falten & Züge, Körnung, Karte — drei Fallen beim Bau

- **Gebaut:** 0.5.19 beta (09.10.2026), Wunsch des Benutzers: Gesichter kommen zu glatt; Regler für
  Falten und für Menge und Größe des Korns, vorher aus dem Ausgangsbild einlesen, nachher vergleichen,
  übereinanderlegen zum Anschauen. Alles wirkt live (das Modell rechnet einmal).
- **Wie:** `FaceDetails` + `AiFaces.Run`/`PasteBack`. *Vor der KI:* Gesichtsbereiche mit BiSeNet
  (Hilfsmodell „Gesichtsbereiche“, 53 MB, MIT; ohne ersatzweise Kreise um Augen und Mund) und das
  Kornprofil des Fotos (wie AV1 Film Grain: Streuung je Helligkeit in den ruhigsten 16er-Blöcken,
  Korngröße über das Verhältnis zweier Hochpässe, abgeglichen an künstlichem Korn). *Nach der KI:*
  Falten = mittlere Detailstufe (1–4 Bildpixel) Original minus Modell, nur auf Haut/Nase/Ohren/Hals,
  mit Abstand zu Augen, Lippen und Gesichtsrand, und nur wo beide übereinanderpassen (örtliche
  Korrelation) — sonst doppelte Konturen. Korn: festes Muster je Bildpixel, Gauß in Korngröße,
  in der Vorschau aus der vollen Größe verkleinert. Karte: rot = Falten kommen zurück, blau = bleibt
  vom Modell (Augen, Brauen, Lippen, Zähne).
- **Falle 1 – `new AiBlend()` war Stärke 0:** Als `record struct` ruft `new AiBlend()` den leeren
  Konstruktor auf, die Vorgaben (Stärke 1) gelten nicht — der Zwischenstand während der Rechnung und
  ein Test zeigten das Original. Jetzt `sealed record` (Klasse).
- **Falle 2 – Korn auf erfundene Struktur:** „100 % = wie im Foto“ legte das volle Korn des Fotos
  auch auf Gesichter, in die CodeFormer schon selbst feine Struktur erfunden hatte (2012er Foto:
  Original 3,0 → KI 4,4 → mit Korn 4,7 Stufen, zu körnig). Jetzt misst `GrainGain` nach der KI das
  Korn des eingesetzten Gesichts (ruhigste 8er-Blöcke auf der Haut, wie beim Foto) und füllt nur auf,
  was fehlt (Streuungen quadratisch). 2016er Foto: KI 1,9 → mit Korn 3,7 (Foto 3,3, Original 3,5).
  Erst die Gesamtstreuung der Haut zu nehmen ging schief: Konturen von Nase und Wangen zählten mit,
  es kam gar kein Korn dazu.
- **Falle 3 – Kanten an den Bereichsgrenzen:** Die Detailstufe reicht über die Grenze der Haut
  hinaus; ohne Abstand kamen Lid-, Lippen- und Gesichtsrand-Kanten aus dem unscharfen Original mit.
  Abstand drei Viertel der Stufenbreite (`FoldsKeepAway`).
- **Nachweis:** `FaceDetailsTests` (Falten auf Haut ≥ 30 % näher am Original, am Auge unverändert;
  Kornmessung weiß und grob; Körnung füllt bis zum Foto auf; nichts, wenn das Gesicht schon körnig
  ist). Vergleichsbögen an Familienfotos (lokal, nicht im Repo).
- **Meilenstein 0.5.20 (09.10.2026):** Stand 0.5.19 beta — KI-Detailregler (Falten, Körnung, Korngröße,
  Karte) vom Benutzer am Foto 172251 als gut befunden: GFPGAN Stärke 80, Hautstruktur 69, Falten 48,
  Körnung 31, Korngröße ×0,46, erst KI; Dunst 47 × 6, Klarheit 29, Kontrast 12, Dynamik 20, Schärfe fein 18,
  Kanten aus, Helligkeit 72, Entstufen 50, Dunstfarbe 50.

## 25. KI: Dunst nach der KI ohne Fortschritt, Vorher zeigte gefiltertes Bild, Regler versteckt

- **Entdeckt:** 0.5.20 (09.10.2026), vom Benutzer. **Behoben:** 0.5.21 beta.
- **Was:** (1) Bei „Erst KI, dann Einstellungen“ lief nach der KI der Dunst (mehrere Durchläufe) über
  das Ergebnis, ohne dass etwas angezeigt wurde — im Debug-Build ~2,5 s je Durchlauf auf 2048 px.
  (2) „Vorher / Nachher“ zeigte bei offenem Ergebnis links das Bild *mit* Einstellungen. (3) Regler
  erschienen je nach Modell oder verschwanden; Modell- und Nachher-Regler standen gemischt.
- **Wie:** `ImageExporter.PlanLook` / `Look.Plan` liefern die Schritte des Looks; die KI-Vorschau
  zeigt sie in Fortschrittsbalken und Label („Dunst entfernen – Durchlauf 2 von 6“), danach wieder
  die Fertig-Meldung. Eine neue Reglerbewegung bricht die veraltete Rechnung zwischen zwei Schritten
  ab. Bei „Erst Einstellungen“ zeigt das Ins-Bild-Rechnen dieselben Schritte. Vorher = Bild vor der
  KI ohne Einstellungen (`setup.Base`, verkleinert wie die Vorschau). Panel: oben, was ins Modell
  geht (Modell, Natürlichkeit, Größe, Reihenfolge), dann KI berechnen und Fortschritt, ein dezenter
  Strich, darunter, was danach live wirkt (Stärke, Hautstruktur, Falten, Körnung, Korngröße,
  Karte); alle immer sichtbar, nicht passende ausgegraut (Tooltip „Nur …“ auch ausgegraut).

## 26. Liste „Bearbeitungen“: je Bearbeitung eine Zeile, Auge blendet aus Nachher und Export aus

- **Gebaut:** 0.5.23 beta (09.10.2026), Wunsch des Benutzers: Liste mit Augensymbol, was in „Nachher“
  einfließt; nicht jede Reglerbewegung, sondern je Bearbeitung in Rechen-Reihenfolge; beim Export nur
  das Angehakte; „Neu anwenden“ schreibt die Auswahl fest. Knopf unten rechts in der Vorschau.
- **Wie:** `MainViewModel.Edits.cs`. Einstellungen: `HiddenSteps` (Flags, Teil von `SettingsState`,
  also je Bild, mit Rückgängig, beim Export über `OptionsFor`/`EffectiveTarget(s)` beachtet) — die
  Regler behalten ihre Werte, `Mask` nimmt sie nur aus der Kette (Größe, Rauschen, Dunst, Licht, Farbe,
  Gleiche Farbe, Schärfe; Gruppen wie die Überschriften im Tools-Bereich). Bildschritte: jeder
  `Snapshot` kennt jetzt seinen Schritt (`ImageStep`: Art, Name, Kurzinfo, Wiederholen); Zuschnitt,
  Drehen, Spiegeln sind wiederholbar, die KI nicht. KI ausblenden = Bild vor dem ersten KI-Schritt plus
  die späteren Schritte noch einmal (`DocumentFor`, je Datei zwischengespeichert) — nur wenn die KI die
  Größe behielt und danach nur Wiederholbares kam. Zuschnitt/Drehen selbst nicht ausblendbar (die KI
  wurde darauf gerechnet). „Neu anwenden“: ausgeblendete Regler auf null, ausgeblendete KI aus dem
  Bild — ein Schritt; danach zählt die KI vor „Auswahl angewendet“ nicht mehr (`FirstLiveAi`).
- **Grenze:** YuNet findet keine seitlich liegenden Gesichter — erst KI, dann drehen.
- **Nachweis:** `EditListTests` (Export ohne Ausgeblendetes, Regler behalten Wert; Farbgruppen getrennt;
  Neu anwenden setzt genau das Ausgeblendete zurück; Auge ist ein Rückgängig-Schritt). UI-Test:
  KI aus → Gesicht ändert sich (3,7 Stufen), Hintergrund gleich (0); drehen danach → KI bleibt
  ausblendbar; Neu anwenden → „Auswahl angewendet – ohne KI“; Rückgängig → wieder ausgeblendet.

## 27. Drehen bei offenem KI-Ergebnis, Liste nur mit Funktionen, eine Fortschrittsanzeige

- **Entdeckt:** 0.5.23 beta (09.10.2026), vom Benutzer. **Behoben:** 0.5.24 beta.
- **Was:** (1) Nach der KI gedreht: Bild schmal, Gesichter an falscher Stelle — das *offene* Ergebnis
  lag als Vorschau-Ebene im alten Querformat über dem gedrehten Bild. (2) Nach Übernehmen und Drehen
  rechnete ein neuer KI-Lauf auf dem gedrehten KI-Ergebnis (KI auf KI). (3) Die Liste zeigte jede
  Drehung als Zeile; gewünscht ist je Funktion eine Zeile zum Aus-/Einschalten wie in Referenzwerkzeug,
  dazu Haut & Details getrennt von der KI. (4) Drei Fortschrittsanzeigen (Vorschau unten rechts, KI
  unten mitte, Balken im KI-Bereich).
- **Wie:** (1) `RunSetup.Current` = Bild beim Rechnen; ändert es sich (FullBitmap), wird das offene
  Ergebnis verworfen, „Übernehmen“ prüft genau dieses Bild. (2) `MainViewModel.CurrentWithoutAi`
  (Bild vor der KI, spätere Zuschnitte/Drehungen noch einmal) ist Ausgangsbild eines neuen Laufs.
  (3) Liste: KI, Haut & Details, Größe, Rauschen, Dunst, Licht, Farbe, Gleiche Farbe, Schärfe — kein
  Drehen/Zuschnitt. Beim Übernehmen wird die KI zusätzlich ohne Haut & Details gemischt und im
  Schritt behalten (`ImageStep.Plain`, freigegeben mit dem Schritt); Ausschalten nimmt diese Fassung
  plus spätere Schritte. „Neu anwenden“ nur ohne Haut & Details = neuer KI-Schritt ohne sie.
  (4) Eine Anzeige unten mitte (`ActivityText/-Progress/-Cancel`, sonst Vorschau-Schritt mit Anteil),
  mit Balken und — bei der KI — Abbrechen; im KI-Bereich nur noch Ergebnis-Meldungen. Export behält
  seinen eigenen Fortschritt.
- **Nachweis:** UI-Test: Spiegeln bei offenem Ergebnis → verworfen mit Hinweis; KI übernommen,
  gespiegelt → Liste KI / Haut & Details / Dunst; KI an: Gesicht 3,9 Stufen, Haut & Details an:
  1,9 Stufen, Hintergrund 0,01; zentrale Anzeige mit „KI · 50 % · noch ca. 15 s“ und Abbrechen, danach
  „Dunst entfernen – Durchlauf 5 von 6“.
- **Nachtrag 0.5.25 beta:** UI-Test aller Funktionsgruppen am Familienfoto (Größe 800 × 600,
  Rauschen 0,69, Helligkeit +42, Sättigung +66, Gleiche Farbe „Farbe“ +42, Schärfen 1,05): alle sechs
  erscheinen in der Liste mit Kurzinfo; jedes Auge aus ändert das Bild, wieder an → identisch (0).
  Die Liste hat höchstens neun Zeilen (je Funktion eine); bei kleinem Fenster scrollt sie
  (`ScrollViewer`, höchstens 330 px) statt oben abgeschnitten zu werden.

## 28. Rauschen getrennt nach Helligkeit und Farbe (statt ImageMagick-Wavelet)

- **Gebaut:** 0.5.26 beta (09.10.2026), Wunsch des Benutzers: eigene Gruppe Rauschen mit Reglern für
  normales Rauschen und Farbrauschen, Details möglichst erhalten.
- **Wie:** `Core/Imaging/Denoiser.cs` im 32-Bit-Puffer, in Y / Cb = B − Y / Cr = R − Y. Farbrauschen:
  Guided Filter (He u. a. 2010) auf Cb und Cr, Führung = Helligkeit, zwei Stufen (3 und 10 px im
  Export; feine Stufe mit der Stärke, grobe mit Stärke²), Toleranz aus der gemessenen Farb-Streuung.
  Helligkeitsrauschen: Non-Local Means (5 × 5-Umgebung, Suchfenster 9 × 9), Filterbreite je Punkt aus
  dem Kornprofil der KI-Körnung (`FaceDetails.MeasureGrain` auf genau diesem Bild) — dunkle, verrauschte
  Stellen mehr, saubere fast nichts. Radien mit dem Vorschau-Maßstab wie beim Schärfen. Ersetzt
  `ImageOps.Denoise` (Wavelet) in Export, Vorschau und KI-Look. Regler 0…100 % in der neuen Gruppe
  RAUSCHEN oben in den Tools (vorher „Schärfen & Rauschen“ im Format-Bereich, jetzt nur „Schärfen“);
  Liste „Bearbeitungen“: Rauschen und Farbrauschen getrennt ausschaltbar.
- **Messung** (Foto 2012, 2592 × 1944, Release): Rauschen ~3,7 s, Farbrauschen ~0,6 s, Vorschau 2048 px
  beides ~2,4 s. Optisch: Korn auf Haut und Hintergrund weg, Wimpern, Augen, Mützenrand scharf; bei
  100 % Rauschen wirkt Haut glatter — um 50 % für Handyfotos.
- **Nachweis:** `DenoiserTests` (eben: Rauschen < halb, feine Streifen näher am Original; Farbe:
  Farbrauschen < 30 %, Helligkeit unverändert; 0 = unverändert). UI-Test: beide Zeilen in der Liste,
  Auge aus ändert das Bild, wieder an → identisch.
- **Offen:** Helligkeits-NLM beschleunigen (gleiche Differenzen für ±Verschiebung teilen).

## 29. Reihenfolge der Kette: Gleiche Farbe vor der Kurve, Entrauschen vor dem Vergrößern

- **Gebaut:** 0.5.27 beta (09.10.2026), Frage des Benutzers, ob alle Filter in der bestmöglichen
  Reihenfolge laufen (Beispiel: erst schärfen, dann entrauschen wäre falsch — das war schon richtig).
- **Was falsch war:**
  1. Gleiche Farbe (Ausgleichen, Farbe, Luminanz) lief in `Adjustments.Apply` nach der
     Helligkeit/Kontrast-Kurve. Ein Farbstich wird von der S-Kurve je Kanal verbogen; danach lässt er
     sich nicht mehr sauber neutralisieren. Jetzt: Tiefen/Lichter → Gleiche Farbe → Kurve →
     Dynamik/Sättigung → Färben (wie Weißabgleich vor der Tonkurve in RAW-Programmen).
  2. Beim Vergrößern wurde erst vergrößert, dann entrauscht — das Korn war dann breiter als die
     NLM-Umgebung. Jetzt `ImageExporter.NoiseFirst`: bei Rauschen > 0 und Zielgröße > Original zuerst
     entrauschen (Maßstab 1), dann vergrößern, danach der übrige Look. Beim Verkleinern bleibt es wie
     bisher (kleiner = schneller). Vorschau Stufe 2 und `Plan()` folgen derselben Regel.
  3. Tools-Tab und Liste „Bearbeitungen“ in der Reihenfolge der Kette: Rauschen → Dunst →
     Gleiche Farbe → Licht → Farbe (Schärfen bleibt im Format-Bereich, läuft zuletzt).
- **Nachweis:** `ChainOrderTests` (grauer Farbstich mit Kontrast 60 + Ausgleichen 100 bleibt neutral;
  Vergrößern = erst `RenderLook`, dann `Resize`). Abnahme 0.5.27-beta gegen 0.5.0: ToolsOhneDunst
  (Wärme + Kontrast) ΔE 0,04–0,05, sonst ≈. Die ✗ bei RauschenSchaerfen (Stufen) kommen vom neuen
  Entrauscher aus 0.5.26 (glättet flache Stellen stärker), die bei KombiPanorama (Ferne) aus den
  Dunst-Änderungen seit 0.5.0 — beide Fälle nutzen weder Gleiche Farbe noch Vergrößern.
- **Meilenstein 0.5.28 (09.10.2026):** Stand 0.5.27 beta — Rauschen und Farbrauschen (Non-Local Means,
  helligkeitsgeführter Farbfilter) und die neue Reihenfolge der Kette vom Benutzer als gut befunden.
  Hinweis aus der Prüfung: Die Vorschau rechnet höchstens 2048 px; beim Hineinzoomen (120 %) wirkt schon
  ein kleiner Reglerwert wie Weichzeichnen, weil die Anzeige vom Original auf die Vorschau wechselt —
  nicht der Filter. Angeboten: sichtbaren Ausschnitt beim Zoomen in voller Auflösung rechnen.

## 30. Frei drehen: drei Fallen beim Bau (Rahmen schrumpfte, Winkel nach Rückgängig, Regler weg)

- **Entdeckt:** beim Bau von 0.5.29 beta (10.10.2026, auf 0.5.28 `fa9aa32`), im unsichtbaren Testfenster.
  **Behoben:** 0.5.29 beta, vor der Auslieferung.
- **Funktion:** Neues Symbol neben dem Auswahlrahmen, Regler unten (−180 … +180°, Zehntelgrad). Die
  Vorschau dreht live; der Rahmen ist bei jedem Winkel das größte Rechteck im Seitenverhältnis des Bildes,
  das ganz im gedrehten Bild liegt (`RotationGeometry.LargestInside`), danach verschieb- und verkleinerbar,
  stößt aber an die schrägen Kanten. „Drehen und zuschneiden“ (Enter, Doppelklick) = ein Schritt
  `ImageOps.RotateAndCrop`; Esc / „0°“ verwerfen. Kurz eingeblendetes Gitter beim Drehen.
- **Falle 1 – Rahmen wurde beim Verschieben schmaler:** Stieß der Rahmen an die schräge Kante, wurde bei
  jedem Mausschritt auf ganze Pixel *nach innen* gerundet (links aufrunden, rechts abrunden) – jedes Mal
  1 px weniger (617 → 597 px nach einem Zug). Behoben: Verschieben rundet nur die Position, und zwar zurück
  zur gültigen Lage (`CropView.MoveInRotated`); dazwischen liegt alles im konvexen gedrehten Bild.
  **Nicht tun:** beim Verschieben `RoundInside` benutzen.
- **Falle 2 – Winkel und Rahmen nach Rückgängig:** Der Winkel steht im `SettingsState` (Gruppe „Auswahl“),
  damit Strg+Z auf „Drehen und zuschneiden“ Bild, Winkel *und* Rahmen zurückholt. `ApplySettings` setzt den
  Winkel **vor** der Auswahl – umgekehrt setzt die Winkeländerung danach den automatischen Rahmen und
  überschreibt den wiederhergestellten. `ApplySourceSize` (neuer Bildstand) setzt Winkel 0 vor `CropSelection = null`.
- **Falle 3 – Regler nach Rückgängig unsichtbar:** Das Werkzeug wird beim Übernehmen geschlossen; nach Strg+Z
  stand die Drehung wieder an, aber ohne Regler. Behoben: Steht ein Winkel an, öffnet sich das Werkzeug
  (`OnRotationAngleChanged`).
- **Weitere Regeln:** Positiver Winkel = im Uhrzeigersinn, in Vorschau (Avalonia `Matrix.CreateRotation`) und
  ImageMagick gleich – Test `RotateAndCrop_PositiveIsClockwise`. Der Rahmen gilt im ungedrehten
  Koordinatensystem um die Bildmitte; `RotateAndCrop` rechnet ihn auf die nach dem Drehen größere Fläche
  um (Mitte bleibt Mitte). Automatischer Rahmen mit 1 px Abstand (Kantenpixel sind beim Drehen geglättet).
  Ohne Transparenz geladene Bilder kommen ohne Alphakanal zurück. Vorher/Nachher wird nur ungedreht gezeichnet.
- **Wo:** `Core/Imaging/RotationGeometry.cs`, `Core/Imaging/ImageOps.cs` (`RotateAndCrop`),
  `Ui/Controls/CropView.cs` (`RotationAngle`, `AutoFrame`, `KeepInRotated`, `MoveInRotated`, `RenderGrid`),
  `Ui/ViewModels/MainViewModel.Editing.cs` (`RotationTool`, `RotationAngle`, `CropToSelection`),
  `MainViewModel.History.cs` (`SettingsState.RotationAngle`), `Views/MainView.axaml`.
- **Test/Nachweis:** `RotationTests` (Rahmen passt genau und 1 % größer nicht mehr, schrumpft und wächst
  wieder, Ecken ohne leeren Hintergrund – mit Gegenprobe, Drehrichtung). Unsichtbares Testfenster mit
  Testbild 1200 × 800 (schiefer Horizont): −8° → Rahmen 998 × 664, übernommen → Bild 998 × 664, alle vier
  Ecken Bildfarbe; Strg+Z → Bild, −8°, Rahmen und Regler zurück; Rahmen per Testmaus verkleinert (617),
  verschoben (bleibt 617, stößt an), links aufgezogen bis zur Kante – immer ganz im gedrehten Bild.

## 31. Entzerren: Viereck mit freien Ecken – Bauweise und Fallen

- **Neu in:** 0.5.31 beta (10.10.2026). Kein gemeldeter Fehler; Entscheidungen und Fallen beim Bau.
- **Funktion:** Symbol neben „Frei drehen“. Der Rahmen hat vier frei verschiebbare Ecken (Griffe auch an den
  Kantenmitten, Innenfläche verschiebt das Ganze), außen abgedunkelt – man sieht gleich, wo die Ränder des
  Ergebnisses liegen (Wunsch des Benutzers statt zweitem Schritt mit automatischem Rahmen). Man legt die
  Ecken auf etwas, das rechteckig ist (Blatt, Bild an der Wand, Fenster); „Entzerren und zuschneiden“
  (Enter, Doppelklick) macht genau den Inhalt des Vierecks zum geraden Rechteck – ein Schritt, Strg+Z.
  Lupe beim Ziehen einer Ecke, Pfeiltasten verschieben die zuletzt gezogene Ecke, Esc bricht ab.
- **Rechnen:** `ImageOps.Straighten` = ImageMagick `Distort Perspective` mit den vier Paaren Ecke → Rechteckecke
  und `Viewport` genau in Zielgröße (keine große Zwischenfläche). Zielgröße = Mittel der gegenüberliegenden
  Kanten (`PerspectiveGeometry.TargetSize`) – das wahre Seitenverhältnis lässt sich aus vier Punkten allein nicht
  bestimmen. Das Drittel-Gitter im Viereck ist perspektivisch richtig (`PointAt`, Homographie Einheitsquadrat →
  Viereck), nicht linear gemittelt.
- **Falle – gespiegeltes Ergebnis:** Ein Viereck, dessen Ecken andersherum umlaufen (z. B. links und rechts
  vertauscht, aber nicht überkreuzt), ist konvex – ImageMagick hätte daraus ein *gespiegeltes* Bild gemacht.
  `PerspectiveGeometry.IsValid` verlangt deshalb den Uhrzeigersinn (Bildschirm, y nach unten: Kreuzprodukt
  aller Ecken positiv); sonst rote Rahmenlinie und Hinweis in der Leiste, Übernehmen gesperrt.
- **Falle – Test ohne Alphakanal:** `Drawables.Draw` legt am Testbild einen Alphakanal an; „ohne Transparenz
  geladen → ohne Alphakanal zurück“ lässt sich nur mit `Alpha(Off)` am Testbild prüfen.
- **Vorschau perspektivisch zeichnen geht:** Avalonia/Skia zeichnet Bitmaps mit perspektivischer Matrix
  (`new Matrix(m11, m12, m13, …, m33)` mit M13/M23 ≠ 0) – im unsichtbaren Test geprüft. Für die Viereck-Bedienung
  nicht nötig, aber die Möglichkeit für eine spätere Live-Vorschau des Ergebnisses.
- **Werkzeuge schließen sich gegenseitig aus:** Drehen an → Entzerren aus und umgekehrt; ein neuer Bildstand
  (`ApplySourceSize`) schließt Entzerren, die Ecken gehören zum Bild davor.
- **Wo:** `Core/Imaging/PerspectiveGeometry.cs`, `Core/Imaging/ImageOps.cs` (`Straighten`),
  `Ui/Controls/CropView.cs` (`Quad`, `HitQuad`, `ShiftQuad`, `RenderQuad`, `DrawLoupe`, `OnQuadKey`),
  `Ui/ViewModels/MainViewModel.Editing.cs` (`PerspectiveTool`, `PerspectiveQuad`, `CropToSelection`),
  `Views/MainView.axaml`.
- **Test/Nachweis:** `PerspectiveTests` (Viereck-Prüfung inkl. gespiegelt/überkreuzt/eingedellt, Zielgröße,
  Homographie, schräges Blatt füllt das Ergebnis und die Markierung liegt oben links, Gegenprobe mit zu großem
  Viereck). Unsichtbares Testfenster: Blatt auf Holztisch 1200 × 800, Ecke unten links per Testmaus auf die
  Blattecke gezogen (Lupe sichtbar) → Ergebnis 528 × 427, Linien waagerecht, alle Ecken Papierfarbe; Strg+Z →
  Original.

## 32. Standardformate (A4 …) und zwei Fallen an kleinen Knöpfen

- **Neu in:** 0.5.32 beta (10.10.2026). **Fehler:** Winkel-Symbol ragte über den runden Knopf (vom Benutzer
  gesehen, eingeführt 0.5.29 beta `cdcc02f`), behoben 0.5.32 beta.
- **Funktion:** Knopf mit Blatt-Symbol über der Kette (Größe → Einheit). Menü mit DIN-Formaten, Fotoabzügen und
  US-Formaten. Auswahl: Einheit mm, Hoch-/Querformat wie das Bild, bei geschlossener Kette ganz hinein
  (eine Kante füllt das Format, `PaperFormats.Fit`), sonst genau das Format. Ziel des Benutzers: gleich passend
  drucken, z. B. 65 × 110 mm → A4 = 175,2 × 297 mm. Die DPI bleibt; bei „Nur Metadaten ändern“ ändert sich die
  DPI statt der Pixel. Muss das Bild dafür größer werden, wird „Nicht vergrößern“ ausgeschaltet – sonst käme
  ein kleines Bild nie auf das Format. Ein Klick = ein Rückgängig-Schritt (Turns im Verlauf).
- **Hinweis Druck:** Der Druckdialog lässt standardmäßig 5 mm Rand; ein Bild, das A4 ganz ausfüllt, wird dort
  um diese Millimeter verkleinert (sonst würde es angeschnitten). Randlos: Rand im Druckdialog auf 0.
- **Falle 1 – Symbol zu groß:** Der Winkel war als Pfad mit Bogenbefehl `A` gezeichnet; trotz `Width/Height = 18`
  und `Stretch = Uniform` ragte er über den 38-px-Knopf. Neu mit Kurve `Q` und kompakt um die Mitte (4…20).
  **Nicht tun:** in den runden Knöpfen Pfade mit `A` (Bogen) – nach Symboländerungen den Knopf vergrößert
  ansehen.
- **Falle 2 – „genauso groß wie die Kette“:** Die Kette ist im Einstellungsbereich kleiner als im Grundstil
  (`.settings ToggleButton.chain`: 34 × 26 statt 42 × 36). Der Format-Knopf braucht dieselbe Regel
  (`.settings Button.paper`), sonst steht ein größerer Knopf über der Kette. Maße im Testfenster ausgelesen
  (`Bounds`), nicht geschätzt.
- **Wo:** `Core/Imaging/PaperFormats.cs`, `Ui/ViewModels/MainViewModel.Size.cs` (`ApplyPaperFormat`),
  `Views/MainView.axaml` (Größe → Einheit), `Styles/Controls.axaml` (`Button.paper`).
- **Test/Nachweis:** `PaperFormatTests` (quer/hoch/quadratisch, schlank/breit, Kette offen, Liste eindeutig).
  Unsichtbares Testfenster: Bild 184 × 312 px bei 72 DPI (64,9 × 110,1 mm) → A4: 175,2 × 297 mm,
  496 × 842 px, „Nicht vergrößern“ aus; Knöpfe Kette und Format beide 34 × 26.
- **Nachtrag 0.5.33:** Standardformate um A2, A1 und A0 erweitert (Menü jetzt A0 … A7, DIN lang, Fotoabzüge,
  US); Test `Din_GoesFromA0ToA7`.
- **Meilenstein 0.5.33 (10.10.2026):** Stand 0.5.32 beta plus A0–A2 – Frei drehen mit automatischem Rahmen,
  Entzerren mit frei verschiebbaren Ecken und Standardformate vom Benutzer als gut befunden. Tag `v0.5.33`,
  Sicherung `../ImageConverter-0.5.33-Sicherung.zip`.

## 33. Weißabgleich mit drei Pipetten (Neutral, Schwarz, Weiß)

- **Neu in:** 0.5.34 beta (10.10.2026). Kein gemeldeter Fehler; Bauweise und eine Falle beim Testen.
- **Funktion:** Gruppe „Weißabgleich“ in der Rubrik Standard unter Schärfen. Drei Pipetten, jede allein oder
  zusammen; jeder weitere Klick mit derselben Pipette wird gemittelt (Mittel in linearem Licht, nach Anzahl
  gewichtet – `WbSample.Add`), die Pipette bleibt dafür an (Esc beendet). Strg+Z nimmt den letzten Klick zurück,
  × löscht eine Pipette, „Zurücksetzen“ alle. Messstellen als kleine Markierung (N, S, W), beim Zielen die Lupe
  vom Entzerren mit eingezeichnetem Messfeld. Hinweistext je Pipette, was man anklicken soll.
- **Rechnen (`WhiteBalance`):** in linearem Licht, je Kanal. Schwarz/Weiß setzen die Tonwert-Enden (die Stelle wird
  ganz schwarz bzw. weiß, Stich in Schatten/Lichtern weg). Neutral macht die Stelle farblos bei gleicher
  Helligkeit (Rec. 709): mit Weißpunkt über den Exponenten je Kanal (Weiß bleibt weiß, wie die Grau-Pipette in
  „Tonwerte“), ohne Weißpunkt über eine Verstärkung je Kanal (physikalisch: anderes Licht). Gemessen wird ein
  Feld von ±max(4, kurze Seite/300) Pixeln; ausgefressene Pixel (≥ 0,985) zählen bei Neutral und Weiß nicht –
  ist alles ausgefressen, Hinweis statt Messung.
- **Stelle in der Kette:** ganz am Anfang von `Adjust.Prepare` (vor Dunst, Licht, Farbe) – er wirkt auf dasselbe
  Bild, an dem die Pipetten messen (`CurrentImage`), und alles Weitere arbeitet mit richtigen Farben. Weil die
  Vorschau `Prepare` zwischenspeichert, stehen die Messungen im `PrepareKey`.
- **Überall mitgeführt:** `Adjustments` (WbNeutral/WbBlack/WbWhite), `SettingsState` (Rückgängig, für alle/einzeln –
  die Messwerte gelten auch für andere Bilder, gleiches Licht), Ausblenden (`EditSteps.WhiteBalance`, Zeile
  „Weißabgleich“ in der Liste vor Dunst), Look (KI rechnet ihn ein → danach neutral). Markierungen gehören zum
  Bild: neuer Bildstand (`ApplySourceSize`) räumt sie ab, die Messwerte bleiben. Pipette an → Drehen und Entzerren
  aus (gemessen wird am ungedrehten Bild).
- **Falle beim Testen:** Ein Testbild mit verschieden starken Stichen auf Grau, Schwarz und Weiß ist kein Foto –
  dann kann Neutral allein das Weiß nicht neutral machen und der Test „scheitert“ an falschen Daten. Testbilder
  deshalb als neutrale Flächen unter *einer* Lichtfarbe erzeugen (gleicher Faktor je Kanal in linearem Licht).
- **Wo:** `Core/Imaging/WhiteBalance.cs`, `Core/Imaging/Adjustments.cs` (Felder, `PrepareKey`, `Prepare`),
  `Ui/ViewModels/MainViewModel.WhiteBalance.cs`, `MainViewModel.History.cs`, `.Tools.cs`, `.PerImage.cs`, `.Edits.cs`,
  `.Look.cs`, `.Size.cs`; `Ui/Controls/CropView.cs` (`PickActive`, `PickCommand`, `PickMarkers`, `DrawLoupe`),
  `Ui/Controls/PickMarker.cs`; `Views/MainView.axaml`.
- **Test/Nachweis:** `WhiteBalanceTests` (Neutral allein: Grau, Weiß und Schwarz farblos, Helligkeit bleibt;
  Schwarz+Weiß; alle drei; nur Weiß; Ausgefressenes; Mitteln nach Anzahl; durch die ganze Kette). Unsichtbares
  Testfenster mit Testmaus: Zimmer unter warmem Licht, Neutral 2×, Schwarz, Weiß → Wand 161/161/160, Weiß 255,
  Schwarz 0; Liste „Neutral 2× · Schwarz · Weiß“; Strg+Z Klick für Klick samt Markierungen; × schaltet ab.
- **Nachtrag 0.5.35:** Hinweis und Tooltip zu „Neutral“ verständlicher (Rückfrage des Benutzers, welches Grau):
  Es kommt nicht auf hell oder dunkel an, sondern darauf, dass die Stelle in Wirklichkeit farblos ist; genannt
  sind gute (Asphalt, Beton, graue Wolke, weißes Papier im Schatten) und ungeeignete Stellen (grelles Weiß, fast
  Schwarzes, Beige, Holz).
- **Meilenstein 0.5.35 (10.10.2026):** Stand 0.5.34 beta plus Hinweistext – Weißabgleich mit drei Pipetten vom
  Benutzer als passend befunden. Tag `v0.5.35`, Sicherung `../ImageConverter-0.5.35-Sicherung.zip`.

## 34. Luminanz mit Schwellwert (Gleiche Farbe)

- **Neu in:** 0.5.36 beta (10.10.2026, Commit „Luminanz mit Schwellwert“). Kein gemeldeter Fehler; Bauweise
  und eine Falle beim Testen.
- **Wunsch:** Beim Aufhellen mit Luminanz dunkle Bereiche (Vordergrund) heller machen, ohne dass helle Bereiche
  (Himmel) weiter aufhellen – Regler „Schwellwert“ unter Luminanz.
- **Rechnen:** `Adjust.Limited`: unterhalb der Grenze dieselbe weiche Schulter wie ohne Grenze, nur auf die Grenze
  statt auf Weiß gestaucht (`limit · Shoulder(L · k / limit)`), darüber unverändert; das Maximum mit dem
  Ausgangswert hält die Kurve stetig und steigend (keine Umkehr der Tonwerte). 100 % = keine Grenze = genau die
  alte Rechnung. Wirkt nur beim Aufhellen (Regler sonst gesperrt, `Normalized` setzt ihn auf 100).
- **Grenze der Lösung:** Rein je Pixel – Töne knapp unter der Grenze rücken zusammen (wirkt dort flacher), und ein
  helles Detail im dunklen Vordergrund wird wie der Himmel behandelt. Rückmeldung des Benutzers: Auf die Bäume
  allein lässt es sich so nicht begrenzen → bereichsbezogene Lösung geplant (nach dem Entfernen, eigene Beta).
- **Falle beim Testen:** `Adjust.Apply(byte[] …)` rundet mit Dither (±1 Stufe Rauschen) – ein Test auf „steigend“
  über die 8-Bit-Ausgabe meldete scheinbare Umkehrungen. Monotonie auf den Float-Werten prüfen.
- **Wo:** `Core/Imaging/Adjustments.cs` (`MatchLimit`, `Limited`, `Normalized`), `Ui/ViewModels/MainViewModel.Tools.cs`,
  `.History.cs`, `.Edits.cs`, `.Look.cs`, `.PerImage.cs`; `Views/MainView.axaml` (Gleiche Farbe).
- **Test/Nachweis:** `AdjustTests.MatchLimit_BrightensOnlyBelowThreshold_AndStaysMonotonic`; Probe an einem
  Panorama mit dunklem Vordergrund (Luminanz 180 %, Grenze 40–50 %: Himmel bleibt, Vordergrund heller).

## 35. Entfernen (Wasserzeichen, Schrift, Störendes) mit LaMa und MI-GAN

- **Neu in:** 0.5.37 beta (10.10.2026). Kein gemeldeter Fehler; Bauweise und vier Fallen beim Bau.
- **Funktion:** Eigene Rubrik „Entfernen“ unter „KI“. Mit dem Pinsel (Größe in Bildschirmpixeln, bleibt beim Zoomen
  gleich) über das Störende malen, „Entfernen“ – ein Füllmodell malt die Stellen passend zur Umgebung neu. Jedes
  Entfernen ist ein Rückgängig-Schritt (`ImageStepKind.Retouch`, Zeile „Entfernt“ in der Liste, nicht
  ausschaltbar). Die Markierung bleibt danach stehen (ausgeblendet): Ein neuer Versuch mit anderem Modell oder
  ergänzter Markierung rechnet vom Bild vor dem letzten Entfernen (Wunsch des Benutzers) und ersetzt die Zeile;
  Strg+Z führt zum vorigen Ergebnis. „Neue Markierung“ beginnt beim aktuellen Bild. Modelle: LaMa (Apache-2.0,
  208 MB, gründlich) und MI-GAN (MIT, 28 MB, schnell), Download in den Einstellungen, dort jetzt in Gruppen
  (Bilder verbessern, Gesichter, Gesichtserkennung, Entfernen).
- **Rechnen (`Inpainter`):** Striche → Maske, um `Grow` (≥ 3 px) vergrößert. Stücke: je Strich ein Rahmen, nahe
  zusammengefasst, lange schmale geteilt (höchstens doppelt so lang wie breit). Je Stück ein Ausschnitt etwa
  doppelt so groß, auf 512 × 512 skaliert, gefüllt, zurückskaliert und mit weichem Rand eingesetzt; außerhalb der
  Markierung bleibt jeder Pixel exakt (volle Tiefe, Alpha, Profile). LaMa braucht die Lücke im Eingang
  geschwärzt (sonst malt es das Alte durchscheinend nach); MI-GAN-Pipeline: Bytes, Maske 255 = bleibt.
- **Falle 1 – Kopie wird schwarz:** In eine frische Kopie (`Clone`) geschriebene Pixel (Pixel-API, sicher wie
  unsafe) machten in Magick.NET 14.17.2 alles außerhalb des geschriebenen Bereichs schwarz – die Kopie teilt ihre
  Pixel noch mit dem Original. Abhilfe: nach dem Kopieren `Evaluate(Add, 0)` (jeder Pixel einmal durchgerechnet),
  danach schreiben. **Nicht tun:** direkt in eine frische Kopie Pixel schreiben. Gegenprobe: ohne `Evaluate`
  schlägt `FillsMarkedArea_AndLeavesEverythingElseUntouched` fehl (Pixel 1,0 → schwarz).
- **Falle 2 – Composite ändert Unberührtes:** `Composite(…, Copy/SrcAtop)` veränderte auch Pixel mit Deckkraft 0
  um ±2 Stufen. Deshalb über die Pixel-API mischen.
- **Falle 3 – Nähte und Reste an Stückgrenzen:** Füllte ein Stück auch die Markierung direkt am Rand seines
  Ausschnitts, fehlte dort die Umgebung (Naht); schmale ungefüllte Streifen malte MI-GAN ins Innere weiter.
  Jetzt übernimmt jedes Stück nur den inneren Teil (¼ der kurzen Seite Abstand, außer am Bildrand); den Rand füllt
  das Nachbarstück.
- **Falle 4 – heller Saum:** Lanczos verschmiert beim Verkleinern das Zeichen bis zu drei Modellpixel über den
  Maskenrand; dieser Saum lag ungelöscht neben der Lücke, LaMa setzte ihn als Band fort (Rest eines Balkens im
  Ergebnis). Abhilfe: Modellmaske um die Filterreichweite vergrößern (`Dilate`, 3 Modellpixel, beim Vergrößern
  entsprechend mehr). Gefunden über gespeicherte Ein-/Ausgaben je Stück.
- **Wo:** `Core/Ai/Inpainter.cs`, `Core/Ai/OnnxModels.cs` (`OnnxInpaintModel`, `RunBytes`), `Core/Ai/AiModels.cs`
  (`Lama`, `Migan`, `AiModelKind.Inpaint`); `Ui/Ai/RetouchViewModel.cs`, `RetouchPanel.axaml`, `AiModelsView.axaml`
  (Gruppen), `AiViewModel.cs` (`ModelGroups`, geteilte Sitzungen); `Ui/Controls/CropView.cs` (Pinsel);
  `MainViewModel.Editing.cs` (`ApplyRetouchAsync`), `.Edits.cs` (Zeile „Entfernt“); `Views/MainView.axaml`.
- **Test/Nachweis:** `InpaintTests` (Unberührtes exakt gleich, 16 Bit; jeder markierte Pixel gefüllt auch über
  Stückgrenzen; Stücke zusammenfassen/teilen; Ausschnitt im Bild; dünne Linien in der Maske; Alpha bleibt).
  Unsichtbares Testfenster ohne Netz mit Testmaus: zwei „Wasserzeichen“ übermalt, LaMa (≈ 15 s, 7 Stücke) entfernt
  beide sauber, MI-GAN (≈ 3 s) vom Bild davor, Liste ersetzt die Zeile, Strg+Z → LaMa-Ergebnis, Strg+Z → Original.

## 36. Luminanz: Schwellwert nach Bereichen und „Ferne schonen“

- **Neu in:** 0.5.38 beta (10.10.2026). Folgt auf #34: Rückmeldung des Benutzers zu 0.5.36 – mit 200 % und ohne
  Schwellwert sind die Bäume gut zu sehen, aber hinten im Bild ist alles überstrahlt; der Schwellwert ließ sich
  nicht so einstellen, dass nur die Bäume heller werden.
- **Warum der alte Schwellwert nicht reichte:** Er entschied je Pixel. Dunkles durfte nur bis zur Grenze hell
  werden (Bäume blieben dunkler als ohne Grenze), und alles gleich Dunkle (Talgrund, Hang) wurde gleich behandelt.
- **Untersucht** (Testprogramm, Panorama mit dunklem Vordergrund, Luminanz 200 %; Messwert: Aufhellung heller
  Pixel neben dunklen, Oklab-L): ohne Grenze +0,31; Maske aus weichgezeichneter Helligkeit +0,19 (Hof am Himmel);
  dieselbe kantentreu nachgeführt +0,18; mehrere Durchläufe grob → fein +0,20; Rolling Guidance (Durchläufe wie
  beim Dunst) +0,16, Maske klumpig; **Helligkeit kantentreu eingeebnet (selbstgeführter Guided Filter) +0,003** –
  gewählt. Dazu Schutz sehr heller Pixel (Himmel zwischen Ästen).
- **Ferne schonen:** Helligkeit allein trennt dunkle Ferne nicht vom dunklen Vordergrund. Dunstkarte wie beim
  Dunst-Regler (dunkelster Farbkanal in der Umgebung): Vordergrund hat wirklich dunkle Farbanteile, dunstige Ferne
  nicht. Regler 0–100 %, Vorgabe aus. **Grenze:** Wo Ferne und Vordergrund gleich hell aneinanderstoßen, bleibt ein
  schmaler heller Saum (Übergang der Karte). Kleinere Radien machten ihn schmaler; die fertige Karte noch einmal
  kantentreu nachzuführen machte ihn breiter (dort gibt es keine Kante zum Ausrichten) – verworfen.
- **Bauweise:** Die Karten (Bereichshelligkeit, Dunst) rechnen einmal in kleiner Auflösung (längste Kante 1200) im
  teuren, zwischengespeicherten Teil (`Adjust.Prepare` → `Prepared.Regions`); im `PrepareKey` steht nur, *ob* sie
  gebraucht werden – Schwellwert und Ferne wirken erst in `Apply` und bleiben flüssig. 28 MP: Karten 88 ms,
  Anwenden 189 ms. `Adjust.Limited` (Grenze je Pixel) entfallen.
- **Wo:** `Core/Imaging/LuminanceRegions.cs`, `Core/Imaging/Adjustments.cs` (`MatchDistance`, `NeedsRegions`,
  `PrepareKey`, `Prepared`, `Apply`); `Ui/ViewModels/MainViewModel.Tools.cs`, `.History.cs`, `.Edits.cs`, `.Look.cs`,
  `.PerImage.cs`; `Views/MainView.axaml` (Gleiche Farbe: Schwellwert, Ferne).
- **Test/Nachweis:** `AdjustTests.MatchLimit_LiftsDarkRegionsFully_AndLeavesBrightOnesIncludingTheEdge`,
  `MatchLimit_ProtectsBrightGapsInsideDarkRegions`, `MatchDistance_SparesHazyRegions_ButLiftsSaturatedForeground` –
  Gegenprobe: ohne Bereichskarten schlagen alle drei fehl. Unsichtbares Testfenster: Regler, Liste
  („Schwellwert 40 % · Ferne schonen 100 % · Luminanz +80“), Rückgängig.

## 37. Luminanz: heller Saum am Übergang Ferne → Vordergrund (Pyramide, Dunstkarte ohne Minimumfilter)

- **Entdeckt in:** 0.5.38 beta (Commit `4ef443b`, 10.10.2026), beim eigenen Test am Panorama; dem Benutzer bei der
  Übergabe genannt. **Behoben in:** 0.5.39 beta.
- **Was:** Mit „Ferne schonen“ hoch lag um Büsche und Baumkanten vor dem dunstigen Tal ein heller Saum.
- **Recherche (Stand der Technik):** Belichtungsverschmelzung nach Mertens über eine Laplace-Pyramide – so arbeitet die
  Tonwertverarbeitung in Googles HDR+; für ein einzelnes Bild mit künstlichen Belichtungen (Extended Exposure Fusion,
  WACV 2020). Entfernung per KI-Tiefenkarte: Adobe Referenzwerkzeug seit April 2026 („Tiefenbereichsmaske“ für jedes Foto).
- **Ursache (zwei Teile):** (1) Gemischt wurde Pixel für Pixel in einer Größenstufe – ein weicher Maskenrand wird so
  als Saum sichtbar. (2) Hauptursache: Die Dunstkarte nahm den dunkelsten Kanal *in der Umgebung* (Minimumfilter wie
  beim Dark Channel). Der trug die dunklen Werte der Büsche ins Tal; dort galt das Tal als nah und wurde aufgehellt.
- **Behoben:** Original und aufgehellte Fassung werden über eine Laplace-Pyramide gemischt (`PyramidBlend`), gröbste
  Stufe 1/24 der längsten Kante (relativ → Vorschau und Export gleich). Weil die Pyramide grobe Helligkeit auch über
  echte Kanten mischt (der Himmel neben dem Baum wurde im Test weiß – bekannter großflächiger Hof der Verschmelzung),
  sind Schwellwert und Schutz heller Pixel eine **Obergrenze**; frei weich gemischt wird nur „Ferne schonen“. Dunstkarte
  ohne Minimumfilter (dunkelster Kanal je Pixel, kantentreu geglättet). Der Saum ist danach am Baum und am Hang deutlich
  schwächer, über den Büschen bleibt ein leichter Schimmer – die eigentliche Lösung ist eine Tiefenkarte (#38).
- **Nicht tun:** Dunst-/Entfernungskarte mit Minimumfilter für scharfe Übergänge benutzen; Pyramide ohne Obergrenze
  (Himmel hellt neben dunklen Bereichen auf); Pyramide bis zur kleinsten Stufe (Maske über das halbe Bild gemittelt).
- **Kosten:** 28 MP: Karten ≈ 80 ms, Anwenden ≈ 0,6 s (vorher 0,2 s).
- **Wo:** `Core/Imaging/PyramidBlend.cs`, `Core/Imaging/LuminanceRegions.cs` (`Weight` liefert Gesamt und Obergrenze,
  Dunstkarte), `Core/Imaging/Adjustments.cs` (`LiftByRegions`, `Apply`).
- **Test/Nachweis:** `AdjustTests.PyramidBlend_GivesSoftTransition_ButKeepsImageEdgesSharp`; die drei Tests aus #36
  (Himmel bis an die Kante unverändert – schlug mit Pyramide ohne Obergrenze fehl). Sichtvergleich Ausschnitt Tal
  0.5.38 ↔ 0.5.39.

## 38. Luminanz: „Ferne schonen“ mit KI-Tiefenkarte (Depth Anything V2)

- **Neu in:** 0.5.40 beta (10.10.2026). Folgt auf #37 (Wunsch des Benutzers nach der Recherche: beide Teile umsetzen).
- **Funktion:** Ist das Modell „Tiefenkarte – Depth Anything V2 (klein)“ geladen (Einstellungen → KI-Modelle, Gruppe
  Tiefenkarte, 99 MB, Apache-2.0), nimmt „Ferne schonen“ die geschätzte Entfernung statt des Dunstes; ohne Modell
  wie bisher der Dunst, unter dem Regler ein Hinweis mit „Laden …“. Vorbild: Adobe Referenzwerkzeug, Tiefenbereichsmaske
  für jedes Foto (April 2026).
- **Bauweise:** `IDepthEstimator` (Core.Imaging) und `OnnxDepthEstimator` (Core.Ai, lange Seite 518, Vielfaches von 14,
  ImageNet-Normierung, Ausgang 2 %/98 % auf 0 fern … 1 nah). Das KI-Modul meldet es an (`LuminanceRegions.Depth`),
  sobald das Modell im Ordner liegt; der `PrepareKey` enthält, ob eine Tiefenkarte da ist → nach dem Laden rechnet
  die Vorschau neu. Die Karte wird kantentreu an das Bild angelegt (Guided Filter) und ist Teil der **Obergrenze**
  (echte Kanten); der Dunst nicht. Tiefenmodell bleibt offen und schließt keine anderen (`OnnxSessions.StaysOpen`) –
  es rechnet in der Vorschau, auch während ein KI-Lauf sein Modell benutzt. Rechnet immer aus dem Bild vor den
  Einstellungen (Regler, kein Knopf) – nichts häuft sich an; das Modell läuft einmal je Bild.
- **Falle (im eigenen Test gefunden, vor der Auslieferung behoben):** Mit Schwellwert „aus“ und nur „Ferne“ leuchtete
  ein Hof um den Baum in den Himmel – die Obergrenze kam nur vom Schwellwert, die Pyramide mischte die Aufhellung über
  die Kante. Jetzt gehört die Tiefenkarte zur Obergrenze. Gegenprobe: mit der alten Regel schlägt
  `WithDepthMap_DistanceFollowsDepth_AndKeepsFarSideUpToTheEdge` fehl.
- **Durchläufe geprüft (Frage des Benutzers):** zwei bzw. drei schwächere Durchläufe hintereinander, Bereiche jedes
  Mal neu aus dem Zwischenstand – kaum Unterschied zu einem Durchlauf, eher weniger Aufhellung im Vordergrund (er ist
  nach dem ersten schon heller als die Grenze), nur mehr Rechenzeit. Wie schon Rolling Guidance (#36): **keine
  Durchläufe**. Nicht wieder vorschlagen ohne neuen Befund.
- **Modell gewählt:** volle Fassung (fp32, 99 MB, 0,14 s je Bild); die gerundete (int8, 27 MB) gab eine sichtbar
  körnige Karte. Größere Fassungen (Base/Large) sind nur nicht-kommerziell lizenziert.
- **Wo:** `Core/Ai/DepthEstimator.cs`, `Core/Ai/AiModels.cs` (`DepthMap`, `AiModelKind.Depth`), `Core/Ai/OnnxModels.cs`
  (`StaysOpen`), `Core/Imaging/LuminanceRegions.cs` (`IDepthEstimator`, `Depth`, `Weight`), `Core/Imaging/Adjustments.cs`
  (`PrepareKey`); `Ui/Ai/AiViewModel.cs` (`UpdateDepth`, Gruppe), `Ui/ViewModels/MainViewModel.Tools.cs`
  (`ShowDepthHint`), `.Preview.cs` (`RefreshPreview`); `Views/MainView.axaml`.
- **Test/Nachweis:** `LuminanceDistanceTests` (eigene Sammlung, nicht parallel – die Tiefenkarte ist global
  angemeldet): Dunst-Fall und Ersatz-Tiefenkarte (links nah, rechts fern: nur links heller, rechts bis an die Kante
  unverändert; `PrepareKey` ändert sich mit dem Modell). Panorama mit echtem Modell (ohne Netz): Tal und Hang bleiben
  natürlich, Vordergrund heller. Unsichtbares Testfenster: Hinweis ohne Modell an, mit Modell aus; Gruppe in den
  Einstellungen.

## 39. Luminanz: weiße Ränder um Bäume, weißlicher Himmel zwischen Ästen (Durchblicke erkennen)

- **Entdeckt in:** 0.5.40 beta (Commit `5a68ba1`), vom Benutzer gemeldet („Überstrahlungen … die weißen Ränder,
  Säume um die Bäume“); bestätigt mit Lupe (3×) am Panorama. **Behoben in:** 0.5.41 beta.
- **Was:** Zwischen den Nadeln am Baumrand wurde der Himmel weißlich, über dem Tal lag zwischen den Ästen ein
  grauweißer Schleier.
- **Warum:** Die Karten sind bewusst grob (Bereiche 1200 px, Tiefe 518 px). Für sie ist ein Baum ein dunkler, naher
  Fleck *samt seiner Lücken* – was durchscheint, wurde mit aufgehellt. Der feste Schutz heller Pixel (ab Oklab-L 0,62)
  griff beim etwas dunkleren Abendhimmel nicht, beim Tal gar nicht.
- **Wie behoben:** Karte „was liegt dahinter“: Farbe und Helligkeit der nicht aufgehellten Flächen (Himmel, Ferne)
  per normierter Faltung ein Stück in die aufgehellten Bereiche fortgesetzt (Reichweite 1/50 der längsten Kante – je
  Reglerstand einmal in kleiner Auflösung). Ein Pixel ist Durchblick, wenn er (a) heller als sein Bereich ist,
  (b) nicht heller als der Hintergrund (eine Mischung aus Ast und Hintergrund liegt immer dazwischen) und (c) dessen
  Farbe hat (Oklab a/b nah). Nur dieser Anteil bleibt unverändert, auch als Obergrenze in der Pyramide.
- **Verworfen / Fallen:** Einfaches Matting („heller als die lokale Umgebung = Durchblick“) hätte auch sonnige Nadeln
  und Blätter für Durchblicke gehalten – der Busch wäre flach geworden. Erster Entwurf mit Reichweite 1/20 und ohne
  Bedingung (b): Mit Tiefenkarte wurde der ganze sonnige Vordergrund zu wenig aufgehellt, weil die Blätter dieselbe
  grüne Farbe hatten wie die Wiesen im Tal dahinter. **Nicht tun:** Durchblicke nur über Helligkeit erkennen;
  Hintergrund tief ins Innere von Büschen fortsetzen.
- **Kosten:** 28 MP: Karten ≈ 90 ms, Anwenden ≈ 0,8 s (vorher 0,6 s).
- **Wo:** `Core/Imaging/LuminanceRegions.cs` (`For`, `Weighting.Weight`, `AreaWeight`, `BehindReach`, `ChromaSame/Other`),
  `Core/Imaging/Adjustments.cs` (`LiftByRegions`: Gewichtung je Reglerstand, Oklab a/b je Pixel).
- **Test/Nachweis:** `AdjustTests.MatchLimit_KeepsSkyGapsAtTreeEdge_ButLiftsSunlitLeaves` (Himmelslücken dunkler als
  der feste Schutz bleiben, gleich helle gelbgrüne Sonnenflecken werden heller) – Gegenprobe: ohne Erkennung schlägt
  er fehl. Lupe am Panorama vorher/nachher (Baumrand vor Himmel, Ast vor dem Tal), Gesamtbild mit und ohne Tiefenkarte.

## 40. Luminanz: Regler „Zielwert“ (Aufhellen bis zu einer Helligkeit statt mit festem Faktor)

- **Neu in:** 0.5.42 beta (10.10.2026). Wunsch des Benutzers: Überstrahlung z. B. der Wiesen links.
- **Warum:** Luminanz 200 % verdoppelt alles in den gewählten Bereichen – ein Mittelton (Wiese, sonniger Busch) wird
  fast so stark angehoben wie ein dunkler Baum und strahlt über.
- **Wie:** Faktor **je Bereich** (eingeebnete Helligkeit, nicht je Pixel – die Struktur im Baum bleibt):
  `Ziel / Bereichshelligkeit`, begrenzt auf 1 … Luminanz-Faktor. Ein Baum bei 20 % bekommt viel, eine Wiese bei 45 % mit
  Ziel 40 % nichts. „Luminanz“ bleibt unverändert wie in Referenzwerkzeug (Entscheidung des Benutzers: eigener Regler statt
  Umdeuten); Zielwert ganz links = aus.
- **Falle:** Der Regler sollte „Ziel“ heißen – den deutschen Text gab es schon („Ziel“ = Speicherort beim Export,
  englisch „Destination“). Die englische Tabelle wird über den Indexer gefüllt, ein zweiter Eintrag überschreibt den
  ersten **still**; die Export-Beschriftung wäre englisch zu „Target“ geworden, kein Test hätte es gemerkt. Regler heißt
  jetzt „Zielwert“, neuer Test `LocalizationTests.English_HasNoDuplicateKeys` (Gegenprobe: mit doppeltem „Ziel“ rot).
  **Nicht tun:** einen deutschen Text, den es schon gibt, für etwas anderes verwenden.
- **Wo:** `Core/Imaging/Adjustments.cs` (`MatchTarget`, `NeedsRegions`, `LiftByRegions`), `Core/Imaging/LuminanceRegions.cs`
  (`AreaBrightness`); `Ui/ViewModels/MainViewModel.Tools.cs`, `.History.cs`, `.Edits.cs`, `.Look.cs`, `.PerImage.cs`;
  `Views/MainView.axaml` (unter Luminanz).
- **Test/Nachweis:** `AdjustTests.MatchTarget_LiftsDarkAreasToTarget_AndLeavesMidtonesAlone` (Gegenprobe: ohne Zielwert
  rot); Panorama mit Tiefenkarte: sonnige Büsche nicht mehr grell, dunkler Vordergrund gleichmäßig angehoben.
- **Nachtrag 0.5.43:** Luminanz bis 400 % (Wunsch des Benutzers; vorher wie Referenzwerkzeug höchstens 200 %). Sinnvoll vor
  allem mit Zielwert – der Faktor ist dann nur die Obergrenze, tiefe Schatten (8 %) brauchen ×5 bis 40 %. Ohne Zielwert
  wird bei 400 % fast alles weiß, und Rauschen in tiefen Schatten wird stark sichtbar (Tooltip sagt es).
  Test `MatchLuminance_UpTo400_LetsDeepShadowsReachTheTarget` (Gegenprobe: mit Grenze 200 % rot).
- **Nachtrag 0.5.44:** Regler „Gesamt“ oben in „Gleiche Farbe“, durch einen Strich abgesetzt (Wunsch des Benutzers):
  Luminanz für das ganze Bild ohne Masken (0–200 %, weiche Schulter), gerechnet **nach** der Bereichs-Luminanz –
  die gleicht mit Zielwert und Ferne die Bereiche an, macht das Bild insgesamt aber nicht heller. Kein Einfluss auf
  den teuren Teil (`PrepareKey` unverändert). Test `MatchGlobal_BrightensWholeImage_AfterRegionalLuminance`
  (Gegenprobe: ohne den Regler rot).
- **Nachtrag 0.5.45:** Luminanz wieder höchstens 300 % (Wunsch des Benutzers) – insgesamt heller macht jetzt „Gesamt“,
  die Bereichs-Luminanz braucht über 300 % nicht mehr. Test `MatchLuminance_UpTo300_LetsDeepShadowsReachTheTarget`
  (prüft auch, dass Werte darüber begrenzt werden).
- **Meilenstein 0.5.46 (10.10.2026):** Stand 0.5.45 beta – Entfernen mit LaMa/MI-GAN (#35) und Luminanz mit Bereichen,
  Zielwert, Ferne (Tiefenkarte), Durchblicken und „Gesamt“ (#34, #36–#40) vom Benutzer als passend befunden. Tag
  `v0.5.46`, Sicherung `../ImageConverter-0.5.46-Sicherung.zip`; Windows-Stand einmalig in den vom Benutzer genannten
  Ordner übertragen (`KI-Modelle/` dort unverändert).

## 41. Luminanz „Ferne“ (ohne Tiefenmodell) schonte zu wenig, wenn der Dunst-Regler aktiv war

- **Entdeckt in:** 0.5.46 (Commit `566d177`) beim Durchdenken der Reihenfolge (Frage des Benutzers, ob die Masken
  auf Dunst wirken); kein Bericht. **Behoben in:** 0.5.47 beta.
- **Was:** Ohne Tiefenmodell erkennt „Ferne“ die Ferne am Dunst (dunkelster Farbkanal). Die Karte wurde aus dem Bild
  *nach* dem Dunst-Regler berechnet – hatte er den Dunst entfernt, sah die Ferne aus wie Vordergrund und wurde
  aufgehellt: die beiden Regler arbeiteten gegeneinander.
- **Wie behoben:** Die Dunstkarte kommt aus dem Bild vor dem Dunst-Regler (nach dem Weißabgleich); alle anderen
  Karten (Bereiche, „was liegt dahinter“, Tiefenkarte) weiter aus dem Bild nach dem Dunst – aufgehellt wird, was man
  nach dem Dunst sieht. Mit Tiefenmodell ändert sich nichts.
- **Reihenfolge der Kette (zur Erinnerung):** Weißabgleich → Dunst → Tiefen/Lichter → Gleiche Farbe (Luminanz mit
  Bereichen, dann „Gesamt“) → Helligkeit/Kontrast → Dynamik/Sättigung/Färben. Dunst nutzt keine Luminanz-Masken.
- **Wo:** `Core/Imaging/Adjustments.cs` (`Prepare`: `beforeDehaze`), `Core/Imaging/LuminanceRegions.cs` (`Compute`
  mit `hazeFrom`, `Shrink`).
- **Test/Nachweis:** `LuminanceDistanceTests.Distance_UsesHazeFromBeforeDehaze` (enthält die Gegenprobe: ohne
  Vorher-Bild wird die Ferne aufgehellt).

## 42. Ausrichten als Rezept (Entzerren, Gerade richten, Zuschnitt, Drehen/Spiegeln)

- **Neu in:** 0.5.48 beta (Kern) und 0.5.49 beta (Einbau), 10.10.2026. Wunsch des Benutzers (Ganzes Ausrichten-Rezept):
  Entzerren und Drehen fehlten in der Liste „Bearbeitungen“; dreimal 90° sollte als 270° zurückzunehmen sein, freies
  Drehen ist die Feinkorrektur.
- **Vorher:** Jede Geometrie-Aktion rechnete sofort in die Pixel (ein Bild je Schritt im Verlauf). Rückgängig ging,
  aber keine Stufe ließ sich nachträglich ausschalten, und eine KI oder ein Entfernen danach ließ sich nicht mehr
  „entdrehen“.
- **Jetzt:** Je Bild ein `GeometryRecipe` im Bildverlauf (`EditHistory.Recipe`, **nicht** in den Einstellungen — ein
  Zuschnitt gilt nie für alle Bilder). Das Pixelbild (`EditHistory.Current`: Original + KI + Entfernen) bleibt ungedreht
  und unbeschnitten; angezeigt, exportiert und gedruckt wird Pixelbild + Rezept (`DocumentFor` = `WithGeometry(PixelsFor …)`).
  Feste Reihenfolge Entzerren → Gerade richten (mit Zuschnitt) → Drehen/Spiegeln, egal in welcher Reihenfolge man die
  Werkzeuge benutzt. Koordinaten relativ (Viereck zum Pixelbild, Zuschnitt zum entzerrten Bild): eine spätere Stufe
  bleibt ungefähr an ihrer Stelle, wenn eine frühere ausgeschaltet wird, und das Rezept passt auf verkleinerte Fassungen.
- **Liste:** je Stufe eine Zeile mit Auge („Entzerrt“, „Gerade gerichtet“, „Zuschnitt“, „Gedreht 270° · gespiegelt“ —
  netto, ein Auge für alle Vierteldrehungen). „Neu anwenden“ entfernt ausgeschaltete Stufen aus dem Rezept.
  Rückgängig weiter je Aktion (Snapshot mit `Recipe` davor).
- **Werkzeuge:** „Frei drehen“ zeigt das Bild ohne freie Drehung und Zuschnitt, Winkel und Rahmen vorbelegt (ein
  Zuschnitt lässt sich so wieder erweitern) und ersetzt beide; „Entzerren“ zeigt das Pixelbild nur gedreht/gespiegelt
  mit dem bisherigen Viereck; ein Zuschnitt im fertigen Bild schneidet enger — ist der Zuschnitt ausgeschaltet, ersetzt
  er ihn. Drehen/Spiegeln: `Rotated`/`Flipped` verknüpfen netto (nach dem Spiegeln dreht „rechts“ intern links herum).
- **Umrechnen:** Pipette (Weißabgleich) und Pinsel (Entfernen, samt Radius) rechnen vom angezeigten Bild ins
  Pixelbild (`ShownToPixels`, `ShownLengthToPixels`). Die KI bekommt das Pixelbild **nur gedreht/gespiegelt** (Gesichter
  aufrecht — liegende Gesichter findet die Erkennung schlecht), das Ergebnis wird exakt zurückgedreht; Zwischenstände und
  Vorschau werden für die Anzeige zurückgedreht und mit dem Rezept gezeigt (`ShowWithGeometry`). Export, Größe und Druck
  nehmen immer das fertige Bild (`ResultDocument`), nie die Ansicht eines Werkzeugs (`ShownDocument`).
- **Fallen:** (1) `ApplySourceSize` setzt Werkzeuge zurück — beim Umschalten in die Ansicht eines Werkzeugs darf es nicht
  laufen (sonst schließt sich das Werkzeug sofort); Ein-/Ausschalten einer Stufe passt nur Quelle (und unveränderte
  Zielgröße) an (`FollowSourceSize`). (2) Fassungen werden im Hintergrund gerechnet (Entzerren großer Bilder dauert);
  verdrängte erst freigeben, wenn die Vorschau steht (`EmptyGeometryTrash`). (3) „Neu anwenden“ prüfte „angezeigtes Bild
  ≠ Pixelbild“ — mit Rezept immer wahr; jetzt wird das Pixelbild mit/ohne KI verglichen.
- **Bewusst so:** Beim Zuschneiden mit ausgeschaltetem Gerade-richten fällt die (unsichtbare) Drehung weg — der neue
  Rahmen gilt im ungedrehten Bild. „Frei drehen“ setzt den Rahmen bei jeder Winkeländerung neu (wie bisher, vom Benutzer
  abgenommen). Speicher: höchstens drei Fassungen je Bild, andere Bilder beim Wechsel freigegeben.
- **Wo:** `Core/Imaging/GeometryRecipe.cs`; `Ui/ViewModels/MainViewModel.Editing.cs` (`EditHistory`, `Snapshot`,
  `CropToSelection`, `CommitRecipeAsync`, `Orient`, Werkzeug-Ansichten, `RestoreAsync`, `ShowCurrentAsync`),
  `.Edits.cs` (`EditSteps.Straighten/Level/CropStep/Orient`, `DocumentFor`, `WithGeometry`, Zeilen, „Neu anwenden“,
  KI-Hilfen), `.Preview.cs` (Vorher mit Rezept), `.Size.cs` (`FollowSourceSize`), `.WhiteBalance.cs`, `.Export.cs`,
  `.PerImage.cs`, `.Look.cs`; `Ui/Ai/AiViewModel.cs`, `Ui/Ai/RetouchViewModel.cs`.
- **Test/Nachweis:** `GeometryRecipeTests` (dreimal rechts = 270° und pixelgleich wie nacheinander gedreht; Drehen/
  Spiegeln in beliebiger Reihenfolge pixelgleich; Umrechnen hin und zurück; Umrechnen trifft einen markierten Punkt im
  gerechneten Bild; Stufe aus behält die anderen; Gerade richten ohne Zuschnitt ohne leere Ecken — Gegenprobe ohne
  automatischen Rahmen rot; Zuschnitt im gedrehten Bild; Werkzeug-Ansichten hin und zurück, auch gespiegelt).
  Unsichtbares Testfenster: 3× rechts → „Gedreht 270°“, Strg+Z → 180°; Zuschnitt, Auge aus/an mit Größe; Frei drehen mit
  vorbelegtem Rahmen; Entzerren; Export-Größe; Pipette im gedrehten Bild misst Grau; zehnmal Strg+Z → Original;
  Entfernen im gedrehten Bild trifft die Stelle im Pixelbild; KI auf gedrehtes Bild: Ergebnis ungedreht im Pixelbild,
  Vorschau richtig gedreht über dem Bild.

## 43. Luminanz: „Ferne schonen“ und KI-Tiefenkarte wieder entfernt

- **Entschieden in:** 0.5.50 beta (10.10.2026), Rückmeldung des Benutzers zu 0.5.49: Mit Tiefenmodell „greift das zu
  stark“ — und grundsätzlich: „ich würde gar nicht mit Ferne und Nähe arbeiten, weil es ja auf Luminanz gehen soll, und
  da kann Sonne auch näher sein (anderer Winkel, Reflexionen mitten im Bild)“.
- **Warum es weg ist:** Nähe/Ferne ist für eine Helligkeitsanpassung das falsche Merkmal. Den Mittelton (dunstiges Tal),
  für den „Ferne“ einst gebaut wurde (#36), hält inzwischen der Zielwert (#40) ruhig — am Panorama mit den Werten des
  Benutzers (Luminanz 173 %, Zielwert 45–52 %, Schwellwert aus) sieht das Tal ohne „Ferne“ fast so natürlich aus wie mit
  Tiefenkarte, entschieden wird aber rein nach Helligkeit.
- **Was entfiel:** Regler „Ferne“ samt Hinweis, Dunstkarte (#36, #37, #41), Tiefenkarte (#38: `IDepthEstimator`,
  `OnnxDepthEstimator`, Modell „Depth Anything V2“ im Katalog und in der Verwaltung, `AiModelKind.Depth`),
  `MatchDistance` in Einstellungen und Verlauf. Die Einträge #36–#41 bleiben als Geschichte stehen.
  **Nicht wieder einbauen** ohne neuen Grund: Ferne/Nähe als Auswahl für Luminanz.
- **Angepasst:** „Was liegt dahinter“ (Durchblicke, #39) richtete sich nach Schwellwert und Ferne — ohne beides gab es
  mit nur Zielwert keine Durchblick-Erkennung. Jetzt zählen auch Bereiche, die der Zielwert kaum anhebt, als „dahinter“.
  Beobachtung beim Test: Mit Zielwert werden Mischpixel am Baumrand ohnehin höchstens bis zum Ziel angehoben — die weißen
  Ränder entstehen dort kaum.
- **Wo:** `Core/Imaging/LuminanceRegions.cs` (neu geschrieben, schlanker; `TargetGain`, `For(limit, target, luminance)`),
  `Core/Imaging/Adjustments.cs`, `Core/Ai/AiModels.cs`, `Core/Ai/OnnxModels.cs`; UI: `.Tools.cs`, `.History.cs`,
  `.Edits.cs`, `.Look.cs`, `.PerImage.cs`, `MainViewModel.cs`, `Ai/AiViewModel.cs`, `Views/MainView.axaml`.
- **Test/Nachweis:** `AdjustTests.MatchTarget_Alone_LiftsTree_AndLeavesSkyAndGaps`; die übrigen Luminanz-Tests
  unverändert grün; `LuminanceDistanceTests` mit der Funktion entfallen. Panorama-Vergleich mit und ohne „Ferne“.

## 44. Neue Gruppe „Luminanzmaske“ (wie die Luminanzmasken in Referenzwerkzeug)

- **Neu in:** 0.5.51 beta (11.10.2026). Wunsch des Benutzers: Dunkles stärker anheben als Helles, um die Sonne nichts,
  Felder etwas, Himmel dezent — „eine feine Maske wie bei Referenzwerkzeug drüber, was dunkler ist bekommt mehr, bei den hellen
  Bereichen lässt man sie auslaufen“. Das Blau zwischen den Zweigen wurde mit „Gleiche Farbe“ zu hell (Saum).
- **Ursache des Saums (gemessen, Lupe):** Richtig blaue Lücken erkannte schon die Durchblick-Erkennung; der Saum kam von
  Mischpixeln (halb Nadel, halb Himmel), die die grobe Bereichskarte als Baum sah und voll anhob.
- **Bewusst als eigene Gruppe** neben „Gleiche Farbe“ (Entscheidung des Benutzers): das Erreichte bleibt unangetastet,
  die Gruppe lässt sich einfach wieder entfernen (`LumMask.cs`, Felder `Mask*`, eine Gruppe in der XAML, Zeile in der Liste).
- **Rechnen (`LumMask`):** Gewicht = (1 − Helligkeit/Auslaufen)^p, Helligkeit zu 70 % die des Pixels (pixelgenau, kein
  Hof, Mischpixel bekommen nur anteilig) und zu 30 % die eingeebnete des Bereichs (sonst hebt die Maske im Baum dunkle
  Nadeln stärker als sonnige — die Struktur würde flach). Aufhellen in Oklab wie Luminanz (Helligkeit und Buntheit im
  selben Verhältnis, weiche Schulter). Läuft nach „Gleiche Farbe“, vor Helligkeit/Kontrast.
- **Umkehr der Tonwerte ausgeschlossen:** L + (k·L − L)·(1 − L/E)^p bleibt steigend, solange
  1 + (k − 1)·min[(1 − u)^p − p·u·(1 − u)^(p−1)] ≥ 0 — für p ≥ 1,5 bis k = 3 erfüllt; p = 1 nur bis k = 2, p &lt; 1 nie
  (am Auslaufpunkt fällt die Kurve). Deshalb Verlauf p = 1,5 („weich“) … 4 („steil“), Stärke bis +200 %.
  **Nicht tun:** den Verlauf weicher als p = 1,5 zulassen.
- **„Maske zeigen“:** nur Ansicht (Graustufen weiß = volle Stärke, schwarz = nichts), kein Rückgängig-Schritt, nie im
  Export (`MaskPreview` setzt nur der Vorschau-Auftrag).
- **Weitere Wege abgewogen:** Mischpixel aufteilen (Matting) wäre am Baumrand genauer, hängt aber an Schätzungen — als
  Verfeinerung in dieser Gruppe vorgemerkt, falls nach dem Test noch ein Schleier stört. Belichtungsverschmelzung (Mertens)
  wirkt großflächiger, aber weniger fein an Zweigen. Mehrere Durchläufe (wie Dunst) helfen nicht: Jeder Durchlauf sieht
  dieselbe grobe Karte, Mischpixel würden wieder angehoben; „unter dem Schwellwert wieder berücksichtigen“ läuft auf eine
  feste Kurve je Pixel hinaus (siehe #36, #38).
- **Am Panorama:** Mit Maske (Stärke 100–150 %, Auslaufen 70–75 %, Verlauf 30–50) Nadeln so hell wie mit „Gleiche Farbe“,
  Blau zwischen den Nadeln natürlicher (weniger weißliche Sprenkel), Felder und Tal ruhiger; offener Himmel +0,01.
- **Wo:** `Core/Imaging/LumMask.cs`, `Core/Imaging/Adjustments.cs` (`MaskAmount`, `MaskEnd`, `MaskCurve`, `MaskPreview`,
  `HasLumMask`, `NeedsRegions`, `Apply`); UI `.Tools.cs`, `.History.cs`, `.Edits.cs` (`EditSteps.LumMask`), `.Look.cs`,
  `.PerImage.cs`, `.Preview.cs`; `Views/MainView.axaml` (Gruppe nach „Gleiche Farbe“).
- **Test/Nachweis:** `AdjustTests.LumMask_WeightsDarkMost_AndFadesOutAtTheEnd`, `LumMask_NeverReversesTones` (4 Fälle,
  Gegenprobe: mit p = 0,5 erlaubt schlagen zwei fehl), `LumMask_Preview_ShowsTheMaskInGrey`. Unsichtbares Testfenster:
  Gruppe, Liste („Luminanzmaske: +100 % · bis 70 % · Verlauf 30“), Maske zeigen.
- **Nachtrag 0.5.51 (Himmel „gräulicher“, gemessen, nichts geändert):** Mit den Werten des Benutzers (Stärke +49 %,
  Auslaufen 87 %, Verlauf 49, dazu Dunst, Gleiche Farbe und Kontrast +45) nimmt die Maske dem Himmel keine Farbe:
  Helligkeit und Buntheit steigen im selben Verhältnis (Blau oben L 0,48 → 0,52, C 0,036 → 0,039; insgesamt hat der
  Himmel mehr Farbe als das Original, vor allem durch Kontrast und Dunst). Gräulich wirkt es, weil das dunkle, satte
  Blau oben und an den Bäumen heller wird und sich dem fast grauen Himmel in der Mitte angleicht. Abhilfe ohne Umbau:
  Auslaufen auf ca. 55–60 %, Stärke etwas höher. Vorgemerkt, falls es doch stört: beim Aufhellen mehr Farbe mitnehmen
  oder Regler „Blau schonen“ (schützt auch das Blau zwischen den Zweigen). Der Benutzer lässt es so („wirklich gut“).
- **Meilenstein 0.5.52 (11.10.2026):** Stand 0.5.51 beta – Ausrichten als Rezept (#42), Luminanz nur nach Helligkeit
  ohne Ferne (#43) und Gruppe Luminanzmaske (#44) vom Benutzer als passend befunden. Tag `v0.5.52`, Sicherung
  `../ImageConverter-0.5.52-Sicherung.zip`; Windows-Stand in den vom Benutzer genannten Ordner übertragen
  (`KI-Modelle/` dort unverändert).

## 45. Neue Gruppe „Körnung“ (Filmkorn über das fertige Bild)

- **Neu in:** 0.5.53 beta (12.10.2026). Wunsch des Benutzers: Körnung wie bei „Haut & Details“, aber als eigene Gruppe
  bei den Reglern, unabhängig davon und nicht an die Luminanzmaske gebunden; Knopf „Aus dem Foto ermitteln“, danach
  von Hand nachbessern. Konzept: `KONZEPT.md` §1 K.
- **Immer der letzte Schritt:** nach Entrauschen und Schärfen, auch nach KI und „Haut & Details“ — gleich, welche
  Reihenfolge KI ↔ Einstellungen gewählt ist. Deshalb gehört das Korn **nicht** zum `Look` (der bei „Erst
  Einstellungen, dann KI“ ins Bild gerechnet und danach auf null gesetzt wird): eigene Felder `ExportOptions.Grain`
  / `GrainScale`, `RenderLook(…, grain)` (die KI ruft es ohne Korn), Stufe 5 der Vorschau, über dem offenen
  KI-Ergebnis in `RenderLookPreview`. **Nicht tun:** das Korn in `Adjustments` oder `Look` packen — dann käme es vor
  der KI ins Bild und das Modell glättet es weg.
- **Rechnen (`Core/Imaging/Grain.cs`):** Gauß-gefiltertes weißes Rauschen fest je Bildpixel (Hash, 4 × gleichverteilt
  statt Box-Muller), in Streifen zu 256 Zeilen mit Rand — gleiches Ergebnis wie am Stück, ohne Ebenen in voller
  Bildgröße; Streuung analytisch auf 1 gebracht (Σk² des Kerns bzw. der drei Boxen von `Blur.Gauss`). Stärke im
  Mittelton 0,12 · (Regler/100)², je Helligkeit √(4Y(1 − Y)) wie Film (Schwarz und Weiß nichts — auf weißem Papier
  sieht man deshalb kaum Korn). Rauheit: gröbere Stufe (2,5 ×) dazu und großflächige Schwankung der Stärke
  (6 ×, tanh). Farbanteil f: je Kanal √(1 − f) gemeinsames + √f eigenes Korn (Korrelation der Kanäle = 1 − f).
  Korngröße 0,25 px · 2^(Regler/20) in Pixeln der Ausgabe.
- **Vorschau und verkleinerter Druck:** Korn so, wie es nach dem Verkleinern des Exports aussähe (`Grain.AtScale`:
  Verkleinern ≈ Gauß 0,28 / Maßstab, gefiltertes Rauschen ∝ 1/Sigma). Abgeglichen an Lanczos 3: Vorschau/Export
  1 px 1,00, 2,8 px 0,92. Erst 0,42 angenommen — Vorschau 22 % zu schwach. Größenschätzung rechnet das Korn
  bewusst **ohne** Abschwächung (die Proben sollen je Pixel so körnig sein wie die echte Datei).
- **Ermitteln:** am Pixelbild vor der KI (`CurrentWithoutAi`), ohne Einstellungen (also vor dem Entrauschen), Mitte
  höchstens 12 MP. Stärke/Größe aus `FaceDetails.MeasureGrain` (Kurve √(4Y(1 − Y)) per kleinsten Quadraten ans
  Profil), Farbanteil aus der Korrelation der Kanäle im Hochpass der ruhigsten 20 % der Blöcke; auf die Ausgabegröße
  umgerechnet; Rauheit 0 (nicht messbar). Ein Rückgängig-Schritt.
- **Liste „Bearbeitungen“:** Zeile „Körnung“ als letzte (`EditSteps.Grain`), „Neu anwenden“ setzt die Stärke auf 0.
- **Test/Nachweis:** `GrainTests` (aus = Puffer selbst; Mittelton = Stärke, 0,1 → 0,6, Schwarz 0; fest je Pixel;
  Streifen = am Stück für echten Gauß und Boxen; Farbanteil 0/50/100 → Korrelation 1/0,5/0; Vorschau wie
  verkleinerter Export; Messen findet 40 %/1 px/50 % wieder, halbe Ausgabe schwächer, glattes Bild kein Korn; im
  Export nach dem Schärfen; Auge aus). Sichtprüfung am Grauverlauf (`~/.cache/imageconverter-build/werkzeuge/korn`).

## 46. Gewölbtes Blatt gerade ziehen (Schritt 1: Linien von Hand)

- **Neu in:** 0.5.54 beta (12.10.2026). Konzept und Recherche: `KONZEPT.md` §1 L. Schritt 2 (Vorschläge ohne Modell) und
  3 (Vorschläge mit UVDoc) folgen; „Doppelseite trennen“ (§1 M) ist noch offen.
- **Rechnen (`Core/Imaging/Dewarp.cs`, Daten `WarpLines.cs`):** Linien relativ zum Pixelbild, Richtung waagerecht /
  senkrecht / Falz. (1) Blattmodell im Raum: Kamera (Brennweite aus EXIF `FocalLengthIn35mmFilm`, sonst 28 mm KB),
  Blatt z = h(x) als natürlicher kubischer Spline (8 Abschnitte, Ränder 0; Doppelseite je Seite 6, am Falz verbunden,
  dort Knick), Levenberg-Marquardt über Lage, Biegung, je Linie die Konstante und je Punkt die Stelle entlang der
  Linie; Biegung entlang x und entlang y wird beides gerechnet, das Bessere gilt. Anfangslage: Homographie aus den
  äußersten Linien mit bekannter Brennweite zerlegt (gibt schon ein Seitenverhältnis), sonst frontal. (2) Abwickeln
  nach Bogenlänge; Vierteldrehung so, dass das Ergebnis wie das Pixelbild liegt (gedreht wird danach im Rezept);
  Auflösung wie in der Blattmitte. (3) DIN: nur wenn je zwei waagerechte und senkrechte Linien das Blatt umranden,
  ≤ 3 % neben 1 : √2 → eingerastet (Doppelseite: je Seite). (4) Gitter (160 Zellen) über das Ergebnis, Feinkorrektur
  als glattes Verschiebungsfeld (kleinste Quadrate, CG), bis jeder Punkt auf seiner Geraden liegt. (5) Größtes
  Rechteck aus Zellen, deren Ecken alle im Bild liegen. Zu wenig Linien oder Modell unplausibel: nur das Feld (eine
  waagerechte Linie verschiebt ganze Spalten), Seitenverhältnis bleibt. Abbildung relativ, Zwischenspeicher je Linien
  und Seitenverhältnis (Vorschau und volle Größe teilen sie). Pixel bikubisch (Catmull-Rom).
- **Abgleich (DewarpTests):** Glättung 300 → 100 und Doppelseite 4 → 6 Abschnitte je Seite: Am Falz steigt das Papier
  steil an (√d); vorher blieben nicht nachgezeichnete Zeilen der Doppelseite um 4–7 ‰ gebogen, jetzt ≤ 1,6 ‰.
- **Rezept:** neue erste Stufe `GeometryStages.Dewarp` (`GeometryRecipe.Dewarp`), Reihenfolge Wölbung → Entzerren →
  Gerade richten → Zuschnitt → Drehen/Spiegeln. Das Viereck des Entzerrens gilt jetzt relativ zum Bild **nach** der
  Wölbung (`QuadInView`/`StraightenedInView` mit den angezeigten Stufen), `LevelView` und `StraightenView` enthalten
  die Wölbung. `ToSource`/`ToResult` rechnen durch die Abbildung (Pipette, Pinsel). **Nicht tun:** `GeometryStages.All`
  in Tests als „alle vorhandenen Stufen“ erwarten — `Present` enthält `Dewarp` nur mit Linien.
- **Bedienung:** Symbol oben in der Vorschau (vierter Knopf), Ansicht `DewarpView` = Pixelbild nur gedreht/gespiegelt.
  `CropView.WarpLines`: Klick setzt Punkt auf die aktive Linie (sonst neue), Enter/Doppelklick/N/„Neue Linie“ beendet
  sie, Punkt ziehen mit Lupe, Klick auf eine Linie fügt ein, Rechtsklick wechselt die Richtung (sonst aus dem Verlauf
  geraten), Entf / Umschalt+Entf, Pfeiltasten. Leiste unten mit Ergebnisgröße und Seitenverhältnis (im Hintergrund
  gerechnet), „Gerade ziehen“ = ein Rückgängig-Schritt. Liste „Bearbeitungen“: Zeile „Gerade gezogen“ (`EditSteps.Dewarp`).
- **Probelauf an den Testbildern** (`TestBilder/`, Linien genau auf Kanten und Mitte gesetzt): Einzelblatt flach,
  Fehler 0,09 ‰, DIN eingerastet, 1546 × 2483; Doppelseite flach, Seite 1 : 1,46 (wahr 1,41 — 3,5 %, nicht eingerastet);
  welliges Blatt: gesetzte Linien gerade, dazwischen Welle (mehr Linien setzen). Lösen ≈ 0,7 s, Rechnen ≈ 0,9 s bei
  1800 × 2400. Das ganze Bild bleibt — unten bleibt viel Tisch stehen (zuschneiden mit dem Zuschnitt).
- **Test/Nachweis:** `DewarpTests` (gewölbt: auch nicht gezeichnete Zeilen ≤ 1 ‰ gebogen; umrandet: DIN eingerastet,
  1,2 auf ± 5 % gefunden; Doppelseite; wellig nur mit Gitter; eine Linie: nur sie, Größe bleibt; Rezept: Stufe zuerst,
  Punkte hin und zurück < 0,5 px, ausschaltbar, Größe beim Anwenden). Die Bedienung in der Vorschau ist nicht
  automatisch getestet (kein unsichtbares Testfenster unter Windows) — vom Benutzer zu prüfen.

## 47. Gewölbtes Blatt: Linien vorschlagen (Schritt 2, ohne Modell)

- **Neu in:** 0.5.55 beta (12.10.2026). Knopf „Linien vorschlagen“ im Werkzeug; Vorschläge gestrichelt, Klick wählt ab
  (grau) oder wieder an, Ziehen an einem Punkt übernimmt den Vorschlag als normale Linie, „Vorschläge verwerfen“. Nur
  angewählte Linien zählen (`WarpLineShape.Counts`); ins Rezept kommen sie als gewöhnliche Linien.
- **Erkennen (`Core/Imaging/LineFinder.cs`, auf höchstens 1600 px verkleinert, ≈ 0,5–0,8 s):**
  Textzeilen — dunkel gegen den örtlichen Hintergrund, Flecken als Buchstaben (Größe aus dem Median), Schreibrichtung
  aus den Richtungen zum nächsten Nachbarn, Ketten von vorn nach hinten mit langsam drehender Richtung, Lücken (Spalten)
  wieder zusammengesetzt; Grundlinie als **eine robuste glatte Kurve über die ganze Zeile** (Knoten alle 5
  Buchstabenhöhen, zweite Differenzen, Unterlängen als Ausreißer in 5 Durchgängen). Auswahl: bis zu 8 Zeilen
  gleichmäßig, bei einer Doppelseite je Seite 6. Blattrand — Otsu, außen = dunkel und mit dem Bildrand verbunden,
  **Blattfläche geschlossen** (Quadrat ± 1/90 der Bildgröße), Moore-Umriss an den vier Ecken (im Rahmen der Schrift)
  geteilt, je 9 Punkte auf den stärksten Sprung geschoben und durch eine robuste kubische Kurve geglättet; Kanten am
  Fotorand fallen weg. Falz — schmales dunkles Tal im mittleren Drittel, in ≥ 6 von 9 Höhen an derselben Stelle.
- **Falle 1 – Grundlinie mit Median je Fenster:** Die Schreibrichtung wird bei gebogenen Zeilen leicht schräg geschätzt
  (≈ −4°); im gedrehten Rahmen steigt die Grundlinie im Fenster an, und wo sich Unterlängen häufen, kippt der Median auf
  sie — 5–6 px daneben. Ausgleichsgerade je Fenster und Nachbar-Abgleich halfen nur teilweise; erst die glatte Kurve über
  die ganze Zeile (gemessen ≤ 0,4 px) trifft überall.
- **Falle 2 – Zickzack-Kante:** Dünne dunkle Rahmenlinien, die den Blattrand berühren, sind mit dem Untergrund verbunden;
  die Blattfläche bekam tiefe Schlitze, der Umriss lief hinein. Ebenso trennte der Schatten am Falz eine Doppelseite in
  zwei Blätter (nur die rechte Seite, kein Falz). Abhilfe: Schließen der Blattfläche; zusätzlich gilt eine Kante nur,
  wenn ihr Umriss höchstens 30 % (+ 8 % Pixeltreppen) länger ist als die Verbindung.
- **Grenze:** Ist der Blattrand selbst nicht gerade (Arbeitsblatt-Testblatt: handgezeichneter schwarzer Rahmen), passt das Modell
  schlechter (3 ‰) — die Kante abwählen.
- **Test/Nachweis:** `LineFinderTests` (künstliche Seite ohne Schriftart: gebogene Zeilen aus Kästchen mit Ober-/Unterlängen
  ≤ 1 px auf der Grundlinie; vier Kanten ≤ 2 px; kein Falz auf einem Blatt; Falz einer Doppelseite ± 5 px; Blatt füllt
  das Foto: keine Kanten). Sichtprüfung an `TestBilder/` (`~/.cache/imageconverter-build/werkzeuge/vorschlag`): Schulbuch-Blatt
  8 Zeilen + 4 Kanten, Fehler 0,33 ‰, DIN; Doppelseiten 12 Zeilen + 4 Kanten + Falz, 0,3–0,7 ‰.

## 48. Gewölbtes Blatt: Vorschläge genauer, senkrechte Linien, Fehlerbild gegen das Original

- **Neu in:** 0.5.56 beta (12.10.2026). Wunsch des Benutzers: Linien liegen teils daneben, unten wellig (Schulbuch-Testblatt),
  auch senkrechte Linien erkennen.
- **Messen statt schätzen — Fehlerbild** (`~/.cache/imageconverter-build/werkzeuge/fehlerbild/Fehlerbild.cs`): Für die
  Testbilder ist die wahre Form bekannt (Woelbung.cs). Ergebnis → Bild → Blatt (Umkehrung), ideal affin; Abweichung je
  Stelle in Pixeln (Mittel, 95 %, max), dazu je Linie die Streuung quer zur wahren Linie und ein Wärmebild. **Nicht
  tun:** Verbesserungen nach Augenschein beurteilen — erst das Fehlerbild zeigte, dass die Rechnung selbst stimmte
  (exakte Linien: 0,3 px) und die Wellen von zwei falschen Vorschlägen kamen.
- **Gefundene Ursachen und Abhilfe (Mittel / Max, automatisch):** Schulbuch-Blatt 1,5 / 9,0 → 0,4 / 3,6 px; Schulbuch-Doppelseite
  14,3 / 55 → 2,0 / 11 px; Arbeitsblatt-Doppelseite 13,9 / 51 → 1,1 / 15 px; Arbeitsblatt-Blatt 9,2 / 44 → 2,0 / 22 px.
  1. Fußzeile (kleine Schrift) sprang in die Nachbarzeile — Toleranzen der Kette jetzt in der Schriftgröße **der Zeile**.
  2. Untere Kante an der eingerollten Ecke 5 px daneben — kubische Glättung zu steif: 15 Punkte, Kurve 5. Grades.
  3. Doppelseite selbst mit exakten Linien 10 px daneben: Am Falz steigt das Papier wie √d an (Spline kann das nicht),
     und die Glättung bestrafte den Knick am Falz. Jetzt je Seite ein Anteil √u · (1 − u), Falz aus der Glättung genommen.
  4. Ober-/Unterkante der Doppelseite (knickt am Falz) bis 49 px daneben — am Falz geteilt, je Seite eine Kante.
  5. Falsche Vorschläge (handgezeichneter Rand des Arbeitsblatt-Blatts, Würfelkanten als „senkrecht“, Aufgabennummern als
     Textrand) — `Dewarp.RejectOutliers`: die am schlechtesten passende Linie fällt heraus, wenn sie über dem 2,5-Fachen
     des Medians **der ersten Runde** liegt (mind. 0,8 ‰) und das Modell ohne sie trägt und ≥ 10 % besser passt; bis zu
     viermal; nur Vorschläge, nie Linien von Hand. In der Oberfläche grau (abgewählt), wieder anwählbar.
     **Falle:** Mit dem Median der laufenden Runde blieb die zweite schlechte Kante drin (sie verbiegt das Modell, alle
     passen schlechter); ohne die Prüfung „trägt ohne sie“ wurde bei der Doppelseite eine richtige Kante abgewählt und
     das Modell fiel aus.
  6. Halber Pixel daneben beim Nachschärfen (Pixelmitte vs. -ecke verwechselt) — `LineFinderTests.Refine_…` fand es.
- **Schneller:** Variable Projection (Golub/Pereyra) — die Stelle jedes Punkts entlang seiner Linie ist keine Unbekannte
  des Lösers mehr, sondern wird je Auswertung bestimmt (nächster Punkt der projizierten Linie, Gauß-Newton, nach jedem
  Schritt als Startwert nachgeführt). Prüfen von 22 Linien 28 s → 1,9 s.
- **Neu erkannt:** gezeichnete Linien waagerecht und senkrecht (dünne Querstücke von Spalte zu Spalte verfolgt,
  Kreuzungen überbrückt, Schräge und Kurzes verworfen) und Textränder (RANSAC über Zeilenanfänge/-enden, ≥ 5 Zeilen).
  Nachschärfen im Original (`LineFinder.Refine`: Kanten auf den Sprung, Falz/gezeichnete Linien auf die dunkelste Stelle,
  Unterpixel). Vorschau zeichnet Linien glatt (Catmull-Rom).
- **Entfernen (Wunsch des Benutzers):** an der Linie unter der Maus (bzw. der aktiven/angefassten) ein × neben der Mitte —
  Klick entfernt die ganze Linie, Vorschlag oder eigene; Entf auf einem Vorschlag entfernt ihn ganz; „Abgewählte entfernen“.
- **Grenze:** Das wellige Testbild dehnt das Papier (nicht abwickelbar) — dort bleibt die Abweichung hoch, echtes Papier
  kann das nicht; nicht danach abgleichen.
- **Test/Nachweis:** `DewarpTests.Outliers_OnlyWrongSuggestionsAreDeselected`, `LineFinderTests.FindsDrawnLines_AlsoVertical`,
  `LineFinderTests.Refine_PutsAnEdgeOnTheStepInFullResolution`; alle 356 Tests grün.

## 49. Gewölbtes Blatt: Absturz beim Klick auf eine vorgeschlagene Linie (oder neben das ×)

- **Entdeckt:** 0.5.56 beta (12.10.2026), vom Benutzer („über das × bricht die App ab“). **Behoben:** im selben Stand.
- **Was:** `ArgumentOutOfRangeException` in `CropView.RenderWarpLines` (Ereignisanzeige, Quelle „.NET Runtime“, auch schon um
  12:27 vor dem ×). Ein Klick auf den *Verlauf* eines Vorschlags (nicht auf einen Punkt) merkt sich die Linie mit Punkt −1
  (`PressSuggestion(…, -1)`), der Ziehmodus läuft — die Lupe zeichnete `Points[-1]`. Beim × kam dazu: Es verschwand, sobald
  die Maus die Linie Richtung × verließ (Hover nur auf der Linie), der Klick traf dann den Verlauf.
- **Wie behoben:** Lupe und Pfeiltasten nur mit Punkt ≥ 0; das × bleibt auf dem Weg dorthin stehen (Hover hält, solange
  die Maus höchstens 30 px vom × entfernt ist). **Nicht tun:** `_warpSel.Point` ohne Prüfung auf −1 als Index benutzen.
- **Hinweis zur Fehlersuche unter Windows:** Abstürze stehen mit Stapel in der Ereignisanzeige
  (`Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='.NET Runtime'}`).

## 50. Lupe auch beim Zuschnitt, Fadenkreuz in Negativfarbe, Verschieben über den Rand, Zeichenmodus

- **Neu in:** 0.5.56 beta (12.10.2026), Wünsche des Benutzers.
- **Lupe beim Zuschnitt:** beim Aufziehen und beim Ziehen an Ecken/Kanten (Stelle: gezogene Ecke bzw. Punkt der gezogenen
  Kante unter der Maus, `_dragImage`), Rahmen als Linie darin; nicht beim Frei drehen (Bild dort gedreht gezeichnet).
- **Fadenkreuz:** alle Lupen laufen über `CropView.DrawLoupe` (eine Stelle für alle: Entzerren, Pipette, Linien, Zuschnitt).
  Farbe = Negativ des Bildes darunter (`CrossColor`: Mittel 5 × 5 Pixel der Bitmap über `Bitmap.CopyPixels`), bei fast
  mittlerem Grau Schwarz/Weiß; dünner Schatten darunter. Vorher weiß — auf weißem Papier unsichtbar.
- **Verschieben über den Rand:** `ClampPan` lässt das Bild so weit, dass jede Stelle bis zur Mitte der Vorschau kommt, auch
  eingepasst (Verschieben schaltet `_fitMode` aus; `UpdateGeometry` passt nur noch ein, wenn `_fitMode` an ist oder der
  Maßstab unter „Einpassen“ liegt). Grund: Ränder eines knapp zugeschnittenen Blatts lagen unter der Werkzeugleiste.
- **Gewölbtes Blatt, Bedienung neu:** Vorgabe „Ansehen“ — Ziehen verschiebt das Bild, Ziehen an einem Punkt den Punkt,
  Klick (ohne Ziehen) auf eine Linie markiert sie bzw. wählt einen Vorschlag ab/an (`ReleaseWarpClick`). „Linien zeichnen“
  ist ein Schalter (`CropView.DrawLines` ↔ `DewarpDrawing`, Taste N): Klick setzt Punkte, Enter/Doppelklick beginnt die
  nächste Linie, Esc beendet den Modus. Entf auf einer markierten Linie entfernt sie ganz.
- **Nachtrag (Wunsch):** Linie verlängern — eine angeklickte (markierte) Linie wird beim Einschalten von „Linien zeichnen“
  die aktive; jeder Klick kommt an ihr **näheres Ende** (vorn oder hinten), ein Vorschlag wird dabei zur normalen Linie.
  Im Zeichenmodus folgt die Lupe der Maus schon vor dem Klick, dazu gestrichelt das nächste Stück vom näheren Ende.
- **Meilenstein 0.5.57 (12.10.2026):** Stand 0.5.56 beta — Körnung (#45), gewölbtes Blatt gerade ziehen von Hand (#46), Linien
  vorschlagen samt senkrechten Linien, Abwählen unpassender Vorschläge und Nachschärfen (#47, #48), Absturz beim Klick auf
  eine Linie behoben (#49), Lupe beim Zuschnitt, Fadenkreuz in Negativfarbe, Verschieben über den Rand, Zeichenmodus und
  Verlängern (#50) — vom Benutzer als passend befunden. Tag `v0.5.57`; schlanke Sicherung auf Wunsch des Benutzers in
  `published/ImageConverter-0.5.57-Sicherung.zip` (mit `.git` samt Hooks und der lokalen Anweisungsdatei; ohne `bin/`, `obj/`, `published/`,
  `KI-Modelle/`, `TestBilder/`).

## 51. Gewölbtes Blatt: Linienfindung — schwache Striche, Flächenkanten, Auswahl (0.5.58 beta)

- **Neu in:** 0.5.58 beta (12.10.2026). Wunsch des Benutzers: klare, präzisere Linien, mehr senkrechte und waagerechte,
  insbesondere Überschriftenbalken, farbige Kästen und das Karoraster, die 0.5.57 nicht fand.
- **Befund vorher** (`Fehlerbild.cs`, Streuung je Linie quer zur wahren Linie, Schulbuch-Blatt): Textzeilen und Blattkanten
  0,1–0,7 px, gezeichnete waagerechte nur 4 (alle am Fußende, nach Länge gewählt), senkrechte: zwei Würfelkanten aus den
  Zeichnungen 12–13 px daneben (Schrägen bis 19° erlaubt, an Ecken mit der Diagonalen verbunden), Balken und grüne Box
  gar nicht (keine dünnen dunklen Striche), Karoraster nicht (hellgrau auf Grün unter der Tintenschwelle).
- **Umbau in `LineFinder`:**
  - `ThinDark`: schwache Striche = Schließen (Max/Min-Filter) minus Bild über 4 σ Rauschen (mind. 0,025), nur im Innern
    des Blatts (1,5 Buchstabenhöhen vom Rand: Schatten, Heftrücken, Kantensaum gaben sonst parallele Falschlinien — DocUNet
    1_1 kippte das Modell). `RuledLines` bekommt dazu die echte Tinte: Breite und Mitte eines Stücks aus seinem Tintenkern,
    sonst gilt ein dunkler Strich samt Saum als zu dick (Arbeitsblatt verlor so seine Kastenränder).
  - Spuren: Verbinden nur bei gleicher Steigung (±0,15), `Smooth` (Sehne unter 14°, Parabelrest ≤ 2,5 px), `Spread`:
    längste zuerst, dann die am weitesten entfernte (Karoraster gäbe sonst 8 Nachbarn), bis 8 je Richtung, Mindestabstand
    0,6 Buchstabenhöhen (1,5 warf bei Arbeitsblatt den doppelten Kastenrand weg).
  - `BoxEdges` (neue Quelle `FoundLineSource.Box`, Status „Flächenkanten“): Abstand zur örtlichen Papierfarbe je Kanal
    (weit geglättet nur über papierähnliche Pixel — ein dunkler Balken zog die Schätzung sonst zu sich, und das Papier galt
    als Fläche), buchstabengroße und dünne Tinte ausgeschlossen (fette Überschriften verschmolzen sonst mit dem Balken),
    Löcher gefüllt, Füllung im Rahmen der Schrift (schräg liegt ein Balken in einem zu großen Bildachsen-Rechteck), Kanten
    wie beim Blattrand über `EdgesOf` auf dem Abstandsbild (Hellgrün neben Weiß ist in der Helligkeit kein Sprung), mit
    Mindestsprung 0,04. `Refine` behandelt `Box` wie `Edge`.
  - Textränder: Toleranz 0,4 statt 0,6 Buchstabenhöhen.
- **`Dewarp`:** Seitenverhältnis nur, wenn die senkrechten Linien 60 % der Höhe und die waagerechten 60 % der Breite
  überspannen (acht kurze Rasterstriche im Kasten sagten sonst 1,314 statt DIN). `DewarpSpec.GridOnly` (nur Gitter) und
  `Dewarp.LineErrors` (Modellrest je Linie) für Werkzeuge und einen späteren zweiten Durchgang.
- **Messung** (echter Fehler gegen den Generator, Mittel/max px): Schulbuch-Blatt 0,4/4,5 → 0,5/2,8 · Schulbuch-Doppelseite 2,0 →
  1,4 · Schulbuch wellig 6,7 → 5,9 · Arbeitsblatt-Blatt 2,0 → 1,9 · Arbeitsblatt-Doppelseite 1,1 → 1,9 (schlechter; mit 6 statt 8 Linien
  je Richtung sogar 6,6 — die Auswahl ist dort empfindlich). 130 echte DocUNet-Fotos: in der Stichprobe 3 besser, 6
  gleich, keins schlechter; die schweren Fälle (Bon in der Hand, Heftrücken) scheitern in beiden Fassungen am Blattmodell.
- **Zweiter Durchgang** (Linien im Ergebnis erneut suchen, nur Gitter nachziehen): auf dem Generator ±0,1 px, beim
  zerknitterten Faltblatt 60_1 sichtbar geradere Ränder. Nur im Werkzeug (`Stapel.neu.cs`, `ZWEITER=1`), nicht in der App.
- **Verworfen am selben Tag:** KI-Modelle UVDoc, Flattener (13–25 px direkt), SyntheticDoc (13 px direkt auf
  Arbeitsblättern, aber klar besser als wir bei Bon in der Hand und Knitter — ONNX müsste selbst gebaut und gehostet
  werden, Export-Skript außerhalb des Projekts) und DvD (AGPL, ~800 MB, kein ONNX). Entscheidung des Benutzers: keine KI.
- **Werkzeuge** (außerhalb des Projekts, `~/.cache/imageconverter-build/werkzeuge/`): `vergleich-alle.sh` (alt gegen
  neu mit echtem Fehler), `stapel/` (viele Bilder in einem Prozess, 0.5.57 aus einem `git worktree`), `ki/` (KI-Modelle),
  `raster/`, `vergleich/`. Jedes Werkzeug braucht einen eigenen `ArtifactsPath`, sonst überschreiben sie sich gegenseitig.

## 52. „KI begradigen“ mit SyntheticDoc (0.5.59 beta, erst einmal nur lokal)

- **Neu in:** 0.5.59 beta (12.10.2026), Entscheidung des Benutzers nach dem Test: SyntheticDoc (ECCV 2026, Nachfolger von
  UVDoc vom selben Autor, 8,45 Mio. Parameter) als **eigene Option** neben „Gewölbtes Blatt“ — eigenes Symbol in der Vorschau,
  beide Symbole in einem Rahmen, weil es zwei Begradigungen derselben Stufe sind. Andere Modelle (UVDoc, Flattener, DvD)
  bleiben verworfen (#51).
- **Modell:** Beim Autor gibt es nur den PyTorch-Checkpoint (`highres_121x177.ckpt`, 97 MB, davon Optimierer). Die ONNX-Datei
  (31 MB) baut `werkzeuge/ki/export_syntheticdoc.py` (außerhalb des Projekts, Python über `uv`, Torch CPU): nur der Ausgang
  `bm_map` (1 × 2 × 121 × 177), Eingabe 1 × 3 × 1440 × 1024 RGB, normalisiert (x − 0,5) / 0,5, Opset 17. Die 31 × 45-Fassung
  ist durchweg schlechter. **Kein Download-Ziel:** im Katalog steht eine leere Adresse, die Datei liegt von Hand unter
  `KI-Modelle/syntheticdoc/syntheticdoc_121x177.onnx` (Programmordner oder `%LOCALAPPDATA%\ImageConverter\KI-Modelle`);
  `AiModelItem.ShowDownload` blendet den Knopf ohne Adresse aus. Hosten (Git-Release oder Hugging Face) ist offen.
- **Rechnung (`AiDewarp`):** Bild verkleinert auf 1600 px, Verkleinern aufs Modellmaß mit Flächenmittel (wie PIL mit
  Vorfilter — bilinear ohne Filter lag daneben); zwei Läufe, der zweite auf dem Ausschnitt um das gefundene Blatt (+ 4 %);
  Gitterwerte mit align_corners = True (±1 = Mitten der äußersten Pixel des Ausschnitts — mit Pixelkanten gerechnet lag es
  um ein halbes Pixel daneben). Das Modell kennt kein Seitenverhältnis (es gibt in der Originalpipeline einfach das
  Eingabeformat zurück): wir nehmen 9 Gitterzeilen und 4 -spalten als Linien für unser Blattmodell → `PageAspect`, DIN;
  ersatzweise Bogenlängen des Gitters. Ergebnisbreite = längste Gitterzeile in Bildpixeln.
- **Rezept:** `DewarpSpec.Grid` (`DewarpGrid`, relativ, mit Gleichheit über die Werte für den Zwischenspeicher) — dann
  zählen keine Linien; `Dewarp.Solve` liefert daraus die `DewarpMap` (`Ai = true`). `GeometryRecipe.AiDewarpedInView` dreht das
  Gitter der (gedrehten/gespiegelten) Ansicht ins Pixelbild zurück: Knoten über `OrientPoint` auf dem Einheitsquadrat und
  `UnOrientPoint`, bei einer Vierteldrehung Nx/Ny und Breite/Höhe getauscht, Seitenverhältnis gestürzt (Test
  `AiDewarpTests`).
- **Oberfläche:** `StraightenWithAiCommand` (MainViewModel.Dewarp.cs): Ansicht `DewarpView` holen, Modell über
  `Ai.Sessions.Get`, Gitter rechnen, `CommitRecipeAsync` als ein Schritt („KI begradigt · 1234 × 1700 px · Seite 1 : 1,41
  (DIN)“); Symbol gedrückt, solange `CurrentRecipe.Dewarp.Grid` da ist (`AiStraightenActive`, nach jedem Umbau der
  Bearbeitungsliste gemeldet), zweiter Klick nimmt die Stufe heraus. Bearbeitungsliste: „KI begradigt · KI (SyntheticDoc)“.
  Fehlt das Modell, sagt die Fehlermeldung, wohin die Datei gehört.
- **Messung** (`werkzeuge/ki/Ki.cs`, echter Fehler gegen den Generator): Arbeitsblätter 13–18 px (Linienverfahren 0,4–2 px),
  Doppelseiten 31 px; echte Fotos: Bon in der Hand und zerknittertes Faltblatt sichtbar besser als das Linienverfahren,
  Fotobuch brauchbar. 1,6–1,8 s je Bild auf der CPU.

## 53. SyntheticDoc ohne Python: Checkpoint → ONNX in C#, Download vom Autor, schneller, Training im Projekt (0.5.61 beta)

- **Neu in:** 0.5.61 beta (13.10.2026), Wunsch des Benutzers: das Bauen der ONNX-Datei in unseren Quellcode, kein
  Hosting, unabhängig vom Original-Repository, selbst trainieren können, leicht wieder entfernbar. Beschreibung, Ablauf
  des Trainings und **Entfernen-Liste**: `docs/KI-BEGRADIGEN.md`.
- **Umwandler** (`ImageConverter.Core/Ai/SyntheticDoc/`): `TorchCheckpoint` (Zip + Pickle-Protokoll 2 — der Checkpoint
  nutzt nur OrderedDict, `_rebuild_tensor_v2`, Float-/LongStorage; Tensoren über Schritte zusammenhängend gelesen),
  `OnnxGraph` (Protobuf von Hand: ModelProto, Graph, Knoten, Initialisierer; ir 8, Opset 17), `SyntheticDocNet`
  (Netz nach `model.py`, BatchNorm gefaltet, gespiegeltes Polster als `Pad`+`Conv`, `Resize` linear `half_pixel` wie
  `align_corners=False`). Gegen den PyTorch-Export: größte Abweichung 4,8 · 10⁻⁷. **Falle:** die Methode hieß erst
  `Convert` und verdeckte `System.Convert` (CS0119) — heißt jetzt `ToOnnx`.
- **Falle Rechenzeit:** ONNX Runtime rechnet alle Knoten, auch wenn nur `bm_map` abgefragt wird — mit Schatten-Zweig
  1,2 s statt 0,6 s. Die App baut ohne (`shadow: false`); `tools/SyntheticDocUmwandeln.cs --schatten` baut mit.
- **Download:** Katalog lädt jetzt den Original-Checkpoint (`igl.ethz.ch/projects/SyntheticDoc/highres_121x177.ckpt`,
  97 MB, SHA-256 a9b69148…a91d, `MainFile` = der Checkpoint); `SyntheticDocModel.OnnxFor` baut `syntheticdoc.onnx`
  daneben beim ersten Gebrauch und neu, wenn der Checkpoint jünger ist (eigenes Training: Datei ersetzen). Die frühere
  Python-ONNX-Datei (feste Größe 1440 × 1024, `syntheticdoc_121x177.onnx`) wird nicht mehr benutzt — mit offener
  Größe braucht es einen neuen Dateinamen, sonst hätte die App die alte Datei mit fester Größe weiterbenutzt.
- **Schneller:** erster Lauf auf 720 × 512 (0,3 s, nur um das Blatt zu finden), zweiter auf 1440 × 1024 auf dem
  Ausschnitt; Ausschnitte und Verkleinern über Magick (`ResampleFilter.Bilinear` = Dreieck mit Vorfilter) aus dem
  vollen Bild statt der 1600-px-Fassung. 1,2–1,4 s je Bild statt 3,5–5 s.
- **Gemessen und verworfen:** Rand 2 % (wie im Training) statt 4 %, dritter Lauf, gespiegelt mitteln: ±1–3 px. Kombination
  „KI, dann unsere Linien im Ergebnis“: nur Gitter ±0,5 px, mit Blattmodell Schulbuch wellig 19,7 → 11,8, aber Schulbuch gewölbt
  13,2 → 16,2 und Arbeitsblatt-Doppelseite kaputt (192 px). Andere Modelle: D2Dewarp hat Gewichte (CC BY-NC-ND), aber DocUNet
  MS-SSIM 0,50 < SyntheticDoc 0,558; AADD weiter ohne Gewichte.
- **Training im Projekt:** `extern/SyntheticDoc/` (MIT, ohne `media/`), eigene Ergänzungen mit „ImageConverter:“
  gekennzeichnet (`IMAGECONVERTER.md`): `train.py --init-weights` (nur Gewichte als Start) und `--accelerator`,
  `generation/pack_shards.py` (gerenderte Beispiele → Zip-Pakete). Trainingsschritt auf dem Entwicklungsrechner (CPU):
  5,6 s je Bild in voller Auflösung → Nachtrainieren mit 20 000 Beispielen × 5 Epochen ≈ 6,5 Tage; auf einer
  NVIDIA-GPU geschätzt Stunden. Zum Trainieren braucht es nur flache Originale; die Verformung wird simuliert (ARCSim,
  nur nicht-kommerziell — für die private App in Ordnung), die Rückabbildung fällt dabei ab. Echte Fotos taugen nur zum Prüfen.

## 54. Start dauerte 3 s und mehr — ReadyToRun; SyntheticDoc fehlte im KI-Tab (0.5.63 beta)

- **Gemessen** (13.10.2026, zweite Instanz bis zum Fenster mit Titel): aus `published` in OneDrive 2,9–3,6 s, lokal
  2,3 s; der **erste** Start nach jedem Bau 6–13 s (Windows prüft die neue Datei, je größer, desto länger). Im Code
  nichts Schweres beim Start — die Zeit ging in das Entpacken der komprimierten Einzeldatei und das Übersetzen (JIT).
- **Varianten** (lokal, warm): heute 2,3 s / 51 MB · ohne Kompression 2,1 s / 109 MB · **ReadyToRun komprimiert
  1,2 s / 70 MB** · ReadyToRun ohne Kompression 0,9 s / 161 MB (erster Start 12,8 s). Genommen: `PublishReadyToRun`
  in `ImageConverter.Desktop.csproj` (Release, gilt auch für `publish.sh`). Danach aus `published` 1,0–1,3 s.
- **KI-Tab:** `AiViewModel.ModelGroups` kannte die Art `Dewarp` nicht — SyntheticDoc war nicht zu sehen und nicht zu
  laden, obwohl die Fehlermeldung von „KI begradigen“ dorthin verweist. Neue Gruppe „BEGRADIGEN“. Bei einer neuen
  `AiModelKind` immer auch eine Gruppe anlegen.

## 55. KI begradigen: Schatten entfernen; Feld „Gewölbtes Blatt“ im kleinen Fenster abgeschnitten (0.5.64 beta)

- **Schatten (Wunsch):** SyntheticDoc sagt eine Schattenkarte voraus, die App nutzte sie nicht. Jetzt rechnet der
  genaue Lauf sie mit (zweite Datei `syntheticdoc-schatten.onnx`, +0,55 s), sie kommt gerade gezogen (256 × 360 × RGB,
  `DewarpShading`) ins Rezept und wird in `DewarpMap.Render` herausgerechnet wie bei den Autoren (÷ Licht · Stärke +
  1 − Stärke, Stärke = min(1, 0,4 / Helligkeit)). Rechtsklick auf das KI-Symbol → „Schatten entfernen“, gemerkt in
  `AppSettings.AiRemoveShadows`; Umschalten ohne neuen KI-Lauf. Einzelheiten: `docs/KI-BEGRADIGEN.md`.
- **Falle:** `OnnxSessions.Get` schloss beim Öffnen einer Datei alle anderen großen Modelle — die zwei Dateien von
  SyntheticDoc hätten sich bei jedem Lauf gegenseitig geschlossen und neu geladen. Jetzt merkt es sich je Pfad das
  Modell und schließt nur andere Modelle.
- **Feld „Gewölbtes Blatt“:** eine feste Zeile mit sechs Knöpfen, Infotext (mindestens 200 px) und der Bedienhilfe
  darüber — über 1000 px breit, in kleinen Fenstern rechts abgeschnitten. Jetzt höchstens 560 px: Bedienhilfe hinter
  „?“, kurze Beschriftungen („Vorschlagen“, „Zeichnen“, „Graue weg“ …), kleine Knöpfe (`Button.compact`) in einem
  `WrapPanel`, das umbricht; seitlich 58 px frei für das Symbol „Bearbeitungen“. Geprüft bei kleinster Fenstergröße.
- **Werkzeug-Falle (PowerShell):** Variablen unterscheiden keine Groß-/Kleinschreibung — `$h` (Fenster) überschrieb
  den Parameter `$H` (Höhe) im Bildschirmfoto-Skript.

## 56. Dunst rechnete bei Tiefen/Lichtern und in der Größenschätzung neu; Korn feiner als ein Pixel, Härte (0.5.66 beta)

- **Frage des Benutzers:** Muss der Dunst neu gerechnet werden, wenn sich nur Spätere in der Kette ändern? Licht und
  Farbe, Schärfen, Körnung nutzten den Zwischenstand schon. Aber: Tiefen/Lichter (nach dem Dunst) steckten im selben
  Schlüssel (`PrepareKey`) — jeder Zug an Tiefen oder Lichtern rechnete alle Dunst-Durchläufe neu, ebenso „Maske
  zeigen“ und die Luminanz-Schwelle (Bereichskarten). Und die Größenschätzung rechnete nach **jeder** Änderung (auch
  Qualität, Format, Schärfen, Körnung) die ganze Kette zweimal ohne Zwischenspeicher, Abbruch erst zwischen den Proben.
- **Lösung:** `Adjust.Prepare` = `Dehazed` (Weißabgleich + Dunst, Schlüssel `Adjustments.DehazeKey`) → `Finish`
  (Tiefen/Lichter, Bereichskarten). Vorschau: eigene Stufe `_dehazedCache`. Schätzung: `LookCache` (skaliert +
  entrauscht → Dunst → Tiefen/Lichter → Licht und Farbe; Schärfen und Körnung jedes Mal), über
  `ImageExporter.Render(…, cache)`, dasselbe Ergebnis wie ohne (`LookCacheTests` vergleicht Pixel und Stufenzahl);
  ein `WorkPlan` nur mit dem Abbruch bricht jetzt auch zwischen den Dunst-Durchläufen ab.
- **Korn 0,1 px (Wunsch):** Bis 0,3 px wurde das Korn gar nicht weichgezeichnet — 0,25 px war schon weißes Rauschen
  (jedes Pixel für sich), ein längerer Regler allein hätte nichts geändert. Feiner als ein Pixel heißt deshalb: weißes
  Rauschen minus ein Teil seiner Gauß-Fassung (1 px), Anteil 0 bei 0,25 px bis 1 bei 0,1 px, Streuung analytisch auf 1
  (1 − 2a·g₀² + a²·(Σg²)²) — grobe Häufungen fallen weg, Nachbarn laufen gegeneinander. Regler −27 … 100
  (`GrainSettings.MinSize`), Anzeige unter 1 px mit zwei Stellen (0,25 statt „0,3“). Messen bleibt bei ≥ 0,25 px.
- **Härte (Wunsch):** tanh(k · Korn), k = 4 · Härte/100, Streuung zurück auf 1 — aus weichen Gauß-Tupfen werden scharf
  begrenzte Körner. Auf feines, grobes und Farbkorn, nicht auf die Klumpen. 0 = wie bisher.

## 57. Mehrere Tooltips gleichzeitig offen beim Ziehen eines Reglers (0.5.67 beta)

- **Fehlerbild (Benutzer):** Regler mit gedrückter linker Taste ziehen und dabei nach unten auf den nächsten rutschen —
  beide Tooltips offen. Avalonia öffnet Tooltips auch bei gedrückter Taste und schließt den alten nicht, solange der
  Regler den Zeiger festhält.
- **Lösung (`App.TooltipsOnlyWhenIdle`, für alle Fenster):** Klassen-Handler auf `TopLevel` (Tunnel, auch behandelte
  Ereignisse) merken, ob eine Maustaste gedrückt ist; ein Druck schließt den offenen Tooltip. `ToolTip.ToolTipOpening`
  wird bei gedrückter Taste abgebrochen, öffnet einer, schließt der vorige — höchstens einer offen. Nach dem Loslassen
  kommt der Tooltip erst wieder beim nächsten Element (Avalonia startet die Wartezeit nur beim Hineinfahren).
- **Geprüft** am Fenster (Bildschirmfotos): Darüberfahren öffnet, gedrückt ziehen über zwei Regler öffnet keinen,
  danach öffnet der nächste Regler normal.

## 58. Gewölbtes Blatt: Linie blieb nach Esc aktiv, Esc warf aus dem Werkzeug, Abwählen statt Weiterzeichnen; Infozeile blieb stehen (0.5.68 beta)

- **Fehlerbilder (Benutzer):** Nach Esc blieb die zuletzt gezeichnete Linie markiert, und „Zeichnen“ machte an ihr weiter
  (Ursache: Esc beendete nur das Zeichnen, `_warpSel` blieb, und das Einschalten machte eine markierte Linie zur aktiven —
  das Verlängern aus #50). Mehrmals Esc verließ das Werkzeug (`WarpLines = null`) — alle Linien weg. Ein Klick auf einen
  Vorschlag wählte ihn ab (grau), statt etwas Nützliches zu tun; „Vorschlagen“ verwarf angepasste Vorschläge, die nur per
  Rechtsklick oder Pfeiltasten geändert waren.
- **Neu:** Esc beendet das Zeichnen und hebt die Markierung auf, verlässt das Werkzeug nie (nur das Symbol oben). „Zeichnen“
  (Knopf, N) beginnt immer eine neue Linie. Klick auf eine Linie oder einen Punkt ohne Ziehen (`BeginDrawingOn`) schaltet
  das Zeichnen ein und lässt sie am näheren Ende weiterwachsen; Ziehen erst ab 3 px (`WarpDragThreshold`). Kein
  `Enabled` mehr in `WarpLineShape` — kein Grau, Knopf „Graue weg“ entfallen. `ChangeWarpLine` macht jeden geänderten
  Vorschlag zur eigenen Linie (Punkte oder Richtung). „Vorschlagen“: unpassende Vorschläge (`Dewarp.RejectOutliers`)
  werden weggelassen statt abgewählt; Vorschläge, die auf einer eigenen Linie liegen (`WarpLineShape.Overlaps`, 60 % der
  Punkte innerhalb max(6 px, 0,6 % der langen Seite), unabhängig von der Richtung), kommen nicht noch einmal.
- **Nebenbei gefunden:** Die Infozeile („2 Linien · Ergebnis …“) blieb ab der zweiten Änderung stehen:
  `using var cts = _dewarpInfoCts = new …` entsorgte das Signal, der nächste Aufruf rief `Cancel()` darauf auf → Ausnahme
  im Hintergrund (fire-and-forget), die Zeile wurde nie mehr neu. **Nicht tun:** ein Feld-CTS mit `using` anlegen, das
  von außen abgebrochen wird — ohne Dispose oder nach Gebrauch auf null setzen (wie `AiViewModel._previewCts`).
- **Meldung unten:** stand in einem waagerechten StackPanel (unendlich Platz, kein Umbruch) und lief unter „Ziel“ und die
  Knöpfe — jetzt Grid mit Umbruch, höchstens drei Zeilen, ganzer Text als Tooltip.
- **Geprüft** am Fenster: zwei Linien, Esc, neue Linie getrennt; 4× Esc, Werkzeug bleibt; Klick auf Linie/Vorschlag
  zeichnet weiter, Vorschlag wird durchgezogen; Rechtsklick-geänderter Vorschlag bleibt, „2 Vorschläge lagen auf eigenen
  Linien“. `WarpLineShapeTests`.

## 59. Gewölbtes Blatt: nach „Gerade ziehen“ justieren statt Rückgängig (0.5.69 beta)

- **Wunsch des Benutzers:** die verwendeten Linien nach dem Geradeziehen nacharbeiten können — nicht über Rückgängig
  (das bleibt ein gewöhnlicher Schritt), sondern im Werkzeug.
- **Umsetzung:** `ApplyDewarpAsync` schließt das Werkzeug nicht mehr: `DewarpShowsResult` = Ergebnis zeigen
  (`ToolView` = alle Stufen), unten Info (`DescribeDewarp`) und „Justieren“ (`AdjustDewarpCommand` → Original mit den
  Linien aus dem Rezept, `EnterDewarpViewAsync`). Das Symbol öffnet immer beim Justieren (`OnDewarpToolChanged` setzt
  `DewarpShowsResult` zurück), ein Punkt am Symbol (`HasAppliedDewarpLines`, nur Linien, nicht KI) zeigt, dass Linien im
  Bild stecken; Stift an der Zeile „Gerade gezogen“ in „Bearbeitungen“ (`EditRow.Edit` = `EditDewarpLinesCommand`).
- **Falle:** `CommitRecipeAsync` → `ShowCurrentAsync` → `ApplySourceSize` schließt das Werkzeug („die Linien gelten nur
  für den Stand davor“) — beim eigenen Übernehmen per `_keepDewarpTool` übersprungen. Reihenfolge beim Umschalten:
  erst `DewarpShowsResult = true`, dann `DewarpLines = null`, sonst schließt `OnDewarpLinesChanged` das Werkzeug.
- **Geprüft** am Fenster: Vorschlagen → Gerade ziehen (Ergebnis, „Justieren“, Punkt) → Justieren (Original mit den
  Linien) → Symbol verlässt → Symbol öffnet direkt beim Justieren → Stift in „Bearbeitungen“ ebenso.
