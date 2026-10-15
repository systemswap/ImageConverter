using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ImageConverter.Core.Platform;

/// <summary>
/// Eigenes Log neben dem Programm („ImageConverter.log“): Abstürze und unbehandelte Fehler — man muss nicht in der
/// Ereignisanzeige suchen. Ein Absturz in nativem Code (ONNX Runtime, ImageMagick) lässt sich in .NET nicht abfangen; dafür
/// merkt sich jede laufende App in einer kleinen Datei, was sie gerade rechnet („ImageConverter.&lt;pid&gt;.laeuft“). Fehlt
/// beim nächsten Start das saubere Ende, kommt der Absturz mit der letzten Tätigkeit ins Log — unter Windows mit dem Eintrag
/// der Ereignisanzeige (Stapel), den Windows selbst schreibt.
/// </summary>
public static class AppLog
{
    public const string FileName = "ImageConverter.log";
    private const string MarkerPrefix = "ImageConverter.";
    private const string MarkerSuffix = ".laeuft";
    private const long MaxBytes = 1 << 20;   // darüber: alte Hälfte nach „.log.alt“

    private static readonly Lock Gate = new();
    private static string? _folder;
    private static string? _marker;
    private static DateTime _started;
    private static string? _activity;
    private static DateTime _activitySince;

    /// <summary>Pfad des Logs, null wenn kein beschreibbarer Ordner gefunden wurde.</summary>
    public static string? LogPath => _folder is null ? null : Path.Combine(_folder, FileName);

    /// <summary>Beim Start: Ordner wählen, Abstürze vom letzten Mal nachtragen, eigene Lauf-Datei anlegen, Fehler abonnieren.</summary>
    public static void Start(string? folder = null)
    {
        _folder = folder ?? WritableFolder();
        if (_folder is null) return;
        _started = DateTime.Now;
        var pid = Environment.ProcessId;
        _marker = Path.Combine(_folder, MarkerPrefix + pid + MarkerSuffix);

        // Lauf-Dateien anderer Prozesse: läuft der Prozess nicht mehr, endete er ohne sauberes Ende
        var crashed = new List<string>();
        foreach (var file in SafeFiles(_folder))
            if (PidOf(file) is { } other && other != pid && !IsRunning(other)) crashed.Add(file);
        WriteMarker();
        if (crashed.Count > 0) _ = Task.Run(() => ReportCrashes(crashed));

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("Unbehandelter Fehler — Programm beendet", e.ExceptionObject.ToString());
        TaskScheduler.UnobservedTaskException += (_, e) =>
            Write("Fehler in einer Hintergrundaufgabe (nicht abgefangen)", e.Exception.ToString());
    }

    /// <summary>Sauberes Ende: Lauf-Datei weg.</summary>
    public static void Stop()
    {
        if (_marker is null) return;
        try { File.Delete(_marker); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Was gerade rechnet (KI-Modell, Upscaling …) — steht nach einem Absturz im Log. null = nichts mehr.</summary>
    public static void Activity(string? what)
    {
        _activity = what;
        _activitySince = DateTime.Now;
        WriteMarker();
    }

    /// <summary>Eintrag mit Zeit, Überschrift und eingerücktem Text.</summary>
    public static void Write(string title, string? details = null)
    {
        if (LogPath is not { } path) return;
        var text = new StringBuilder();
        text.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append("  ").AppendLine(title);
        if (!string.IsNullOrWhiteSpace(details))
            foreach (var line in details.Replace("\r", "").TrimEnd().Split('\n'))
                text.Append("    ").AppendLine(line);
        lock (Gate)
        {
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes) File.Move(path, path + ".alt", overwrite: true);
                File.AppendAllText(path, text.ToString());
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    // ------------------------------------------------------------ intern

    private static Encoding AnsiEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            return Encoding.Latin1;
        }
    }

    /// <summary>Zeit eines Ereignisses aus der Zeile „Date: …“ von wevtutil.</summary>
    private static DateTime? DateOf(string evt)
    {
        var i = evt.IndexOf("Date: ", StringComparison.Ordinal);
        if (i < 0) return null;
        var end = evt.IndexOfAny(['\r', '\n'], i);
        var text = evt[(i + 6)..(end < 0 ? evt.Length : end)].Trim();
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
    }

    /// <summary>Neben dem Programm, wenn dort geschrieben werden darf; sonst im Benutzerordner.</summary>
    private static string? WritableFolder()
    {
        foreach (var dir in new[]
                 {
                     AppLocations.ProgramFolder,
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImageConverter"),
                 })
        {
            try
            {
                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, FileName);
                using (File.Open(probe, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { }
                return dir;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    private static void WriteMarker()
    {
        if (_marker is null) return;
        var lines = new[]
        {
            _started.ToString("o", CultureInfo.InvariantCulture),
            _activity is null ? "" : _activitySince.ToString("o", CultureInfo.InvariantCulture),
            _activity ?? "",
        };
        try { File.WriteAllLines(_marker, lines); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static IEnumerable<string> SafeFiles(string folder)
    {
        try { return Directory.GetFiles(folder, MarkerPrefix + "*" + MarkerSuffix); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    private static int? PidOf(string file)
    {
        var name = Path.GetFileName(file);
        var middle = name[MarkerPrefix.Length..^MarkerSuffix.Length];
        return int.TryParse(middle, out var pid) ? pid : null;
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited && p.ProcessName.StartsWith("ImageConverter", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return false; }
    }

    /// <summary>Für jede verwaiste Lauf-Datei: Absturz ins Log, mit letzter Tätigkeit und (Windows) der Ereignisanzeige.</summary>
    private static void ReportCrashes(IEnumerable<string> markers)
    {
        foreach (var marker in markers)
        {
            string[] lines;
            try { lines = File.ReadAllLines(marker); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            var started = lines.Length > 0 && DateTime.TryParse(lines[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var s)
                ? s : (DateTime?)null;
            var since = lines.Length > 1 && DateTime.TryParse(lines[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var a)
                ? a : (DateTime?)null;
            var activity = lines.Length > 2 && lines[2].Length > 0 ? lines[2] : null;

            var details = new StringBuilder();
            if (started is { } st) details.AppendLine("Gestartet: " + st.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            details.AppendLine(activity is null
                ? "Zuletzt: keine Rechnung (Absturz bei der Bedienung)"
                : $"Zuletzt: {activity} (seit {since?.ToString("HH:mm:ss", CultureInfo.InvariantCulture)})");
            if (OperatingSystem.IsWindows() && started is { } from && EventLogSince(from, PidOf(marker)) is { Length: > 0 } events)
                details.AppendLine().AppendLine("Ereignisanzeige (Windows):").Append(events);
            Write("Absturz — das Programm wurde unerwartet beendet", details.ToString());
            try { File.Delete(marker); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Einträge von „Application Error“ (mit Prozess-ID) und „.NET Runtime“ (mit Stapel, ohne Prozess-ID — der zeitlich
    /// passende davor) zu diesem Absturz — über wevtutil (gehört zu Windows), ohne zusätzliches Paket. Leer, wenn nichts
    /// gefunden oder nicht lesbar.
    /// </summary>
    private static string EventLogSince(DateTime from, int? pid)
    {
        try
        {
            var utc = from.ToUniversalTime().AddSeconds(-5).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
            var psi = new ProcessStartInfo("wevtutil")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                // wevtutil schreibt umgeleitet in der ANSI-Codepage (deutsch: 1252)
                StandardOutputEncoding = AnsiEncoding(),
            };
            foreach (var arg in new[]
                     {
                         "qe", "Application", "/rd:true", "/c:20", "/f:text",
                         $"/q:*[System[(Provider[@Name='.NET Runtime'] or Provider[@Name='Application Error']) and TimeCreated[@SystemTime>='{utc}Z']]]",
                     })
                psi.ArgumentList.Add(arg);
            using var p = Process.Start(psi);
            if (p is null) return "";
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(10_000) || !output.Wait(1000)) return "";
            // Ein Ereignis beginnt mit „Event[n]“; nur die zu diesem Programm
            var events = output.Result.Replace("\0", "").Split("Event[", StringSplitOptions.RemoveEmptyEntries)
                .Where(e => e.Contains("ImageConverter", StringComparison.OrdinalIgnoreCase))
                .Select(e => (Text: "Event[" + e.TrimEnd(), Date: DateOf(e), Runtime: e.Contains(".NET Runtime", StringComparison.Ordinal)))
                .ToList();
            // Absturzmeldung dieses Prozesses (neueste zuerst); ohne Prozess-ID die neueste
            var crash = events.FirstOrDefault(e => !e.Runtime && (pid is not { } id || e.Text.Contains($"0x{id:X}", StringComparison.OrdinalIgnoreCase)));
            if (crash.Text is null) crash = events.FirstOrDefault(e => !e.Runtime && pid is null);
            var at = crash.Text is null ? (DateTime?)null : crash.Date;
            var runtime = events.FirstOrDefault(e => e.Runtime && (at is not { } t || e.Date is { } d && d <= t.AddSeconds(2) && d >= t.AddSeconds(-30)));
            return string.Join(Environment.NewLine + Environment.NewLine,
                new[] { runtime.Text, crash.Text }.Where(t => t is not null));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return "";
        }
    }
}
