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

// Weißabgleich mit drei Pipetten (Neutral, Schwarz, Weiß) — Gruppe „Weißabgleich“ in der Rubrik Standard.
public partial class MainViewModel
{
    /// <summary>Gemittelte Messungen je Pipette; null = nicht benutzt. Gehören zum Einstellungsstand (Rückgängig).</summary>
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

    /// <summary>„Neutral“ bzw. „Neutral 3×“ — wie viele Klicks gemittelt sind.</summary>
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

    // ------------------------------------------------------------ Pipetten

    /// <summary>Welche Pipette gerade misst; null = keine. Sie bleibt an — jeder Klick ins Bild wird gemittelt.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PickNeutral), nameof(PickBlack), nameof(PickWhite), nameof(IsPicking), nameof(WbHint))]
    public partial WbPoint? ActivePipette { get; set; }

    public bool IsPicking
    {
        get => ActivePipette is not null;
        set { if (!value) ActivePipette = null; }   // Esc in der Vorschau
    }

    public bool PickNeutral { get => ActivePipette == WbPoint.Neutral; set => SetPipette(WbPoint.Neutral, value); }
    public bool PickBlack { get => ActivePipette == WbPoint.Black; set => SetPipette(WbPoint.Black, value); }
    public bool PickWhite { get => ActivePipette == WbPoint.White; set => SetPipette(WbPoint.White, value); }

    private void SetPipette(WbPoint kind, bool on)
    {
        if (on)
        {
            // Gemessen wird am ungedrehten Bild — Drehen und Entzerren schließen
            RotationTool = false;
            PerspectiveTool = false;
            _wbProblem = null;
            ActivePipette = kind;
        }
        else if (ActivePipette == kind) ActivePipette = null;
    }

    partial void OnActivePipetteChanged(WbPoint? value) => OnPropertyChanged(nameof(IsPicking));

    /// <summary>Ein Klick ins Bild mit aktiver Pipette: messen und zu den bisherigen Messungen dieser Pipette mitteln.</summary>
    [RelayCommand]
    private void PickAt(Point at)
    {
        if (ActivePipette is not { } kind || CurrentImage is not { } image) return;

        // Geklickt wird im angezeigten Bild (gedreht, zugeschnitten …), gemessen im Pixelbild
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

    /// <summary>Messungen einer Pipette löschen (z. B. nach einem Fehlklick) — ein Rückgängig-Schritt.</summary>
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

    /// <summary>Alle drei Pipetten zurück — ein Rückgängig-Schritt.</summary>
    [RelayCommand]
    private void ResetWhiteBalance()
    {
        WbNeutral = null;
        WbBlack = null;
        WbWhite = null;
        ActivePipette = null;
    }

    private string? _wbProblem;

    /// <summary>Was zu tun ist: je Pipette, was man wählen soll — oder was an der letzten Stelle nicht ging.</summary>
    public string WbHint => _wbProblem ?? ActivePipette switch
    {
        WbPoint.Neutral => Tr.T("Neutral: auf etwas klicken, das in Wirklichkeit farblos ist – egal ob hell oder dunkel, z. B. Asphalt, Beton, eine graue Wolke, weißes Papier im Schatten. Nicht: grelles Weiß, fast Schwarzes, Beige oder Holz. Mehrere Klicks werden gemittelt; Esc beendet."),
        WbPoint.Black => Tr.T("Schwarz: auf die dunkelste Stelle klicken, die schwarz sein sollte. Mehrere Klicks werden gemittelt; Esc beendet."),
        WbPoint.White => Tr.T("Weiß: auf die hellste Stelle klicken, die weiß sein sollte, aber noch Zeichnung hat (nicht ausgefressen). Mehrere Klicks werden gemittelt; Esc beendet."),
        _ => Tr.T("Pipette wählen und ins Bild klicken. Eine Pipette reicht – Neutral (grau) ist meist die beste. Mit Schwarz und Weiß dazu und mit mehreren Klicks wird es genauer."),
    };

    // ------------------------------------------------------------ Markierungen im Bild

    private readonly List<PickMarker> _wbMarkers = [];

    /// <summary>Messstellen zum Anzeigen in der Vorschau (nur für das aktuelle Bild).</summary>
    [ObservableProperty] public partial IReadOnlyList<PickMarker>? WbMarkers { get; set; }

    private static string MarkerLabel(WbPoint kind) => kind switch
    {
        WbPoint.Neutral => Tr.T("N"),
        WbPoint.Black => Tr.T("S"),
        _ => Tr.T("W"),
    };

    /// <summary>Messung geändert (Klick, ×, Rückgängig): Markierungen auf die Zahl der Klicks kürzen, Vorschau neu.</summary>
    private void WbChanged(WbPoint kind, WbSample? value)
    {
        var keep = value?.Count ?? 0;
        var mine = _wbMarkers.Where(m => m.Kind == kind).ToList();
        foreach (var extra in mine.Skip(keep)) _wbMarkers.Remove(extra);
        WbMarkers = _wbMarkers.Count == 0 ? null : _wbMarkers.ToArray();
        ToolChanged(nameof(WbHint));
    }

    /// <summary>Neuer Bildstand: Markierungen gehören zum Bild davor, die Pipette hört auf.</summary>
    private void ResetWbForNewImage()
    {
        _wbMarkers.Clear();
        WbMarkers = null;
        ActivePipette = null;
        _wbProblem = null;
    }
}
