using System.Collections.Generic;
using System.Linq;
using ImageConverter.Core.Ai;
using ImageConverter.Core.Ai.Upscale;

namespace ImageConverter.Models;

/// <summary>Ein Eintrag der Auswahl „Upscaling“ unter „Größe“.</summary>
public sealed record UpscaleOption(UpscaleMethod Method)
{
    public static IReadOnlyList<UpscaleOption> All { get; } =
        [new(UpscaleMethod.Filter), new(UpscaleMethod.Fast), new(UpscaleMethod.Medium), new(UpscaleMethod.Generative)];

    public static UpscaleOption Of(UpscaleMethod method) => All.First(o => o.Method == method);

    public AiModel? Model => Upscalers.ModelOf(Method);

    public string Name => Method switch
    {
        UpscaleMethod.Fast => Tr.T("KI schnell"),
        UpscaleMethod.Medium => Tr.T("KI mittel"),
        UpscaleMethod.Generative => Tr.T("KI wie Referenzwerkzeug"),
        _ => Tr.T("Nur Filter"),
    };

    public string Description => Method switch
    {
        UpscaleMethod.Fast => Tr.T("Real-ESRGAN x4v3: entfernt Rauschen und JPEG-Blöcke und macht Kanten klar. Sekunden je Bild, erfindet kaum etwas."),
        UpscaleMethod.Medium => Tr.T("4x-UltraSharpV2: schärfer und feiner als „KI schnell“, bleibt nah am Original. Etwa eine Minute je Megapixel."),
        UpscaleMethod.Generative => Tr.T("PiSA-SR (Stable Diffusion): ergänzt glaubhafte feine Details wie Nadeln, Blätter, Haut — wie Referenzwerkzeug, aber lokal. Sehr langsam: rund 40 Minuten je Megapixel des Originals."),
        _ => Tr.T("Wie bisher: der Filter oben rechnet die Pixel hoch. Sofort, erfindet nichts — wird aber weich."),
    };

    public override string ToString() => Name;
}
