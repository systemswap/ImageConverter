using ImageConverter.Core.Imaging;
using ImageMagick;
using Microsoft.ML.OnnxRuntime;
using ImageConverter.Core.Platform;

namespace ImageConverter.Core.Ai.SyntheticDoc;

/// <summary>
/// AI dewarping with SyntheticDoc: the model predicts the backward mapping of a photographed sheet, a grid of 121 × 177
/// points of the flat sheet, each with its position in the photo.
/// </summary>
/// <remarks>
/// <para>
/// Two runs: a coarse one on a quarter of the pixels (512 × 720, width × height), only to locate the sheet, then the
/// accurate one (1024 × 1440, the training resolution) on the crop around the sheet (+ 4 % per side), because the model
/// was trained only on such crops.
/// </para>
/// <para>
/// The model does not know the aspect ratio of the page. It comes from the app's own sheet model, for which grid rows
/// and columns serve as lines (with DIN snapping), or else from the arc lengths of the grid.
/// </para>
/// <para>
/// The accurate run also yields the shading map (how much light each point of the sheet received). It is flattened,
/// reduced and stored with the grid (<see cref="DewarpShading"/>), and removed when rendering if switched on.
/// </para>
/// </remarks>
public static class SyntheticDocDewarp
{
    /// <summary>Model input size of the accurate run in pixels and size of the predicted grid in nodes.</summary>
    public const int InputWidth = 1024, InputHeight = 1440, GridWidth = 177, GridHeight = 121;

    /// <summary>Sample grid of the shading map in the result: a quarter of the model input, because light changes slowly.</summary>
    public const int ShadingWidth = InputWidth / 4, ShadingHeight = InputHeight / 4;

    /// <summary>
    /// Computes the dewarping grid for an image (as shown, full resolution).
    /// </summary>
    /// <param name="session">Session of the grid-only model; used for the coarse run and, without a shadow model, also for the accurate run.</param>
    /// <param name="shadowSession">Model with shading map for the accurate run; null: no shading, grid only.</param>
    /// <param name="doc">Photo of the sheet; it is not modified.</param>
    /// <param name="focal35">35 mm equivalent focal length in mm, used by the sheet model for the aspect ratio.</param>
    /// <param name="removeShadows">Initial state of <see cref="DewarpShading.On"/>, i.e. whether the shading is removed when rendering.</param>
    /// <param name="ct">Cancels the computation.</param>
    /// <returns>
    /// The grid in relative coordinates, valid for any size of the image; with <see cref="DewarpGrid.Shading"/> set if
    /// <paramref name="shadowSession"/> was given. If the coarse run finds a sheet smaller than 20 % of the width or
    /// height, the accurate run uses the whole image.
    /// </returns>
    public static DewarpGrid Run(InferenceSession session, InferenceSession? shadowSession, MagickImage doc, double focal35,
        bool removeShadows, CancellationToken ct)
    {
        int w, h;
        lock (doc) (w, h) = ((int)doc.Width, (int)doc.Height);
        var (first, _) = Predict(session, doc, 0, 0, w, h, InputWidth / 2, InputHeight / 2, false, ct);

        // Locate the sheet: outline of the grid, 4 % margin
        double bx0 = double.MaxValue, by0 = double.MaxValue, bx1 = double.MinValue, by1 = double.MinValue;
        for (var j = 0; j < GridHeight; j++)
        for (var i = 0; i < GridWidth; i++)
        {
            if (j != 0 && j != GridHeight - 1 && i != 0 && i != GridWidth - 1) continue;
            var (x, y) = first[j, i];
            (bx0, bx1, by0, by1) = (Math.Min(bx0, x), Math.Max(bx1, x), Math.Min(by0, y), Math.Max(by1, y));
        }
        double mx = 0.04 * (bx1 - bx0), my = 0.04 * (by1 - by0);
        int x0 = (int)Math.Max(0, Math.Floor(bx0 - mx)), y0 = (int)Math.Max(0, Math.Floor(by0 - my));
        int x1 = (int)Math.Min(w, Math.Ceiling(bx1 + mx)), y1 = (int)Math.Min(h, Math.Ceiling(by1 + my));
        if (x1 - x0 < 0.2 * w || y1 - y0 < 0.2 * h) (x0, y0, x1, y1) = (0, 0, w, h);   // nothing usable found: whole image
        var (grid, light) = Predict(shadowSession ?? session, doc, x0, y0, x1, y1, InputWidth, InputHeight, shadowSession is not null, ct);
        var result = ToGrid(grid, w, h, focal35);
        if (light is null) return result;
        return result with { Shading = Straighten(light, result, w, h, x0, y0, x1, y1, removeShadows) };
    }

    /// <summary>Shading map of the accurate run (in the crop, reduced, planar RGB) and the mean brightness of the input.</summary>
    private sealed record Light(float[] Rgb, int Width, int Height, double Brightness);

    /// <summary>
    /// Flattens the shading map: for each sample of the raster over the result, finds the position in the photo (from the
    /// grid) and reads the map there. Strength as used by the authors: min(1, 0.4 / mean brightness), less for bright
    /// photos so that nothing burns out.
    /// </summary>
    private static DewarpShading Straighten(Light light, DewarpGrid grid, int w, int h, int x0, int y0, int x1, int y1, bool on)
    {
        var map = new DewarpMap(grid.Nx, grid.Ny, grid.Sx, grid.Sy, grid.RelWidth, grid.RelHeight);
        double cw = x1 - x0, ch = y1 - y0;
        var rgb = new float[ShadingWidth * ShadingHeight * 3];
        Parallel.For(0, ShadingHeight, ComputeLimits.Options, b =>
        {
            for (var a = 0; a < ShadingWidth; a++)
            {
                var (rx, ry) = map.Source(a / (double)(ShadingWidth - 1), b / (double)(ShadingHeight - 1));
                // Photo (pixel edges) → map (pixel centres)
                var mx = (rx * w - x0) / cw * light.Width - 0.5;
                var my = (ry * h - y0) / ch * light.Height - 0.5;
                var k = (b * ShadingWidth + a) * 3;
                for (var c = 0; c < 3; c++) rgb[k + c] = Bilinear(light.Rgb, light.Width, light.Height, c, mx, my);
            }
        });
        var strength = Math.Min(1, 0.4 / Math.Max(light.Brightness, 1e-6));
        return new DewarpShading(ShadingWidth, ShadingHeight, rgb, strength, on);
    }

    private static float Bilinear(float[] planes, int w, int h, int c, double x, double y)
    {
        x = Math.Clamp(x, 0, w - 1);
        y = Math.Clamp(y, 0, h - 1);
        int ix = Math.Min((int)x, w - 2), iy = Math.Min((int)y, h - 2);
        float ax = (float)(x - ix), ay = (float)(y - iy);
        var o = c * w * h;
        float At(int i, int j) => planes[o + j * w + i];
        return (At(ix, iy) * (1 - ax) + At(ix + 1, iy) * ax) * (1 - ay) + (At(ix, iy + 1) * (1 - ax) + At(ix + 1, iy + 1) * ax) * ay;
    }

    /// <summary>
    /// Image crop → model (input inW × inH) → grid as positions in the image (pixel edges: 0 and w are the borders);
    /// with <paramref name="shadow"/> also the shading map (0 … 1, averaged over 4 × 4 pixels).
    /// </summary>
    private static ((double X, double Y)[,] Grid, Light? Light) Predict(InferenceSession session, MagickImage doc,
        int x0, int y0, int x1, int y1, int inW, int inH, bool shadow, CancellationToken ct)
    {
        // Crop, reduced with a triangle filter (like the prefiltered downsampling used in training)
        using var crop = ImageOps.CloneOf(doc);
        crop.Crop(new MagickGeometry(x0, y0, (uint)(x1 - x0), (uint)(y1 - y0)));
        crop.ResetPage();
        using var small = ImageOps.Resize(crop, inW, inH, ResampleFilter.Bilinear, gammaCorrect: false);
        var px = ImageOps.ToDisplayPixels(small);   // B, G, R, A, 0 … 1
        var t = new float[3 * inH * inW];
        try
        {
            Parallel.For(0, inH, ComputeLimits.Options, j =>
            {
                for (var i = 0; i < inW; i++)
                {
                    var k = (j * inW + i) * 4;
                    t[j * inW + i] = px[k + 2] * 2 - 1;                     // R, scaled to −1 … 1
                    t[(inH + j) * inW + i] = px[k + 1] * 2 - 1;             // G
                    t[(2 * inH + j) * inW + i] = px[k] * 2 - 1;             // B
                }
            });
        }
        finally
        {
            PixelPool.Return(px);
        }
        ct.ThrowIfCancellationRequested();
        using var value = OrtValue.CreateTensorValueFromMemory(t, [1, 3, inH, inW]);
        var outputs = OnnxSessions.Run(session, new Dictionary<string, OrtValue> { [session.InputMetadata.Keys.First()] = value },
            shadow ? ["bm_map", "shadow_map"] : ["bm_map"], ct);
        var bm = outputs[0];
        Light? light = null;
        if (shadow)
        {
            // −1 … 1 → 0 … 1, averaged over 4 × 4 pixels (the raster in the result is a quarter as fine anyway)
            int lw = inW / 4, lh = inH / 4;
            var raw = outputs[1];
            var light4 = new float[3 * lw * lh];
            Parallel.For(0, 3 * lh, ComputeLimits.Options, row =>
            {
                int c = row / lh, j = row % lh;
                for (var i = 0; i < lw; i++)
                {
                    double s = 0;
                    for (var dy = 0; dy < 4; dy++)
                    for (var dx = 0; dx < 4; dx++) s += raw[(c * inH + j * 4 + dy) * inW + i * 4 + dx];
                    light4[(c * lh + j) * lw + i] = (float)(s / 16 * 0.5 + 0.5);
                }
            });
            double sum = 0;
            foreach (var v in t) sum += v;
            light = new Light(light4, lw, lh, (sum / t.Length + 1) / 2);
        }
        var grid = new (double X, double Y)[GridHeight, GridWidth];
        double cw = x1 - x0, ch = y1 - y0;
        for (var j = 0; j < GridHeight; j++)
        for (var i = 0; i < GridWidth; i++)
        {
            double vx = bm[j * GridWidth + i], vy = bm[GridHeight * GridWidth + j * GridWidth + i];
            // As applied with the model (align_corners = True): ±1 are the centres of the outermost pixels of the crop
            grid[j, i] = (x0 + 0.5 + (vx + 1) / 2 * (cw - 1), y0 + 0.5 + (vy + 1) / 2 * (ch - 1));
        }
        return (grid, light);
    }

    private static DewarpGrid ToGrid((double X, double Y)[,] grid, int w, int h, double focal35)
    {
        static double Dist((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        // Size: the longest grid row in image pixels, so that the best-sampled edge gets one result pixel per image pixel
        double rows = 0, cols = 0, longest = 0;
        for (var j = 0; j < GridHeight; j++)
        {
            double s = 0;
            for (var i = 0; i + 1 < GridWidth; i++) s += Dist(grid[j, i], grid[j, i + 1]);
            rows += s;
            longest = Math.Max(longest, s);
        }
        for (var i = 0; i < GridWidth; i++)
        for (var j = 0; j + 1 < GridHeight; j++) cols += Dist(grid[j, i], grid[j + 1, i]);
        var arcAspect = (cols / GridWidth) / Math.Max(1e-9, rows / GridHeight);   // height / width as the photo shows it

        // Aspect ratio from the sheet model: 9 grid rows and 4 grid columns as lines; the outer ones count as sheet edges
        var lines = new List<WarpLine>();
        foreach (var j in Enumerable.Range(0, 9).Select(k => (int)Math.Round(k * (GridHeight - 1) / 8.0)))
            lines.Add(new WarpLine(WarpLineKind.Horizontal,
                Enumerable.Range(0, GridWidth).Where(i => i % 11 == 0 || i == GridWidth - 1).Select(i => (grid[j, i].X / w, grid[j, i].Y / h)).ToArray()));
        foreach (var i in Enumerable.Range(0, 4).Select(k => (int)Math.Round(k * (GridWidth - 1) / 3.0)))
            lines.Add(new WarpLine(WarpLineKind.Vertical,
                Enumerable.Range(0, GridHeight).Where(j => j % 8 == 0 || j == GridHeight - 1).Select(j => (grid[j, i].X / w, grid[j, i].Y / h)).ToArray()));
        var model = Dewarp.MapFor(new DewarpSpec(lines, focal35), w, h);
        var (aspect, din) = model is { PageAspect: { } a } ? (a, model.Din) : (arcAspect, false);

        var sx = new float[GridHeight * GridWidth];
        var sy = new float[GridHeight * GridWidth];
        for (var j = 0; j < GridHeight; j++)
        for (var i = 0; i < GridWidth; i++)
            (sx[j * GridWidth + i], sy[j * GridWidth + i]) = ((float)(grid[j, i].X / w), (float)(grid[j, i].Y / h));
        var relWidth = longest / w;
        return new DewarpGrid(GridWidth, GridHeight, sx, sy, relWidth, relWidth * aspect, aspect, din);
    }
}
