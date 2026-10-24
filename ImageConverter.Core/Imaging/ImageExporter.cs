using System.Text;
using ImageConverter.Core.Localization;
using ImageMagick;

namespace ImageConverter.Core.Imaging;

/// <summary>Specifies the file format of an export.</summary>
/// <remarks>See <see cref="ImageExporter.Extension"/> for the file extensions and
/// <see cref="ImageExporter.IsAlwaysLossless"/>, <see cref="ImageExporter.SupportsAlpha"/> and
/// <see cref="ImageExporter.SupportsHighBitDepth"/> for the capabilities.</remarks>
public enum OutputFormat { WebP, Png, Jpeg, Avif, JpegXl, Tiff, Bmp, Ico }

/// <summary>Specifies how metadata is handled on export.</summary>
public enum MetadataMode
{
    /// <summary>EXIF, IPTC and XMP stay as in the original.</summary>
    KeepAll,
    /// <summary>Remove location data (GPS), keep the rest.</summary>
    RemoveGps,
    /// <summary>Remove all metadata; the colour profile is kept.</summary>
    RemoveAll,
}

/// <summary>Specifies how the colour space and the embedded profile are handled on export.</summary>
public enum ColorMode
{
    /// <summary>Keep the colour space and profile of the original (for example Adobe RGB).</summary>
    KeepProfile,
    /// <summary>Convert to sRGB and embed an sRGB profile; safe for the web and print services.</summary>
    ConvertToSrgb,
    /// <summary>Convert to sRGB without embedding a profile; smallest file, browsers assume sRGB.</summary>
    DiscardProfile,
}

/// <summary>All settings of an export: a complete, immutable job.</summary>
/// <param name="Format">Output file format.</param>
/// <param name="Quality">Quality 1–100 for lossy formats (clamped when encoding).</param>
/// <param name="Lossless">Lossless encoding for WebP, AVIF and JPEG XL.</param>
/// <param name="Width">Target width in px.</param>
/// <param name="Height">Target height in px.</param>
/// <param name="Filter">Resampling filter.</param>
/// <param name="GammaCorrect">Scale in linear light.</param>
/// <param name="Denoise">Luminance noise reduction, 0…1; values up to 0.01 count as off.</param>
/// <param name="SharpenAmount">Unsharp mask strength (see <see cref="Sharpen"/>); values up to 0.01 count as off.</param>
/// <param name="SharpenSigma">Unsharp mask radius in px (Gaussian sigma).</param>
/// <param name="Dpi">Resolution written to the file, in dots per inch.</param>
/// <param name="Metadata">Metadata handling.</param>
/// <param name="Color">Colour profile handling.</param>
/// <param name="Adjustments">Settings of the Tools tile; null = none.</param>
/// <param name="HighBitDepth">PNG/TIFF with 16 bits per channel (otherwise 8 bits with dithering, unless the source
/// already has more than 8 bits).</param>
/// <param name="ColorDenoise">Colour noise reduction, 0…1 (<paramref name="Denoise"/> is luminance noise).</param>
/// <param name="Grain">Film grain, applied as the last step; null = none.</param>
/// <param name="GrainScale">Size of the computation relative to the real output (&lt; 1 for a downscaled print).</param>
/// <param name="Upscaled">AI upscaler result: the same image at four times the size, used instead of the source as the
/// basis when enlarging; null = none. Not owned by the options.</param>
public sealed record ExportOptions(
    OutputFormat Format,
    int Quality,
    bool Lossless,
    int Width,
    int Height,
    ResampleFilter Filter,
    bool GammaCorrect,
    double Denoise,
    double SharpenAmount,
    double SharpenSigma,
    int Dpi,
    MetadataMode Metadata,
    ColorMode Color,
    Adjustments? Adjustments = null,   // Tools tile; null = none
    bool HighBitDepth = false,         // PNG/TIFF with 16 bits per channel (otherwise 8 bits with dithering)
    double ColorDenoise = 0,           // Colour noise 0…1 (Denoise = luminance noise)
    GrainSettings? Grain = null,       // Grain, the last step; null = none
    double GrainScale = 1,             // Size of the computation relative to the real output (downscaled print < 1)
    MagickImage? Upscaled = null);     // Upscaler: the same image at four times the size (AI), the basis for enlarging instead of the source

/// <summary>
/// The export chain: scaling → noise reduction → Tools (light, colour) → sharpening → grain → colour profile →
/// metadata → encoding.
/// </summary>
/// <remarks>
/// The same steps as the preview, here at full resolution. When enlarging, noise reduction comes before scaling (see
/// <see cref="NoiseFirst"/>).
/// </remarks>
public static class ImageExporter
{
    /// <summary>Returns the file extension of a format.</summary>
    /// <param name="format">Output format.</param>
    /// <returns>The extension without a leading dot, for example "jpg"; "bin" for unknown values.</returns>
    public static string Extension(OutputFormat format) => format switch
    {
        OutputFormat.WebP => "webp",
        OutputFormat.Png => "png",
        OutputFormat.Jpeg => "jpg",
        OutputFormat.Avif => "avif",
        OutputFormat.JpegXl => "jxl",
        OutputFormat.Tiff => "tif",
        OutputFormat.Bmp => "bmp",
        OutputFormat.Ico => "ico",
        _ => "bin",
    };

    /// <summary>Determines whether a format has no quality setting because it always saves losslessly.</summary>
    /// <param name="format">Output format.</param>
    /// <returns><see langword="true"/> for PNG, TIFF, BMP and ICO.</returns>
    public static bool IsAlwaysLossless(OutputFormat format) =>
        format is OutputFormat.Png or OutputFormat.Tiff or OutputFormat.Bmp or OutputFormat.Ico;

    /// <summary>Bit depth of the source, stored as an artifact on the computed image (which itself is always 16 bits).</summary>
    private const string SourceDepthArtifact = "imageconverter:source-depth";

    /// <summary>Determines whether a format can be saved with 16 bits per channel.</summary>
    /// <param name="format">Output format.</param>
    /// <returns><see langword="true"/> for PNG and TIFF.</returns>
    public static bool SupportsHighBitDepth(OutputFormat format) =>
        format is OutputFormat.Png or OutputFormat.Tiff;

    /// <summary>Determines whether a format supports transparency; for the others, alpha is flattened onto white when
    /// encoding.</summary>
    /// <param name="format">Output format.</param>
    /// <returns><see langword="false"/> for JPEG and BMP.</returns>
    public static bool SupportsAlpha(OutputFormat format) =>
        format is not (OutputFormat.Jpeg or OutputFormat.Bmp);

    /// <summary>Applies the image chain (without encoding). The source remains unchanged.</summary>
    /// <param name="source">Source image; not modified.</param>
    /// <param name="o">Export settings.</param>
    /// <param name="plan">Optional progress plan with the steps of <see cref="PlanLook"/> or the export.</param>
    /// <param name="cache">Intermediate results for repeated runs on the same image (size estimate): the same result,
    /// computed only from the first changed stage on.</param>
    /// <returns>A new image with colour profile, metadata and density applied; the caller disposes it.</returns>
    /// <exception cref="OperationCanceledException">The plan's cancellation token was triggered between two steps.</exception>
    public static MagickImage Render(MagickImage source, ExportOptions o, WorkPlan? plan = null, LookCache? cache = null)
    {
        if (cache is not null) return RenderCached(source, o, plan, cache);
        uint sourceDepth;
        lock (source) sourceDepth = source.Depth;
        var basis = ResizeBasis(source, o);

        // When enlarging, denoise first and enlarge afterwards; otherwise enlarging blows the grain up into blotches
        // before the denoiser sees it. When downscaling, denoise afterwards: faster, and downscaling averages anyway.
        var noiseFirst = NoiseFirst(source, o);
        var start = noiseFirst ? RenderLook(source, o.Denoise, o.ColorDenoise, null, 0, 0, plan) : basis;
        plan?.Next();
        // Same size and the look computes a new image anyway: continue without a copy, because RenderLook only reads the
        // image. This saves a whole copy (panorama: 225 MB) for the duration of the image chain. Without a look the copy
        // is needed, because ApplyColor/ApplyMetadata modify the result.
        var lookDenoise = noiseFirst ? 0 : o.Denoise;
        var lookColorDenoise = noiseFirst ? 0 : o.ColorDenoise;
        bool sameSize;
        lock (start) sameSize = start.Width == o.Width && start.Height == o.Height;
        var resized = sameSize && LookProcesses(lookDenoise, lookColorDenoise, o.Adjustments, o.SharpenAmount, o.Grain)
            ? start
            : ImageOps.Resize(start, o.Width, o.Height, o.Filter, o.GammaCorrect);
        if (!ReferenceEquals(start, basis) && !ReferenceEquals(resized, start)) start.Dispose();
        var result = RenderLook(resized, lookDenoise, lookColorDenoise, o.Adjustments, o.SharpenAmount, o.SharpenSigma, plan,
            o.Grain, o.GrainScale);
        if (!ReferenceEquals(result, resized) && !ReferenceEquals(resized, basis)) resized.Dispose();

        plan?.Next();
        ApplyColor(result, o.Color);
        ApplyMetadata(result, o.Metadata);
        result.Density = new Density(o.Dpi, o.Dpi, DensityUnit.PixelsPerInch);
        result.SetArtifact(SourceDepthArtifact, sourceDepth.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return result;
    }

    /// <summary>
    /// <see cref="Render(MagickImage, ExportOptions, WorkPlan?, LookCache?)"/> with intermediate results: the same steps in
    /// the same order (otherwise the size estimate would measure a different image), but scaling + noise reduction,
    /// dehaze, shadows/highlights and light and colour come from <paramref name="cache"/> if their settings have not
    /// changed. The stored buffers belong to the cache and never go into the pool.
    /// </summary>
    private static MagickImage RenderCached(MagickImage source, ExportOptions o, WorkPlan? plan, LookCache cache)
    {
        cache.BeginRun();
        uint sourceDepth;
        lock (source) sourceDepth = source.Depth;
        var basis = ResizeBasis(source, o);
        var key = new LookCache.InputKey(new LookCache.SourceRef(basis), o.Width, o.Height, o.Filter, o.GammaCorrect,
            o.Denoise, o.ColorDenoise);
        var input = cache.GetInput(key, () =>
        {
            var noiseFirst = NoiseFirst(source, o);
            var start = noiseFirst ? RenderLook(source, o.Denoise, o.ColorDenoise, null, 0, 0, plan) : basis;
            plan?.Next();
            var resized = ImageOps.Resize(start, o.Width, o.Height, o.Filter, o.GammaCorrect);
            if (!ReferenceEquals(start, basis)) start.Dispose();
            try
            {
                var px = Pixels.FromMagick(resized);
                var noise = !noiseFirst && (o.Denoise > 0.01 || o.ColorDenoise > 0.01);
                if (noise)
                {
                    int rw, rh;
                    lock (resized) (rw, rh) = ((int)resized.Width, (int)resized.Height);
                    px = Swap(px, Denoiser.Apply(px, rw, rh, o.Denoise, o.ColorDenoise, 1, plan));
                }
                return new LookCache.Input(resized, px, noise);
            }
            catch
            {
                resized.Dispose();   // Cancelled: nothing goes into the cache
                throw;
            }
        });

        var image = input.Resized;
        int w, h;
        lock (image) (w, h) = ((int)image.Width, (int)image.Height);
        var adjust = o.Adjustments is { IsNone: false } a ? a : null;
        var grainy = o.Grain is { IsNone: false };
        MagickImage result;
        if (!input.Processed && adjust is null && o.SharpenAmount <= 0.01 && !grainy)
        {
            result = ImageOps.CloneOf(image);   // As in RenderLook: nothing to do, the scaled image itself
        }
        else
        {
            var looked = input.Pixels;
            if (adjust is not null)
            {
                looked = cache.GetAdjusted(key, adjust, () =>
                {
                    var dehazed = cache.GetDehazed(key, adjust.DehazeKey, () => Adjust.Dehazed(input.Pixels, w, h, adjust, plan));
                    var prepared = cache.GetPrepared(key, adjust.PrepareKey, () => Adjust.Finish(dehazed, w, h, adjust, plan));
                    return Adjust.Apply(prepared, w, h, adjust, plan);
                });
            }
            var work = looked;
            if (o.SharpenAmount > 0.01)
            {
                plan?.Next();
                work = Sharpen.UnsharpMask(work, w, h, o.SharpenAmount, o.SharpenSigma);
            }
            if (grainy)
            {
                plan?.Next();
                var grained = Grain.Apply(work, w, h, o.Grain, o.GrainScale);
                if (!ReferenceEquals(work, looked) && !ReferenceEquals(work, grained)) PixelPool.Return(work);
                work = grained;
            }
            lock (image) result = Pixels.ToMagick16(work, w, h, image.HasAlpha);
            if (!ReferenceEquals(work, looked)) PixelPool.Return(work);
            CopyProfiles(image, result);
            lock (image) result.Density = image.Density;
        }

        plan?.Next();
        ApplyColor(result, o.Color);
        ApplyMetadata(result, o.Metadata);
        result.Density = new Density(o.Dpi, o.Dpi, DensityUnit.PixelsPerInch);
        result.SetArtifact(SourceDepthArtifact, sourceDepth.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return result;
    }

    /// <summary>
    /// Determines the image that is scaled from.
    /// </summary>
    /// <param name="source">Source image; its size is read under a lock on it.</param>
    /// <param name="o">Export settings.</param>
    /// <returns>When enlarging with an upscaler result (<see cref="ExportOptions.Upscaled"/>), that image at four times the
    /// size (downscaled from it to the target); otherwise <paramref name="source"/>. No new image is created.</returns>
    public static MagickImage ResizeBasis(MagickImage source, ExportOptions o)
    {
        if (o.Upscaled is null) return source;
        lock (source) return o.Width > source.Width || o.Height > source.Height ? o.Upscaled : source;
    }

    /// <summary>Determines whether noise reduction comes before scaling.</summary>
    /// <param name="source">Source image; its size is read under a lock on it.</param>
    /// <param name="o">Export settings.</param>
    /// <returns><see langword="true"/> if noise reduction is on and the image is enlarged from the source itself (more
    /// target pixels than source pixels, no upscaler result in use).</returns>
    public static bool NoiseFirst(MagickImage source, ExportOptions o)
    {
        if (o.Denoise <= 0.01 && o.ColorDenoise <= 0.01) return false;
        if (!ReferenceEquals(ResizeBasis(source, o), source)) return false;   // The AI has already denoised; afterwards as when downscaling
        lock (source) return (long)o.Width * o.Height > (long)source.Width * source.Height;
    }

    /// <summary>
    /// Applies the look of the chain (noise reduction → Tools (light, colour, dehaze) → sharpening → grain) without
    /// scaling, colour profile and metadata.
    /// </summary>
    /// <param name="image">Image to process; not modified.</param>
    /// <param name="denoise">Luminance noise reduction, 0…1; values up to 0.01 count as off.</param>
    /// <param name="colorDenoise">Colour noise reduction, 0…1; values up to 0.01 count as off.</param>
    /// <param name="adjustments">Settings of the Tools tile; null = none.</param>
    /// <param name="sharpenAmount">Unsharp mask strength; values up to 0.01 count as off.</param>
    /// <param name="sharpenSigma">Unsharp mask radius in px (Gaussian sigma).</param>
    /// <param name="plan">Optional progress plan (see <see cref="PlanLook"/>).</param>
    /// <param name="grain">Grain; null if the look is applied to the image before the AI, because the grain always comes
    /// last.</param>
    /// <param name="grainScale">Size of this image relative to the output.</param>
    /// <returns><paramref name="image"/> itself if there is nothing to do; otherwise a new 16-bit image with the same
    /// profiles and density, which the caller disposes.</returns>
    public static MagickImage RenderLook(MagickImage image, double denoise, double colorDenoise, Adjustments? adjustments,
        double sharpenAmount, double sharpenSigma, WorkPlan? plan = null, GrainSettings? grain = null, double grainScale = 1)
    {
        var adjust = adjustments is { IsNone: false } a ? a : null;
        var noise = denoise > 0.01 || colorDenoise > 0.01;
        var grainy = grain is { IsNone: false };
        if (!LookProcesses(denoise, colorDenoise, adjustments, sharpenAmount, grain)) return image;

        // Everything in the 32-bit buffer as in the preview, read at full depth (Q16) and returned as a 16-bit image;
        // rounding happens only when encoding. Profiles are taken over.
        int w, h;
        lock (image) (w, h) = ((int)image.Width, (int)image.Height);
        var pixels = Pixels.FromMagick(image);
        if (noise) pixels = Swap(pixels, Denoiser.Apply(pixels, w, h, denoise, colorDenoise, 1, plan));
        if (adjust is not null) pixels = Swap(pixels, Adjust.Apply(pixels, w, h, adjust, plan));
        if (sharpenAmount > 0.01)
        {
            plan?.Next();
            pixels = Swap(pixels, Sharpen.UnsharpMask(pixels, w, h, sharpenAmount, sharpenSigma));
        }
        if (grainy)
        {
            plan?.Next();
            pixels = Swap(pixels, Grain.Apply(pixels, w, h, grain, grainScale));
        }
        MagickImage processed;
        lock (image) processed = Pixels.ToMagick16(pixels, w, h, image.HasAlpha);
        PixelPool.Return(pixels);
        CopyProfiles(image, processed);
        lock (image) processed.Density = image.Density;
        return processed;
    }

    /// <summary>Does <see cref="RenderLook"/> compute anything (and therefore return a new image)?</summary>
    private static bool LookProcesses(double denoise, double colorDenoise, Adjustments? adjustments, double sharpenAmount,
        GrainSettings? grain) =>
        denoise > 0.01 || colorDenoise > 0.01 || adjustments is { IsNone: false } || sharpenAmount > 0.01 || grain is { IsNone: false };

    /// <summary>Returns the old buffer to the pool and passes on the new one (the export buffers are owned only by this
    /// class).</summary>
    private static float[] Swap(float[] old, float[] next)
    {
        if (!ReferenceEquals(old, next)) PixelPool.Return(old);
        return next;
    }

    /// <summary>Encodes a finished image in the target format.</summary>
    /// <param name="image">Image from <see cref="Render(MagickImage, ExportOptions, WorkPlan?, LookCache?)"/>.</param>
    /// <param name="o">Export settings (format, quality, lossless, bit depth).</param>
    /// <param name="output">Stream to write to; not closed.</param>
    /// <param name="inPlace">The image belongs to the caller and is discarded afterwards: convert it without a copy (alpha,
    /// depth, format), which saves a whole copy when writing. Otherwise <paramref name="image"/> remains unchanged.</param>
    public static void Encode(MagickImage image, ExportOptions o, Stream output, bool inPlace = false)
    {
        using var copy = inPlace ? null : ImageOps.CloneOf(image);
        var img = copy ?? image;

        if (!SupportsAlpha(o.Format) && img.HasAlpha)
        {
            img.BackgroundColor = MagickColors.White;
            img.Alpha(AlphaOption.Remove);
        }

        // Bit depth: 16 bits where the format supports it and it is requested or the source already had more than
        // 8 bits; otherwise dithered to 8 bits once (Magick would round without dithering, and soft gradients would
        // band again)
        var sourceDepth = int.TryParse(img.GetArtifact(SourceDepthArtifact), out var d) ? d : (int)img.Depth;
        if (SupportsHighBitDepth(o.Format) && (o.HighBitDepth || sourceDepth > 8))
            img.Depth = 16;
        else
            Pixels.DitherTo8Bit(img);

        var quality = (uint)Math.Clamp(o.Quality, 1, 100);
        switch (o.Format)
        {
            case OutputFormat.Jpeg:
                img.Format = MagickFormat.Jpeg;
                img.Quality = quality;
                img.Settings.Interlace = Interlace.Plane;   // Progressive: shows a coarse version immediately on the web
                break;

            case OutputFormat.WebP:
                img.Format = MagickFormat.WebP;
                img.Quality = o.Lossless ? 100 : quality;
                if (o.Lossless) img.Settings.SetDefine(MagickFormat.WebP, "lossless", true);
                break;

            case OutputFormat.Avif:
                img.Format = MagickFormat.Avif;
                img.Quality = o.Lossless ? 100 : quality;
                if (o.Lossless) img.Settings.SetDefine(MagickFormat.Heic, "lossless", true);
                break;

            case OutputFormat.JpegXl:
                img.Format = MagickFormat.Jxl;
                img.Quality = o.Lossless ? 100 : quality;   // 100 = mathematically lossless
                break;

            case OutputFormat.Png:
                img.Format = MagickFormat.Png;
                img.Quality = 95;   // zlib level 9, adaptive filtering
                break;

            case OutputFormat.Tiff:
                img.Format = MagickFormat.Tiff;
                img.Settings.Compression = CompressionMethod.Zip;
                break;

            case OutputFormat.Bmp:
                img.Format = MagickFormat.Bmp;
                break;

            case OutputFormat.Ico:
                img.Format = MagickFormat.Icon;
                // Icons are at most 256 px; several sizes in one file
                img.Settings.SetDefine(MagickFormat.Icon, "auto-resize", "256,128,64,48,32,16");
                break;
        }

        img.Write(output);
    }

    /// <summary>Computes and writes an export file.</summary>
    /// <param name="source">Source image; not modified.</param>
    /// <param name="o">Export settings.</param>
    /// <param name="path">Target file; it must not exist yet (the temporary file is moved without overwriting).</param>
    /// <param name="progress">Progress across all steps up to the finished file, with the step that is running.</param>
    /// <returns>The file size in bytes.</returns>
    /// <remarks>
    /// Writes to <c>path + ".tmp"</c> first and then renames it, so that an abort never leaves a half-written target
    /// file. Afterwards the free pool buffers are dropped; returning the memory to the operating system
    /// (<see cref="PixelPool.Trim"/> with <c>release: true</c>) is left to the caller.
    /// </remarks>
    public static long Export(MagickImage source, ExportOptions o, string path, IProgress<WorkProgress>? progress = null)
    {
        var plan = progress is null ? null : Plan(source, o, progress);
        using (var rendered = Render(source, o, plan))
        {
            plan?.Next();

            // First into a temporary file, then rename: an abort never leaves a half-written target file.
            var temp = path + ".tmp";
            try
            {
                using (var fs = File.Create(temp))
                    Encode(rendered, o, fs, inPlace: true);   // rendered is discarded right afterwards
                File.Move(temp, path, overwrite: false);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }

        // Drop the free buffers of this computation. Returning the memory to the system (PixelPool.Trim(release: true)) is
        // left to the caller after the return: here the intermediate buffers are still reachable, and measured, 2 GB
        // would stay in use.
        PixelPool.Trim();
        plan?.Done(Tr.T("Gespeichert"));
        return new FileInfo(path).Length;
    }

    /// <summary>
    /// Creates a progress plan with the steps of <see cref="RenderLook"/> (noise reduction, dehaze per pass, light and
    /// colour, sharpening, grain), for example when the AI applies the look to the image beforehand or lays it over a
    /// result.
    /// </summary>
    /// <param name="megapixels">Size of the image that is computed on, in megapixels.</param>
    /// <param name="denoise">Luminance noise reduction, 0…1.</param>
    /// <param name="colorDenoise">Colour noise reduction, 0…1.</param>
    /// <param name="adjustments">Settings of the Tools tile; null = none.</param>
    /// <param name="sharpenAmount">Unsharp mask strength.</param>
    /// <param name="progress">Receives the progress; null discards it.</param>
    /// <param name="cancel">Checked between the steps.</param>
    /// <param name="grain">Grain; null = none.</param>
    /// <returns>A new plan with the steps registered, in the order of <see cref="RenderLook"/>.</returns>
    public static WorkPlan PlanLook(double megapixels, double denoise, double colorDenoise, Adjustments? adjustments, double sharpenAmount,
        IProgress<WorkProgress>? progress, CancellationToken cancel = default, GrainSettings? grain = null)
    {
        var plan = new WorkPlan(progress, cancel);
        if (denoise > 0.01 || colorDenoise > 0.01) plan.Add(Tr.T("Rauschen entfernen"), WorkPlan.DenoiseCost * megapixels);
        if (adjustments is { IsNone: false } a) Adjust.PlanSteps(a, plan, megapixels);
        if (sharpenAmount > 0.01) plan.Add(Tr.T("Schärfen"), WorkPlan.SharpenCost * megapixels);
        if (grain is { IsNone: false }) plan.Add(Tr.T("Körnung"), WorkPlan.GrainCost * megapixels);
        return plan;
    }

    /// <summary>The steps of <see cref="Render(MagickImage, ExportOptions, WorkPlan?, LookCache?)"/> and of saving, in their
    /// order.</summary>
    private static WorkPlan Plan(MagickImage source, ExportOptions o, IProgress<WorkProgress> progress)
    {
        var plan = new WorkPlan(progress);
        var mp = (double)o.Width * o.Height / 1e6;
        var noise = o.Denoise > 0.01 || o.ColorDenoise > 0.01;
        var noiseFirst = NoiseFirst(source, o);
        if (noiseFirst) plan.Add(Tr.T("Rauschen entfernen"), WorkPlan.DenoiseCost * source.Width * source.Height / 1e6);
        plan.Add(Tr.T("Größe anpassen"), WorkPlan.ResizeCost * Math.Max(mp, (double)source.Width * source.Height / 1e6));
        if (noise && !noiseFirst) plan.Add(Tr.T("Rauschen entfernen"), WorkPlan.DenoiseCost * mp);
        if (o.Adjustments is { IsNone: false } a) Adjust.PlanSteps(a, plan, mp);
        if (o.SharpenAmount > 0.01) plan.Add(Tr.T("Schärfen"), WorkPlan.SharpenCost * mp);
        if (o.Grain is { IsNone: false }) plan.Add(Tr.T("Körnung"), WorkPlan.GrainCost * mp);
        plan.Add(Tr.T("Farbprofil und Metadaten"), WorkPlan.ColorCost * mp);
        plan.Add(Tr.T("Datei schreiben ({0})", Extension(o.Format).TrimStart('.').ToUpperInvariant()), EncodeCost(o) * mp);
        return plan;
    }

    /// <summary>Cost of encoding in seconds per megapixel (roughly measured; AVIF and JPEG XL are slow).</summary>
    private static double EncodeCost(ExportOptions o) => o.Format switch
    {
        OutputFormat.Jpeg => 0.04,
        OutputFormat.Png => 0.15,
        OutputFormat.WebP => o.Lossless ? 0.4 : 0.15,
        OutputFormat.Avif => 0.8,
        OutputFormat.JpegXl => 0.4,
        OutputFormat.Tiff => 0.06,
        _ => 0.03,
    };

    // ------------------------------------------------------------ Colour

    private static void ApplyColor(MagickImage image, ColorMode mode)
    {
        if (mode == ColorMode.KeepProfile) return;

        // To sRGB; only needed if a different profile is embedded
        var profile = image.GetColorProfile();
        if (profile is not null && !IsSrgb(profile))
            image.TransformColorSpace(ColorProfiles.SRGB);

        if (mode == ColorMode.ConvertToSrgb)
            image.SetProfile(ColorProfiles.SRGB);
        else
            image.RemoveProfile("icc");
    }

    internal static bool IsSrgb(IColorProfile profile) =>
        profile.Description?.Contains("sRGB", StringComparison.OrdinalIgnoreCase) == true;

    // ------------------------------------------------------------ Metadata

    private static void ApplyMetadata(MagickImage image, MetadataMode mode)
    {
        switch (mode)
        {
            case MetadataMode.RemoveAll:
            {
                // Strip also removes the colour profile, so it is set again afterwards
                var icc = image.GetColorProfile();
                image.Strip();
                if (icc is not null) image.SetProfile(icc);
                break;
            }

            case MetadataMode.RemoveGps:
            {
                var exif = image.GetExifProfile();
                if (exif is not null)
                {
                    // Remove one by one: ExifParts alone leaves the coordinates in the file
                    // (verified with Magick.NET 14.17.2).
                    var gpsTags = exif.Values
                        .Select(v => v.Tag)
                        .Where(t => t.ToString().StartsWith("GPS", StringComparison.Ordinal))
                        .ToList();
                    foreach (var tag in gpsTags)
                        exif.RemoveValue(tag);
                    exif.Parts = ExifParts.IfdTags | ExifParts.ExifTags;
                    image.SetProfile(exif);
                }

                // XMP can also contain coordinates (exif:GPS…); in that case remove it entirely
                var xmp = image.GetXmpProfile();
                if (xmp?.ToByteArray() is { } bytes && Encoding.UTF8.GetString(bytes).Contains("GPS", StringComparison.Ordinal))
                    image.RemoveProfile("xmp");
                break;
            }
        }
    }

    internal static void CopyProfiles(MagickImage from, MagickImage to)
    {
        // "from" is often the shared original (see ImageOps.CloneOf), so it is read under its lock
        var profiles = new List<IImageProfile>();
        lock (from)
        {
            foreach (var name in from.ProfileNames)
                if (from.GetProfile(name) is { } profile)
                    profiles.Add(profile);
        }

        foreach (var profile in profiles)
            to.SetProfile(profile);
    }
}
