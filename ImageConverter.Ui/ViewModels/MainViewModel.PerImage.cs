using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using ImageConverter.Core.Imaging;
using ImageMagick;

namespace ImageConverter.ViewModels;

// "Same settings for all images" or each image on its own.
//
// Ticked: all images share the settings. The size is converted
// per image (AdaptToImage) so that nothing is distorted.
// Unticked (the state after opening images, see ResetToStart): each image remembers its own state (_fileSettings).
// Ticked again: all images take over the state of the current image.
// Crop, rotation and flip always belong to the individual image anyway.
public partial class MainViewModel
{
    [ObservableProperty] public partial bool ApplyToAll { get; set; } = true;

    /// <summary>Per-image mode: state per file. Images without an entry start from <see cref="_individualTemplate"/>, otherwise from a copy of the current state.</summary>
    private Dictionary<string, SettingsState> _fileSettings = new(StringComparer.Ordinal);

    /// <summary>Per-image mode: the state when the tick was removed (or the start state after opening images); starting point for images without their own state.</summary>
    private SettingsState? _individualTemplate;

    private string? CurrentPath => _fileIndex >= 0 && _fileIndex < _files.Count ? _files[_fileIndex] : null;

    partial void OnApplyToAllChanged(bool value)
    {
        if (_suppressHistory > 0) return;

        // A separate undo step that saves the mode, all per-image states and the template
        CommitPendingSettings();
        _undo.Push(new Snapshot(null, null, CaptureSettings(),
            ApplyToAll: !value, FileSettings: new Dictionary<string, SettingsState>(_fileSettings),
            Template: _individualTemplate));
        ClearRedo();

        if (value)
        {
            // "For all" again: all images take over the current state
            _fileSettings.Clear();
            _individualTemplate = null;
        }
        else
        {
            // Per image from now on. The template for all images is the state at THIS moment,
            // not the state another image receives later (otherwise a change to
            // image 2 would be carried over when image 1 is first visited).
            _individualTemplate = CaptureSettings();
            if (CurrentPath is { } path)
                _fileSettings[path] = _individualTemplate;
        }

        NotifyUndoRedo();
        RequestEstimate();
    }

    /// <summary>
    /// Converts a settings state to an image of a different size.
    /// <list type="bullet">
    /// <item>Size not touched (target = original) → the other image also keeps its original size.</item>
    /// <item>Chain open → exactly the set dimensions (deliberately distorted).</item>
    /// <item>Chain closed → the edge entered last applies, the other follows the
    ///   aspect ratio of the new image. In mm mode this corresponds to the same print edge.</item>
    /// </list>
    /// Selection frame, corner radius and pending rotation belong to the old image and are discarded.
    /// </summary>
    /// <param name="s">The state to convert.</param>
    /// <param name="width">Width of the new image in px.</param>
    /// <param name="height">Height of the new image in px.</param>
    /// <returns>The converted state.
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
            RotationAngle = 0,
        };
    }

    /// <summary>The state that applies to a file: the current image's state, its own state or a converted copy.</summary>
    /// <param name="path">Path of the file.</param>
    /// <param name="width">Width of the image in px.</param>
    /// <param name="height">Height of the image in px.</param>
    /// <param name="fileDpi">DPI from the file, for images that have never been shown (as on first display).</param>
    /// <returns>The settings state for the file.</returns>
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
    /// DPI for an image that receives a state for the first time: from the file if the template is the start state
    /// or its DPI is still that of the previous image's file (that is, not set manually); otherwise the manual setting.
    /// </summary>
    private SettingsState WithFileDpi(SettingsState template, double fileDpi, double? previousFileDpi) =>
        ReferenceEquals(template, _startSettings)
        || (previousFileDpi is { } d && template.Dpi == (int)Math.Round(d))
            ? template with { Dpi = (int)Math.Round(fileDpi) }
            : template;

    /// <summary>Target dimensions with a switched-off size step taken into account; like EffectiveTarget(), but for any state.</summary>
    internal static (int Width, int Height) EffectiveTarget(SettingsState s)
    {
        if ((s.Hidden & EditSteps.Resize) != 0) return (Math.Max(1, s.SourceWidth), Math.Max(1, s.SourceHeight));   // size switched off
        return EffectiveResize(s);
    }

    private static (int Width, int Height) EffectiveResize(SettingsState s) => (Math.Max(1, s.TargetWidth), Math.Max(1, s.TargetHeight));

    /// <summary>Export job for a settings state.</summary>
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
                Math.Round(s.DehazeBrightness), Math.Round(s.DehazeDeband), Math.Round(s.DehazeCast),
                s.WbNeutral, s.WbBlack, s.WbWhite, Math.Round(s.MatchLimit), Math.Round(s.MatchTarget), Math.Round(s.MatchGlobal),
                Math.Round(s.MaskAmount), Math.Round(s.MaskEnd), Math.Round(s.MaskCurve)), s.Hidden),
            s.HighBitDepth && ImageExporter.SupportsHighBitDepth(format),
            (s.Hidden & EditSteps.ColorNoise) == 0 && s.ColorNoiseReduction > 0.005 ? s.ColorNoiseReduction : 0,
            (s.Hidden & EditSteps.Grain) == 0 && Math.Round(s.GrainAmount) >= 1
                ? new GrainSettings(Math.Round(s.GrainAmount), Math.Round(s.GrainSize), Math.Round(s.GrainRoughness), Math.Round(s.GrainColor),
                    Math.Round(s.GrainHardness))
                : null);
    }

    // ------------------------------------------------- for the export window

    internal IReadOnlyList<string> OpenFiles => _files;

    /// <summary>
    /// The image of a file in its current state: the edited image from the history,
    /// otherwise freshly loaded. <c>Owned</c> = the caller must dispose it.
    /// </summary>
    internal async Task<(MagickImage Image, bool Owned, long FileBytes, double Dpi)> GetImageAsync(string path)
    {
        if (_histories.TryGetValue(path, out var h))
        {
            var hidden = HiddenFor(path);
            // in the background: applying geometry to a large image takes time
            return (await Task.Run(() => DocumentFor(path, h, hidden)), false, h.FileBytes, h.Dpi);   // without switched-off functions (AI …), with geometry
        }

        var loaded = await ImageLoader.LoadAsync(path);
        return (loaded.Image, true, loaded.FileBytes, loaded.Dpi);
    }

    /// <summary>
    /// For lists (export of several images, printing): size, DPI and thumbnail. Images in the history come from there;
    /// all others only from the header data and a thumbnail decoded at reduced size, so that no image is loaded
    /// completely for the list and then again for the export. If probing fails, the image is loaded completely.
    /// </summary>
    internal async Task<(int Width, int Height, double Dpi, byte[] Thumb, int ThumbWidth, int ThumbHeight)> GetListEntryAsync(string path,
        int thumbSize)
    {
        if (!_histories.ContainsKey(path) && await ImageLoader.ProbeAsync(path) is { } probe)
        {
            using var small = await ImageLoader.LoadThumbnailAsync(path, thumbSize);
            var bgra = await Task.Run(() => ImageOps.ToDisplayBgra(small));
            return (probe.Width, probe.Height, probe.Dpi, bgra, (int)small.Width, (int)small.Height);
        }
        var (image, owned, _, dpi) = await GetImageAsync(path);
        try
        {
            var (thumb, tw, th) = await Task.Run(() =>
            {
                using var t = ImageOps.CloneOf(image);
                t.Thumbnail((uint)thumbSize, (uint)thumbSize);
                return (ImageOps.ToDisplayBgra(t), (int)t.Width, (int)t.Height);
            });
            return ((int)image.Width, (int)image.Height, dpi, thumb, tw, th);
        }
        finally
        {
            if (owned) image.Dispose();
        }
    }

    /// <summary>Target folder and file name (not yet checked for collisions).</summary>
    internal (string Folder, string FileName) PlanOutput(string path, SettingsState s, ExportOptions o) =>
        (OutputNaming.ResolveFolder(s.OutputFolder, path),
         OutputNaming.BuildFileName(s.NamePattern, path, o.Width, o.Height,
             _files.IndexOf(path) + 1, DateTime.Now, ImageExporter.Extension(o.Format)));

    /// <summary>Exports a file with its state.</summary>
    /// <param name="path">Path of the source file.</param>
    /// <param name="progress">Progress reports; null for none.</param>
    /// <param name="upscale">Compute the AI upscaler where selected; false = filter only.</param>
    /// <param name="ct">Cancels the upscaling step; the encoding itself is not cancelled.</param>
    /// <returns>Target path and file size in bytes.</returns>
    internal async Task<(string Path, long Bytes)> ExportFileAsync(string path, IProgress<WorkProgress>? progress = null, bool upscale = true,
        CancellationToken ct = default)
    {
        var (image, owned, _, dpi) = await GetImageAsync(path);
        MagickImage? upscaled = null;
        try
        {
            var state = SettingsFor(path, (int)image.Width, (int)image.Height, dpi);
            var options = OptionsFor(state);
            if (upscale && await UpscaleForBatchAsync(path, image, state, options, progress, ct) is { } up)
            {
                options = options with { Upscaled = up.Image };
                if (up.Owned) upscaled = up.Image;
            }
            var (folder, fileName) = PlanOutput(path, state, options);

            return await Task.Run(() =>
            {
                Directory.CreateDirectory(folder);
                var target = OutputNaming.MakeUnique(folder, fileName);
                var written = ImageExporter.Export(image, options, target, progress);
                PixelPool.Trim(release: true);   // only now are all export buffers free: return memory
                return (target, written);
            });
        }
        finally
        {
            if (owned) image.Dispose();
            upscaled?.Dispose();
        }
    }
}
