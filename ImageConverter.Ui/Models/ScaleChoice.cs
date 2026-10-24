using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using ImageConverter.Core.Ai.Upscale;

namespace ImageConverter.Models;

/// <summary>Direction of scaling: same size (no dimensions yet), smaller, larger.</summary>
public enum ScaleDirection { Same, Down, Up }

/// <summary>
/// An entry of the "Verfahren" (method) list under "Größe" (size): a filter, an AI upscaler or the separator between
/// them (not selectable).
/// </summary>
/// <remarks>
/// The list is fixed; depending on the direction, entries are only hidden (<see cref="IsOffered"/>): when downscaling
/// the filters suited for it, when upscaling the suitable filters followed by the AI upscalers. Entries are hidden
/// instead of replacing the list because a new list resets the ComboBox selection, after which it shows nothing.
/// </remarks>
public sealed partial class ScaleChoice(FilterOption? filter, UpscaleOption? ai) : ObservableObject
{
    public FilterOption? Filter { get; } = filter;
    public UpscaleOption? Ai { get; } = ai;

    public bool IsHeader => Filter is null && Ai is null;
    public bool IsSelectable => !IsHeader;

    /// <summary>Whether the entry is offered for the current direction.</summary>
    [ObservableProperty] public partial bool IsOffered { get; set; } = true;

    public string Name => Filter?.Name ?? Ai?.Name ?? "—";

    /// <summary>Tooltip text: what the method does and what it is good for.</summary>
    public string? Description => Filter is { } f ? Tr.T(f.Description) + "\n\n" + Tr.T("Gut für:") + " " + Tr.T(f.BestFor)
        : Ai?.Description;

    /// <summary>
    /// Determines whether the entry belongs in the list for the given direction: when upscaling the filters suited for
    /// it and the AI upscalers, when downscaling only the filters suited for it; without a size change everything, so
    /// that the method can be chosen before the dimensions.
    /// </summary>
    /// <param name="direction">Current scaling direction.</param>
    /// <returns><c>true</c> if the entry is offered for <paramref name="direction"/>.</returns>
    public bool OfferedFor(ScaleDirection direction) => direction switch
    {
        ScaleDirection.Up => Filter is not { } f || f.Up,
        ScaleDirection.Down => Filter is { Down: true },
        _ => true,
    };

    public override string ToString() => Name;

    /// <summary>Creates all entries in display order: filters, separator, AI upscalers.</summary>
    /// <returns>A new list; each view model gets its own instance.</returns>
    public static IReadOnlyList<ScaleChoice> CreateList() =>
        FilterOption.All.Select(f => new ScaleChoice(f, null))
            .Append(new ScaleChoice(null, null))
            .Concat(UpscaleOption.All.Where(o => o.Method != UpscaleMethod.Filter).Select(o => new ScaleChoice(null, o)))
            .ToList();
}
