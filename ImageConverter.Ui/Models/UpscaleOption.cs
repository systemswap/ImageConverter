using System.Collections.Generic;
using System.Linq;
using ImageConverter.Core.Ai;
using ImageConverter.Core.Ai.Upscale;

namespace ImageConverter.Models;

/// <summary>An entry of the "Upscaling" choice under "Größe" (size).</summary>
public sealed record UpscaleOption(UpscaleMethod Method)
{
    public static IReadOnlyList<UpscaleOption> All { get; } =
        [new(UpscaleMethod.Filter), new(UpscaleMethod.Fast), new(UpscaleMethod.Medium), new(UpscaleMethod.Generative)];

    public static UpscaleOption Of(UpscaleMethod method) => All.First(o => o.Method == method);

    public AiModel? Model => Upscalers.ModelOf(Method);

    /// <summary>Name in the list: "KI" (AI), the method and its speed, so that it is visible which method produced the result.</summary>
    public string Name => Method switch
    {
        UpscaleMethod.Fast => Tr.T("KI Real-ESRGAN x4v3 · schnell"),
        UpscaleMethod.Medium => Tr.T("KI UltraSharpV2 · mittel"),
        UpscaleMethod.Generative => Tr.T("KI PiSA-SR · langsam"),
        _ => Tr.T("Nur Filter"),
    };

    public string Description => Method switch
    {
        UpscaleMethod.Fast => Tr.T("Real-ESRGAN x4v3: entfernt Rauschen und JPEG-Blöcke und macht Kanten klar. Sekunden je Bild, erfindet kaum etwas."),
        UpscaleMethod.Medium => Tr.T("4x-UltraSharpV2: schärfer und feiner als Real-ESRGAN x4v3, bleibt nah am Original. Deutlich langsamer — die geschätzte Zeit steht am Knopf „Skalieren“."),
        UpscaleMethod.Generative => Tr.T("PiSA-SR (Stable Diffusion): ergänzt glaubhafte feine Details wie Nadeln, Blätter, Haut — lokal auf diesem Rechner. Sehr langsam — die geschätzte Zeit steht am Knopf „Skalieren“."),
        _ => Tr.T("Wie bisher: der Filter oben rechnet die Pixel hoch. Sofort, erfindet nichts — wird aber weich."),
    };

    public override string ToString() => Name;
}
