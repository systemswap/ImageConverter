using System.Collections.Generic;

namespace ImageConverter.Models;

/// <summary>Ein Skalierungsfilter mit kurzer, laienverständlicher Erklärung für den Tooltip.</summary>
public sealed record FilterOption(string Name, string Description, string BestFor)
{
    public override string ToString() => Name;

    public static IReadOnlyList<FilterOption> All { get; } =
    [
        new("Lanczos3",
            "Bezieht viele Nachbarpixel ein und hält dadurch feine Details wie Haare, Blätter oder Schrift scharf. An harten Kanten kann ein leichter heller Saum entstehen.",
            "Fotos verkleinern — die beste Wahl für fast alles."),

        new("Bicubic",
            "Glättet weich und natürlich. Etwas weniger scharf als Lanczos, dafür ohne Säume an Kanten.",
            "Vergrößern und als sicherer Allrounder."),

        new("Catmull-Rom",
            "Fast so knackig wie Lanczos, erzeugt aber weniger helle Säume an Kanten.",
            "Bilder mit Text, Logos oder Grafiken."),

        new("Mitchell",
            "Bewusst ausgewogen zwischen scharf und weich — weder Säume noch Treppchen fallen auf.",
            "Vergrößern und leicht verrauschte Bilder."),

        new("Spline",
            "Glättet stark: Rauschen und Treppenstufen verschwinden, das Bild wird aber spürbar weicher.",
            "Verrauschte Handy- oder Nachtfotos."),

        new("Bilinear",
            "Schnell und einfach, Ergebnis eher weich. Beim starken Verkleinern können flimmernde Muster entstehen.",
            "Kleine Größenänderungen und schnelle Vorschauen."),

        new("Box / Area",
            "Fasst jeweils einen Block Pixel zu einem zusammen und nimmt den Durchschnitt. Sehr sauber beim Verkleinern, beim Vergrößern blockig.",
            "Starkes Verkleinern, z. B. auf Vorschaubild-Größe."),

        new("Nearest",
            "Übernimmt einfach den nächstgelegenen Pixel, ohne zu glätten — Kanten bleiben hart und pixelig.",
            "Pixel-Art, Icons und Screenshots. Für Fotos ungeeignet."),
    ];
}
