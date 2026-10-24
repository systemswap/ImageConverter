using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageConverter.Controls;
using ImageConverter.Core.Imaging;

namespace ImageConverter.ViewModels;

// White balance with three eyedroppers (neutral, black, white): the "White balance" group in the standard section.
public partial class MainViewModel
{
    /// <summary>Averaged measurements per eyedropper; null = not used. Part of the settings state (undo).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WbNeutralBrush), nameof(WbNeutralText), nameof(HasWbNeutral), nameof(HasWhiteBalance))]
    public partial WbSample? WbNeutral { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WbBlackBrush), nameof(WbBlackText), nameof(HasWbBlack), nameof(HasWhiteBalance))]
    public partial WbSample? WbBlack { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WbWhiteBrush), nameof(WbWhiteText), nameof(HasWbWhite), nameof(HasWhiteBalance))]
    public partial WbSample? WbWhite { get; set; }

    partial void OnWbNeutralChanged(WbSample? value) => WbChanged(WbPoint.Neutral, value);
    partial void OnWbBlackChanged(WbSample? value) => WbChanged(WbPoint.Black, value);
    partial void OnWbWhiteChanged(WbSample? value) => WbChanged(WbPoint.White, value);

    public bool HasWbNeutral => WbNeutral is not null;
    public bool HasWbBlack => WbBlack is not null;
    public bool HasWbWhite => WbWhite is not null;
    public bool HasWhiteBalance => WhiteBalance.IsActive(WbNeutral, WbBlack, WbWhite);

    public IBrush? WbNeutralBrush => BrushOf(WbNeutral);
    public IBrush? WbBlackBrush => BrushOf(WbBlack);
    public IBrush? WbWhiteBrush => BrushOf(WbWhite);

    /// <summary>"Neutral" or "Neutral 3×": how many clicks are averaged.</summary>
    public string WbNeutralText => Named(Tr.T("Neutral"), WbNeutral);
    public string WbBlackText => Named(Tr.T("Schwarz"), WbBlack);
    public string WbWhiteText => Named(Tr.T("Weiß"), WbWhite);

    private static string Named(string name, WbSample? s) => s is { Count: > 1 } m ? $"{name} {m.Count}×" : name;

    private static IBrush? BrushOf(WbSample? s)
    {
        if (s is not { } m) return null;
        var (r, g, b) = m.ToSrgb8();
        return new SolidColorBrush(Color.FromRgb(r, g, b));
    }

    // ------------------------------------------------------------ Eyedroppers

    /// <summary>The eyedropper currently measuring; null = none. It stays active, and every click into the image is averaged in.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PickNeutral), nameof(PickBlack), nameof(PickWhite), nameof(IsPicking), nameof(WbHint))]
    public partial WbPoint? ActivePipette { get; set; }

    public bool IsPicking
    {
        get => ActivePipette is not null;
        set { if (!value) ActivePipette = null; }   // Esc in the preview
    }

    public bool PickNeutral { get => ActivePipette == WbPoint.Neutral; set => SetPipette(WbPoint.Neutral, value); }
    public bool PickBlack { get => ActivePipette == WbPoint.Black; set => SetPipette(WbPoint.Black, value); }
    public bool PickWhite { get => ActivePipette == WbPoint.White; set => SetPipette(WbPoint.White, value); }

    private void SetPipette(WbPoint kind, bool on)
    {
        if (on)
        {
            // The measurement is taken on the unrotated pixel image; close the rotation, perspective and curved-page tools.
            RotationTool = false;
            PerspectiveTool = false;
            DewarpTool = false;
            _wbProblem = null;
            ActivePipette = kind;
        }
        else if (ActivePipette == kind) ActivePipette = null;
    }

    partial void OnActivePipetteChanged(WbPoint? value) => OnPropertyChanged(nameof(IsPicking));

    /// <summary>A click into the image with an active eyedropper: measures and averages with the previous measurements of this eyedropper.</summary>
    /// <param name="at">Click position in pixels of the displayed image.</param>
    [RelayCommand]
    private void PickAt(Point at)
    {
        if (ActivePipette is not { } kind || CurrentImage is not { } image) return;

        // The click is in the displayed image (rotated, cropped …), the measurement in the pixel image.
        var (px, py) = ShownToPixels(at.X, at.Y);
        var sample = WhiteBalance.Measure(image, px, py, kind);
        if (sample is not { } s)
        {
            _wbProblem = Tr.T("Diese Stelle ist ausgefressen (reines Weiß, keine Farbe mehr) – bitte eine etwas dunklere Stelle wählen.");
            OnPropertyChanged(nameof(WbHint));
            return;
        }

        _wbProblem = null;
        _wbMarkers.Add(new PickMarker(at, MarkerLabel(kind), kind));
        switch (kind)
        {
            case WbPoint.Neutral: WbNeutral = WbNeutral?.Add(s) ?? s; break;
            case WbPoint.Black: WbBlack = WbBlack?.Add(s) ?? s; break;
            case WbPoint.White: WbWhite = WbWhite?.Add(s) ?? s; break;
        }
        OnPropertyChanged(nameof(WbHint));
    }

    /// <summary>Deletes the measurements of one eyedropper (for example after a misclick), as one undo step.</summary>
    /// <param name="kind">Name of a <see cref="WbPoint"/> value.</param>
    [RelayCommand]
    private void ClearPipette(string kind)
    {
        if (!Enum.TryParse<WbPoint>(kind, out var k)) return;
        switch (k)
        {
            case WbPoint.Neutral: WbNeutral = null; break;
            case WbPoint.Black: WbBlack = null; break;
            case WbPoint.White: WbWhite = null; break;
        }
    }

    /// <summary>Resets all three eyedroppers, as one undo step.</summary>
    [RelayCommand]
    private void ResetWhiteBalance()
    {
        WbNeutral = null;
        WbBlack = null;
        WbWhite = null;
        ActivePipette = null;
    }

    private string? _wbProblem;

    /// <summary>What to do: per eyedropper, what to pick, or what went wrong at the last spot.</summary>
    public string WbHint => _wbProblem ?? ActivePipette switch
    {
        WbPoint.Neutral => Tr.T("Neutral: auf etwas klicken, das in Wirklichkeit farblos ist – egal ob hell oder dunkel, z. B. Asphalt, Beton, eine graue Wolke, weißes Papier im Schatten. Nicht: grelles Weiß, fast Schwarzes, Beige oder Holz. Mehrere Klicks werden gemittelt; Esc beendet."),
        WbPoint.Black => Tr.T("Schwarz: auf die dunkelste Stelle klicken, die schwarz sein sollte. Mehrere Klicks werden gemittelt; Esc beendet."),
        WbPoint.White => Tr.T("Weiß: auf die hellste Stelle klicken, die weiß sein sollte, aber noch Zeichnung hat (nicht ausgefressen). Mehrere Klicks werden gemittelt; Esc beendet."),
        _ => Tr.T("Pipette wählen und ins Bild klicken. Eine Pipette reicht – Neutral (grau) ist meist die beste. Mit Schwarz und Weiß dazu und mit mehreren Klicks wird es genauer."),
    };

    // ------------------------------------------------------------ Markers in the image

    private readonly List<PickMarker> _wbMarkers = [];

    /// <summary>Measuring points to show in the preview (for the current image only).</summary>
    [ObservableProperty] public partial IReadOnlyList<PickMarker>? WbMarkers { get; set; }

    private static string MarkerLabel(WbPoint kind) => kind switch
    {
        WbPoint.Neutral => Tr.T("N"),
        WbPoint.Black => Tr.T("S"),
        _ => Tr.T("W"),
    };

    /// <summary>Measurement changed (click, ×, undo): trims the markers to the number of clicks and refreshes the preview.</summary>
    private void WbChanged(WbPoint kind, WbSample? value)
    {
        var keep = value?.Count ?? 0;
        var mine = _wbMarkers.Where(m => m.Kind == kind).ToList();
        foreach (var extra in mine.Skip(keep)) _wbMarkers.Remove(extra);
        WbMarkers = _wbMarkers.Count == 0 ? null : _wbMarkers.ToArray();
        ToolChanged(nameof(WbHint));
    }

    /// <summary>New image state: the markers belong to the previous image, and the eyedropper stops.</summary>
    private void ResetWbForNewImage()
    {
        _wbMarkers.Clear();
        WbMarkers = null;
        ActivePipette = null;
        _wbProblem = null;
    }
}
