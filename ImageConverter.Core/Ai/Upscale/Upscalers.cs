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

    // Sekunden je Megapixel des Originals (gemessen am Panorama-Ausschnitt 500 × 300 → 2000 × 1200 auf einem Ryzen 7
    // Notebook-Prozessor; CPU: x4v3 3,5 s, UltraSharpV2 2 min, PiSA-SR 6 min; Grafikkarte = dessen Radeon über DirectML). Nach jedem Lauf
    // gilt die gemessene Zeit dieses Rechners.
    private static readonly Dictionary<(UpscaleMethod, bool Gpu), double> SecondsPerMegapixel = new()
    {
        [(UpscaleMethod.Fast, false)] = 23,
        [(UpscaleMethod.Medium, false)] = 800,
        [(UpscaleMethod.Generative, false)] = 2370,
        [(UpscaleMethod.Fast, true)] = GpuFast,
        [(UpscaleMethod.Medium, true)] = GpuMedium,
        [(UpscaleMethod.Generative, true)] = GpuGenerative,
    };

    // Grafikkarte (Radeon des Notebook-Prozessor über DirectML), gemessen am selben Ausschnitt
    private const double GpuFast = 13, GpuMedium = 530, GpuGenerative = 910;   // 2,0 s, 80 s, 137 s für 0,15 MP

    /// <summary>Rechnen die Modelle gerade auf der Grafikkarte? (setzt die Oberfläche nach den Einstellungen)</summary>
    public static bool OnGpu { get; set; }

    /// <summary>Geschätzte Rechenzeit für ein Original dieser Größe (ohne einmaliges Umwandeln/Laden).</summary>
    public static TimeSpan Estimate(UpscaleMethod method, int width, int height) =>
        method == UpscaleMethod.Filter ? TimeSpan.Zero
            : TimeSpan.FromSeconds(SecondsPerMegapixel[(method, OnGpu)] * width * (double)height / 1e6);

    /// <summary>Gemessene Zeit eines Laufs merken (verfeinert die Schätzung; kleine Läufe zählen weniger).</summary>
    public static void Measured(UpscaleMethod method, int width, int height, TimeSpan elapsed)
    {
        var mp = width * (double)height / 1e6;
        if (method == UpscaleMethod.Filter || mp < 0.01) return;
        var weight = Math.Min(1, mp / 0.3);
        var key = (method, OnGpu);
        lock (SecondsPerMegapixel)
            SecondsPerMegapixel[key] = SecondsPerMegapixel[key] * (1 - weight) + elapsed.TotalSeconds / mp * weight;
    }

    /// <summary>„3 s“, „4 min“, „2 h 10 min“.</summary>
    public static string Format(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{Math.Max(1, Math.Round(t.TotalSeconds)):0} s"
        : t.TotalMinutes < 60 ? $"{Math.Round(t.TotalMinutes):0} min"
        : $"{(int)t.TotalHours} h {t.Minutes:00} min";
}
