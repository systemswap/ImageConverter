using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using ImageConverter.Core.Imaging;
using ImageMagick;

namespace ImageConverter.ViewModels;

// „Gleiche Einstellungen für alle Bilder" oder jedes Bild einzeln.
//
// Haken gesetzt (Standard): alle Bilder teilen sich die Einstellungen. Die Größe
// wird pro Bild umgerechnet (AdaptToImage), damit nichts verzerrt.
// Haken entfernt: jedes Bild merkt sich seinen eigenen Stand (_fileSettings).
// Haken wieder gesetzt: alle Bilder übernehmen den Stand des aktuellen Bildes.
// Zuschnitt, Drehen, Spiegeln gehören ohnehin immer zum einzelnen Bild.
public partial class MainViewModel
{
    [ObservableProperty] public partial bool ApplyToAll { get; set; } = true;

    /// <summary>Einzelmodus: Stand je Datei. Bilder ohne Eintrag starten mit einer Kopie des aktuellen.</summary>
    private Dictionary<string, SettingsState> _fileSettings = new(StringComparer.Ordinal);

    /// <summary>Einzelmodus: Stand beim Entfernen des Hakens — Startpunkt für Bilder ohne eigenen Stand.</summary>
    private SettingsState? _individualTemplate;

    private string? CurrentPath => _fileIndex >= 0 && _fileIndex < _files.Count ? _files[_fileIndex] : null;

    partial void OnApplyToAllChanged(bool value)
    {
        if (_suppressHistory > 0) return;

        // Ein eigener Rückgängig-Schritt, der Modus, alle Einzelstände und die Vorlage sichert
        CommitPendingSettings();
        _undo.Push(new Snapshot(null, null, CaptureSettings(),
            ApplyToAll: !value, FileSettings: new Dictionary<string, SettingsState>(_fileSettings),
            Template: _individualTemplate));
        ClearRedo();

        if (value)
        {
            // Wieder „für alle": alle übernehmen den aktuellen Stand
            _fileSettings.Clear();
            _individualTemplate = null;
        }
        else
        {
            // Ab jetzt einzeln. Vorlage für alle Bilder ist der Stand von JETZT —
            // nicht der, den ein anderes Bild später bekommt (sonst würde eine
            // Änderung an Bild 2 beim ersten Besuch von Bild 1 mit übernommen).
            _individualTemplate = CaptureSettings();
            if (CurrentPath is { } path)
                _fileSettings[path] = _individualTemplate;
        }

        NotifyUndoRedo();
        RequestEstimate();
    }

    /// <summary>
    /// Rechnet einen Einstellungsstand auf ein anders großes Bild um.
    /// <list type="bullet">
    /// <item>Größe nicht angefasst (Ziel = Original) → auch das andere Bild in Originalgröße.</item>
    /// <item>Kette offen → genau die eingestellten Maße (bewusst verzerrt).</item>
    /// <item>Kette zu → die zuletzt eingegebene Kante gilt, die andere folgt dem
    ///   Seitenverhältnis des neuen Bildes. Im mm-Modus entspricht das derselben Druckkante.</item>
    /// </list>
    /// Auswahlrahmen und Eckenradius gehören zum alten Bild und werden verworfen.
    /// </summary>
    internal static SettingsState AdaptToImage(SettingsState s, int width, int height)
    {
        int tw, th;
        var untouched = s.TargetWidth == s.SourceWidth && s.TargetHeight == s.SourceHeight;

        if (untouched || s.SourceWidth <= 0 || s.SourceHeight <= 0)
        {
            (tw, th) = (width, height);
        }
        else if (!s.KeepAspectRatio)
        {
            (tw, th) = (s.TargetWidth, s.TargetHeight);
        }
        else if (s.WidthIsLeading)
        {
            tw = s.TargetWidth;
            th = Math.Max(1, (int)Math.Round(tw * (double)height / width));
        }
        else
        {
            th = s.TargetHeight;
            tw = Math.Max(1, (int)Math.Round(th * (double)width / height));
        }

        var dpi = Math.Max(1, s.Dpi);
        return s with
        {
            SourceWidth = width,
            SourceHeight = height,
            TargetWidth = tw,
            TargetHeight = th,
            PrintWidthIn = tw / (double)dpi,
            PrintHeightIn = th / (double)dpi,
            Calc = new ResizeCalculator(width, height),
            CropSelection = null,
            CornerRadius = 0,
        };
    }

    /// <summary>Stand, der für eine Datei gilt — aktuelles Bild, eigener Stand oder umgerechnete Kopie.</summary>
    /// <param name="fileDpi">DPI aus der Datei — für Bilder, die noch nie angezeigt wurden (wie beim ersten Anzeigen).</param>
    internal SettingsState SettingsFor(string path, int width, int height, double fileDpi)
    {
        if (path == CurrentPath)
            return CaptureSettings();
        if (!ApplyToAll && _fileSettings.TryGetValue(path, out var own))
            return own.SourceWidth == width && own.SourceHeight == height ? own : AdaptToImage(own, width, height);
        var template = !ApplyToAll && _individualTemplate is { } t ? t : CaptureSettings();
        return AdaptToImage(WithFileDpi(template, fileDpi, _history?.Dpi), width, height);
    }

    /// <summary>
    /// DPI für ein Bild, das zum ersten Mal einen Stand bekommt: aus der Datei, wenn die Vorlage der Startstand ist
    /// oder ihre DPI noch die der Datei des Bildes davor (also nicht selbst eingestellt) — sonst die eigene Einstellung.
    /// </summary>
    private SettingsState WithFileDpi(SettingsState template, double fileDpi, double? previousFileDpi) =>
        ReferenceEquals(template, _startSettings)
        || (previousFileDpi is { } d && template.Dpi == (int)Math.Round(d))
            ? template with { Dpi = (int)Math.Round(fileDpi) }
            : template;

    /// <summary>Zielmaße nach „Nicht vergrößern" — wie EffectiveTarget, aber für einen beliebigen Stand.</summary>
    internal static (int Width, int Height) EffectiveTarget(SettingsState s)
    {
        if ((s.Hidden & EditSteps.Resize) != 0) return (Math.Max(1, s.SourceWidth), Math.Max(1, s.SourceHeight));   // Größe ausgeblendet
        return EffectiveResize(s);
    }

    private static (int Width, int Height) EffectiveResize(SettingsState s)
    {
        var w = Math.Max(1, s.TargetWidth);
        var h = Math.Max(1, s.TargetHeight);
        if (!s.DoNotEnlarge || (w <= s.SourceWidth && h <= s.SourceHeight)) return (w, h);

        var f = Math.Min((double)s.SourceWidth / w, (double)s.SourceHeight / h);
        return (Math.Max(1, (int)Math.Round(w * f)), Math.Max(1, (int)Math.Round(h * f)));
    }

    /// <summary>Export-Auftrag für einen Einstellungsstand.</summary>
    internal static ExportOptions OptionsFor(SettingsState s)
    {
        var (w, h) = EffectiveTarget(s);
        var format = FormatByLabel.TryGetValue(s.SelectedFormat ?? "", out var f) ? f : OutputFormat.WebP;
        var losslessAvailable = format is OutputFormat.WebP or OutputFormat.Avif or OutputFormat.JpegXl;
        return new ExportOptions(
            format,
            (int)Math.Round(s.Quality),
            s.Lossless && losslessAvailable,
            w, h,
            s.SelectedFilter.Filter, s.GammaCorrect,
            (s.Hidden & EditSteps.Denoise) == 0 && s.NoiseReduction > 0.005 ? s.NoiseReduction : 0,
            (s.Hidden & EditSteps.Sharpen) == 0 && s.SharpenAmount > 0.01 ? s.SharpenAmount : 0,
            s.SharpenRadius,
            s.Dpi,
            MetadataByLabel.TryGetValue(s.SelectedMetadataMode ?? "", out var m) ? m : MetadataMode.KeepAll,
            ColorByLabel.TryGetValue(s.SelectedColorMode ?? "", out var c) ? c : ColorMode.ConvertToSrgb,
            Mask(new Adjustments(Math.Round(s.Brightness), Math.Round(s.Contrast), Math.Round(s.Shadows),
                Math.Round(s.Highlights), Math.Round(s.Saturation), TintOf(s.SelectedTint),
                Math.Round(s.Vibrance), Math.Round(s.TintHue), Math.Round(s.Dehaze),
                Math.Round(s.MatchLuminance), Math.Round(s.Neutralize), Math.Round(s.Warmth),
                (int)Math.Round(s.DehazePasses), Math.Round(s.DehazeClarity), Math.Round(s.DehazeContrast),
                Math.Round(s.DehazeVibrance), Math.Round(s.DehazeSharpen), Math.Round(s.DehazeEdge),
                Math.Round(s.DehazeBrightness), Math.Round(s.DehazeDeband), Math.Round(s.DehazeCast)), s.Hidden),
            s.HighBitDepth && ImageExporter.SupportsHighBitDepth(format),
            (s.Hidden & EditSteps.ColorNoise) == 0 && s.ColorNoiseReduction > 0.005 ? s.ColorNoiseReduction : 0);
    }

    // ------------------------------------------------- für das Export-Fenster

    internal IReadOnlyList<string> OpenFiles => _files;

    /// <summary>
    /// Das Bild einer Datei in ihrem aktuellen Stand: bearbeitet aus dem Verlauf,
    /// sonst frisch geladen. <c>Owned</c> = der Aufrufer muss es freigeben.
    /// </summary>
    internal async Task<(MagickImage Image, bool Owned, long FileBytes, double Dpi)> GetImageAsync(string path)
    {
        if (_histories.TryGetValue(path, out var h))
            return (DocumentFor(path, h, HiddenFor(path)), false, h.FileBytes, h.Dpi);   // ohne Ausgeblendetes (KI …)

        var loaded = await ImageLoader.LoadAsync(path);
        return (loaded.Image, true, loaded.FileBytes, loaded.Dpi);
    }

    /// <summary>Zielordner und Dateiname (noch ohne Kollisionsprüfung).</summary>
    internal (string Folder, string FileName) PlanOutput(string path, SettingsState s, ExportOptions o) =>
        (OutputNaming.ResolveFolder(s.OutputFolder, path),
         OutputNaming.BuildFileName(s.NamePattern, path, o.Width, o.Height,
             _files.IndexOf(path) + 1, DateTime.Now, ImageExporter.Extension(o.Format)));

    /// <summary>Exportiert eine Datei mit ihrem Stand. Gibt Zielpfad und Größe zurück.</summary>
    internal async Task<(string Path, long Bytes)> ExportFileAsync(string path, IProgress<WorkProgress>? progress = null)
    {
        var (image, owned, _, dpi) = await GetImageAsync(path);
        try
        {
            var state = SettingsFor(path, (int)image.Width, (int)image.Height, dpi);
            var options = OptionsFor(state);
            var (folder, fileName) = PlanOutput(path, state, options);

            return await Task.Run(() =>
            {
                Directory.CreateDirectory(folder);
                var target = OutputNaming.MakeUnique(folder, fileName);
                return (target, ImageExporter.Export(image, options, target, progress));
            });
        }
        finally
        {
            if (owned) image.Dispose();
        }
    }
}
