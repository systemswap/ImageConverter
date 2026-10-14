using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using ImageConverter.Core.Ai.Upscale;

namespace ImageConverter.Models;

/// <summary>
/// Ein Eintrag der Liste „Verfahren“ unter „Größe“: ein Filter, ein KI-Scaler oder die Trennlinie dazwischen (nicht
/// wählbar). Die Liste ist fest; je nach Richtung werden Einträge nur ausgeblendet (<see cref="IsOffered"/>) — beim
/// Verkleinern Filter, die dafür taugen, beim Vergrößern die passenden Filter und darunter die KI-Scaler. Ausblenden statt
/// Austauschen: Eine neue Liste setzt die Auswahl der ComboBox zurück, und sie zeigte danach nichts mehr an.
/// </summary>
public sealed partial class ScaleChoice(FilterOption? filter, UpscaleOption? ai) : ObservableObject
{
    public FilterOption? Filter { get; } = filter;
    public UpscaleOption? Ai { get; } = ai;

    public bool IsHeader => Filter is null && Ai is null;
    public bool IsSelectable => !IsHeader;

    /// <summary>In der aktuellen Richtung angeboten.</summary>
    [ObservableProperty] public partial bool IsOffered { get; set; } = true;

    public string Name => Filter?.Name ?? Ai?.Name ?? "—";

    /// <summary>Tooltip: was das Verfahren tut und wofür es gut ist.</summary>
    public string? Description => Filter is { } f ? Tr.T(f.Description) + "\n\n" + Tr.T("Gut für:") + " " + Tr.T(f.BestFor)
        : Ai?.Description;

    /// <summary>Gehört in die Liste für diese Richtung (Trennlinie und KI-Scaler nur beim Vergrößern).</summary>
    public bool OfferedFor(bool enlarging) => Filter is { } f ? (enlarging ? f.Up : f.Down) : enlarging;

    public override string ToString() => Name;

    /// <summary>Alle Einträge in Anzeigereihenfolge: Filter, Trennlinie, KI-Scaler (je ViewModel eine eigene Liste).</summary>
    public static IReadOnlyList<ScaleChoice> CreateList() =>
        FilterOption.All.Select(f => new ScaleChoice(f, null))
            .Append(new ScaleChoice(null, null))
            .Concat(UpscaleOption.All.Where(o => o.Method != UpscaleMethod.Filter).Select(o => new ScaleChoice(null, o)))
            .ToList();
}
