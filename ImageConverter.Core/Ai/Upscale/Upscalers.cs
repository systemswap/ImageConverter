namespace ImageConverter.Core.Ai.Upscale;

/// <summary>Womit beim Vergrößern die Pixel entstehen (Gruppe „Upscaling“ unter „Größe“).</summary>
public enum UpscaleMethod
{
    /// <summary>Nur der Filter (Lanczos …) — wie bisher.</summary>
    Filter,

    /// <summary>Real-ESRGAN x4v3: Sekunden.</summary>
    Fast,

    /// <summary>4x-UltraSharpV2: Minuten.</summary>
    Medium,

    /// <summary>PiSA-SR: erfindet feine Details, sehr langsam.</summary>
    Generative,
}

/// <summary>Durchgänge (1 oder 2) und Größe des Bildes, das der letzte Durchgang ×4 vergrößert.</summary>
public readonly record struct UpscalePlan(int Passes, int BaseWidth, int BaseHeight);

public static class Upscalers
{
    /// <summary>Das Modell zum Verfahren (null = Filter).</summary>
    public static AiModel? ModelOf(UpscaleMethod method) => method switch
    {
        UpscaleMethod.Fast => AiModelCatalog.Allround,
        UpscaleMethod.Medium => AiModelCatalog.UltraSharpV2,
        UpscaleMethod.Generative => AiModelCatalog.PiSaSr,
        _ => null,
    };

    // Vorgaben: Sekunden je Megapixel des Originals, gemessen am Panorama-Ausschnitt 500 × 300 → 2000 × 1200 auf einem
    // Notebook-Prozessor (CPU: x4v3 3,5 s, UltraSharpV2 2 min, PiSA-SR 6 min; Grafikkarte = dessen Radeon über DirectML).
    // Geschätzt wird über den Rechenaufwand (Kacheln samt Überlappung, Work) statt über die Fläche — bei kleinen Bildern
    // überlappen die Kacheln anteilig mehr, die Fläche allein lag dort daneben. Gemessene Werte dieses Rechners
    // (Geschwindigkeitstest, jeder Lauf) ersetzen die Vorgaben und werden gespeichert (Learned/Restore) — sonst schätzte
    // ein schneller Rechner nach jedem Start mit den Laptop-Werten (Ryzen 9 7950X3D: PiSA-SR etwa 6× so schnell).
    private static readonly Dictionary<(UpscaleMethod, bool Gpu), double> DefaultSecondsPerMegapixel = new()
    {
        [(UpscaleMethod.Fast, false)] = 23,
        [(UpscaleMethod.Medium, false)] = 800,
        [(UpscaleMethod.Generative, false)] = 2370,
        [(UpscaleMethod.Fast, true)] = 13,          // Grafikkarte: 2,0 s, 80 s, 97 s (fp16) für 0,15 MP
        [(UpscaleMethod.Medium, true)] = 530,
        [(UpscaleMethod.Generative, true)] = 650,
    };

    /// <summary>Sekunden je Aufwandseinheit (<see cref="Work"/>) je Verfahren und Gerät.</summary>
    private static readonly Dictionary<(UpscaleMethod, bool Gpu), double> SecondsPerWork =
        DefaultSecondsPerMegapixel.ToDictionary(p => p.Key, p => p.Value * 0.15 / Work(p.Key.Item1, 500, 300));

    /// <summary>Rechenaufwand für ein Original dieser Größe: Kacheln (Kachelmodelle) bzw. Kachelflächen (PiSA-SR).</summary>
    public static double Work(UpscaleMethod method, int width, int height) => method switch
    {
        UpscaleMethod.Generative => PiSaSrRun.Work(width, height),
        UpscaleMethod.Fast or UpscaleMethod.Medium => UpscaleEngine.TileCount(width, height, ModelOf(method)!.TileSize),
        _ => 0,
    };

    /// <summary>Rechnen die Modelle gerade auf der Grafikkarte? (setzt die Oberfläche nach den Einstellungen)</summary>
    public static bool OnGpu { get; set; }

    /// <summary>Geschätzte Rechenzeit für ein Original dieser Größe (ohne einmaliges Umwandeln/Laden).</summary>
    public static TimeSpan Estimate(UpscaleMethod method, int width, int height)
    {
        if (method == UpscaleMethod.Filter || width <= 0 || height <= 0) return TimeSpan.Zero;
        lock (SecondsPerWork) return TimeSpan.FromSeconds(SecondsPerWork[(method, OnGpu)] * Work(method, width, height));
    }

    /// <summary>
    /// Wie gerechnet wird: ein KI-Durchgang (×4, den Rest macht der Filter) oder — über 4× und wenn gewünscht — zwei. Beim
    /// zweiten wird das Zwischenbild ×4 erst auf ein Viertel des Ziels verkleinert (höchstens ×4 des Bildes), so landet der
    /// zweite Durchgang genau beim Ziel und rechnet nicht mehr als nötig. Basis = das Bild, das der letzte Durchgang vergrößert.
    /// </summary>
    public static UpscalePlan Plan(int width, int height, int targetWidth, int targetHeight, bool twice)
    {
        if (!twice || (targetWidth <= 4 * width && targetHeight <= 4 * height)) return new UpscalePlan(1, width, height);
        return new UpscalePlan(2, Math.Clamp((targetWidth + 3) / 4, width, 4 * width), Math.Clamp((targetHeight + 3) / 4, height, 4 * height));
    }

    /// <summary>Mehr als 4× (in einer Kante) — dann ist ein zweiter Durchgang möglich.</summary>
    public static bool BeyondOnePass(int width, int height, int targetWidth, int targetHeight) =>
        targetWidth > 4 * width || targetHeight > 4 * height;

    /// <summary>Geschätzte Zeit nach Plan für das Bild (<paramref name="width"/> × <paramref name="height"/>) oder einen Ausschnitt davon.</summary>
    public static TimeSpan Estimate(UpscaleMethod method, int width, int height, UpscalePlan plan, int partWidth = 0, int partHeight = 0)
    {
        if (partWidth <= 0 || partHeight <= 0) (partWidth, partHeight) = (width, height);
        var t = Estimate(method, partWidth, partHeight);
        if (plan.Passes == 2)
            t += Estimate(method, (int)Math.Round(partWidth * (double)plan.BaseWidth / width),
                (int)Math.Round(partHeight * (double)plan.BaseHeight / height));
        return t;
    }

    /// <summary>Werte, die auf diesem Rechner gemessen wurden (nicht die Vorgaben).</summary>
    private static readonly HashSet<(UpscaleMethod, bool)> LearnedKeys = [];

    /// <summary>
    /// Gemessene Zeit eines Laufs merken (verfeinert die Schätzung; kleine Läufe zählen weniger). Die erste Messung eines
    /// Rechners und jede aus dem Geschwindigkeitstest (<paramref name="replace"/>) ersetzen den Wert ganz.
    /// </summary>
    public static void Measured(UpscaleMethod method, int width, int height, TimeSpan elapsed, bool replace = false)
    {
        var mp = width * (double)height / 1e6;
        var work = Work(method, width, height);
        if (method == UpscaleMethod.Filter || mp < 0.002 || work <= 0) return;
        var key = (method, OnGpu);
        lock (SecondsPerWork)
        {
            var weight = LearnedKeys.Add(key) || replace ? 1 : Math.Min(1, mp / 0.3);
            SecondsPerWork[key] = SecondsPerWork[key] * (1 - weight) + elapsed.TotalSeconds / work * weight;
        }
    }

    /// <summary>Die gemessenen Werte zum Speichern („Generative|cpu“ → Sekunden je Aufwandseinheit).</summary>
    public static Dictionary<string, double> Learned()
    {
        lock (SecondsPerWork)
            return LearnedKeys.ToDictionary(k => $"{k.Item1}|{(k.Item2 ? "gpu" : "cpu")}", k => SecondsPerWork[k]);
    }

    /// <summary>Gespeicherte Werte dieses Rechners übernehmen (unbekannte oder unsinnige Einträge werden übergangen).</summary>
    public static void Restore(IReadOnlyDictionary<string, double>? saved)
    {
        if (saved is null) return;
        lock (SecondsPerWork)
            foreach (var (name, seconds) in saved)
            {
                var parts = name.Split('|');
                if (parts.Length != 2 || !Enum.TryParse<UpscaleMethod>(parts[0], out var method) || method == UpscaleMethod.Filter
                    || parts[1] is not ("cpu" or "gpu") || !double.IsFinite(seconds) || seconds <= 0) continue;
                var key = (method, parts[1] == "gpu");
                SecondsPerWork[key] = seconds;
                LearnedKeys.Add(key);
            }
    }

    /// <summary>
    /// Dauer mit Uhrzeit, wann es fertig ist: „4 min, fertig gegen 15:42“ (am nächsten Tag mit Datum). Unter einer
    /// Minute nur die Dauer.
    /// </summary>
    public static string FormatWithEnd(TimeSpan t, DateTime now)
    {
        if (t.TotalSeconds < 60) return Format(t);
        var end = now + t;
        var clock = end.Date == now.Date ? end.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)
            : end.ToString("dd.MM. HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        return Tr.T("{0}, fertig gegen {1}", Format(t), clock);
    }

    /// <summary>„3 s“, „4 min“, „2 h 10 min“.</summary>
    public static string Format(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{Math.Max(1, Math.Round(t.TotalSeconds)):0} s"
        : t.TotalMinutes < 60 ? $"{Math.Round(t.TotalMinutes):0} min"
        : $"{(int)t.TotalHours} h {t.Minutes:00} min";
}
