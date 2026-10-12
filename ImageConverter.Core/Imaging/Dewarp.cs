using ImageMagick;

namespace ImageConverter.Core.Imaging;

/// <summary>
/// Ergebnis von <see cref="Dewarp.MapFor"/>: welcher Punkt des Pixelbilds an welche Stelle des gerade gezogenen
/// Ergebnisses kommt. Ein Gitter über das Ergebnis (bilinear dazwischen), Koordinaten relativ — dieselbe Abbildung
/// passt auf jede Größe des Pixelbilds (Vorschau, volle Größe).
/// </summary>
public sealed class DewarpMap
{
    private readonly int _nx, _ny;            // Knoten waagerecht, senkrecht
    private readonly float[] _sx, _sy;        // Stelle im Pixelbild, relativ (x / Breite, y / Höhe)

    internal DewarpMap(int nx, int ny, float[] sx, float[] sy, double relWidth, double relHeight)
    {
        (_nx, _ny, _sx, _sy) = (nx, ny, sx, sy);
        (RelWidth, RelHeight) = (relWidth, relHeight);
    }

    /// <summary>Breite des Ergebnisses in Breiten des Pixelbilds.</summary>
    public double RelWidth { get; }

    /// <summary>Höhe des Ergebnisses in Breiten des Pixelbilds.</summary>
    public double RelHeight { get; }

    /// <summary>Mit Blattmodell im Raum gerechnet (sonst nur die Linien gerade gezogen, Seitenverhältnis wie vorher).</summary>
    public bool Modelled { get; internal init; }

    /// <summary>Doppelseite (mit Falz).</summary>
    public bool DoublePage { get; internal init; }

    /// <summary>Seitenverhältnis einer Seite (Höhe / Breite), wenn die Linien das Blatt umranden; sonst null.</summary>
    public double? PageAspect { get; internal init; }

    /// <summary>Auf DIN (1 : √2) eingerastet.</summary>
    public bool Din { get; internal init; }

    /// <summary>Wie gut das Blattmodell passt: mittlere Abweichung der Punkte in Tausendsteln der Bildbreite.</summary>
    public double ModelError { get; internal init; }

    internal DewarpMap With(bool modelled, bool doublePage, double? pageAspect, bool din, double modelError) =>
        new(_nx, _ny, _sx, _sy, RelWidth, RelHeight)
        {
            Modelled = modelled, DoublePage = doublePage, PageAspect = pageAspect, Din = din, ModelError = modelError,
        };

    /// <summary>
    /// Das Pixelbild gerade gezogen (neues Bild mit denselben Profilen; die Quelle bleibt). Je Ergebnispixel die Stelle im
    /// Pixelbild aus dem Gitter, dort bikubisch (Catmull-Rom) gelesen.
    /// </summary>
    public MagickImage Render(MagickImage source)
    {
        int sw, sh;
        bool alpha;
        lock (source) (sw, sh, alpha) = ((int)source.Width, (int)source.Height, source.HasAlpha);
        var (ow, oh) = SizeFor(sw);
        var src = Pixels.FromMagick(source);
        var dst = PixelPool.Rent(ow * oh * 4);
        Parallel.For(0, oh, y =>
        {
            var v = (y + 0.5) / oh;
            for (var x = 0; x < ow; x++)
            {
                var (rx, ry) = Source((x + 0.5) / ow, v);
                SampleCubic(src, sw, sh, rx * sw - 0.5, ry * sh - 0.5, dst, (y * ow + x) * 4);
            }
        });
        PixelPool.Return(src);
        MagickImage result;
        lock (source) result = Pixels.ToMagick16(dst, ow, oh, alpha);
        PixelPool.Return(dst);
        ImageExporter.CopyProfiles(source, result);
        lock (source) result.Density = source.Density;
        return result;
    }

    private static void SampleCubic(float[] src, int w, int h, double x, double y, float[] dst, int o)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        Span<double> wx = stackalloc double[4];
        Span<double> wy = stackalloc double[4];
        CatmullRom(fx, wx);
        CatmullRom(fy, wy);
        Span<double> acc = stackalloc double[4];
        for (var j = 0; j < 4; j++)
        {
            var row = Math.Clamp(y0 - 1 + j, 0, h - 1) * w;
            for (var i = 0; i < 4; i++)
            {
                var k = (row + Math.Clamp(x0 - 1 + i, 0, w - 1)) * 4;
                var weight = wx[i] * wy[j];
                acc[0] += weight * src[k];
                acc[1] += weight * src[k + 1];
                acc[2] += weight * src[k + 2];
                acc[3] += weight * src[k + 3];
            }
        }
        for (var c = 0; c < 4; c++) dst[o + c] = (float)Math.Clamp(acc[c], 0, 1);
    }

    private static void CatmullRom(double t, Span<double> w)
    {
        double t2 = t * t, t3 = t2 * t;
        w[0] = -0.5 * t3 + t2 - 0.5 * t;
        w[1] = 1.5 * t3 - 2.5 * t2 + 1;
        w[2] = -1.5 * t3 + 2 * t2 + 0.5 * t;
        w[3] = 0.5 * t3 - 0.5 * t2;
    }

    /// <summary>Größe des Ergebnisses für ein Pixelbild dieser Breite.</summary>
    public (int Width, int Height) SizeFor(int width) =>
        (Math.Max(1, (int)Math.Round(RelWidth * width)), Math.Max(1, (int)Math.Round(RelHeight * width)));

    /// <summary>Stelle (u, v ∈ 0 … 1) im Ergebnis → Stelle im Pixelbild (relativ).</summary>
    public (double X, double Y) Source(double u, double v)
    {
        var fx = Math.Clamp(u, 0, 1) * (_nx - 1);
        var fy = Math.Clamp(v, 0, 1) * (_ny - 1);
        int ix = Math.Min((int)fx, _nx - 2), iy = Math.Min((int)fy, _ny - 2);
        double ax = fx - ix, ay = fy - iy;
        int i = iy * _nx + ix;
        double Lerp(float[] g) =>
            (g[i] * (1 - ax) + g[i + 1] * ax) * (1 - ay) + (g[i + _nx] * (1 - ax) + g[i + _nx + 1] * ax) * ay;
        return (Lerp(_sx), Lerp(_sy));
    }

    /// <summary>Umkehrung von <see cref="Source"/>: Stelle im Pixelbild (relativ) → Stelle im Ergebnis (0 … 1, auch außerhalb).</summary>
    public (double U, double V) Result(double x, double y)
    {
        // Startpunkt: nächster Knoten, dann Newton auf der bilinearen Abbildung
        var best = 0;
        var bestD = double.MaxValue;
        for (var k = 0; k < _sx.Length; k++)
        {
            double dx = _sx[k] - x, dy = _sy[k] - y, d = dx * dx + dy * dy;
            if (d < bestD) (bestD, best) = (d, k);
        }
        double u = (best % _nx) / (double)(_nx - 1), v = (best / _nx) / (double)(_ny - 1);
        const double h = 1e-4;
        for (var it = 0; it < 30; it++)
        {
            var (px, py) = Source(u, v);
            double ex = x - px, ey = y - py;
            if (ex * ex + ey * ey < 1e-14) break;
            var (ux, uy) = Source(u + h, v);
            var (vx, vy) = Source(u, v + h);
            double a = (ux - px) / h, b = (vx - px) / h, c = (uy - py) / h, d = (vy - py) / h;
            var det = a * d - b * c;
            if (Math.Abs(det) < 1e-12) break;
            u += (d * ex - b * ey) / det;
            v += (-c * ex + a * ey) / det;
        }
        return (u, v);
    }
}

/// <summary>
/// Gewölbtes Blatt gerade ziehen: aus nachgezeichneten Linien (in Wirklichkeit gerade) eine Abbildung, die das Blatt
/// glättet — das ganze Bild bleibt, beschnitten auf das größte volle Rechteck.
/// </summary>
/// <remarks>
/// <para><b>1. Blattmodell im Raum</b> (wie page_dewarp, Zucker 2016; Kim/Koo/Cho 2015): Kamera mit Brennweite aus den
/// EXIF-Daten, das Blatt als in einer Richtung gebogene Fläche z = h(x) (abwickelbar wie Papier; Biegung als natürlicher
/// kubischer Spline über 8 Abschnitte, an den Rändern 0), Doppelseite: zwei Splines, am Falz verbunden, dort mit Knick.
/// Waagerechte Linien liegen auf dem Blatt bei y = konst., senkrechte bei x = konst., der Falz bei x = Falz. Unbekannt
/// sind Lage, Biegung, je Linie ihre Konstante und je Punkt seine Stelle entlang der Linie — gelöst mit
/// Levenberg-Marquardt (Abstand der Punkte zu ihrer Projektion). Gerechnet wird beides, Biegung entlang x und entlang y;
/// es gilt das Bessere. Abgewickelt nach Bogenlänge ergibt sich das echte Seitenverhältnis; liegt es höchstens 3 %
/// neben DIN (1 : √2), wird DIN genommen.</para>
/// <para><b>2. Feinkorrektur mit Gitter</b> (wie die Weitwinkelkorrektur bei Carroll/Agrawala/Agarwala 2009): Was das
/// Modell nicht trifft (welliges Papier), gleicht ein glattes Verschiebungsfeld aus, bis jeder Punkt auf seiner geraden
/// Linie liegt. Mit nur einer Linie (oder wenn das Modell nicht passt) nur dieses Feld: die Linie wird gerade, das
/// Seitenverhältnis bleibt.</para>
/// <para><b>3.</b> Das ganze Bild bleibt erhalten (außerhalb des Blatts läuft die Biegung flach aus); beschnitten wird auf
/// das größte Rechteck ohne leere Stellen.</para>
/// </remarks>
public static class Dewarp
{
    /// <summary>Höchstens so viel neben DIN, damit eingerastet wird.</summary>
    public const double DinTolerance = 0.03;

    private const double Scale = 1000;   // Fehler in Tausendsteln der Bildbreite (≈ Pixel bei 1000 px)

    /// <summary>
    /// Brennweite der Aufnahme als Kleinbild-Äquivalent aus den EXIF-Daten (FocalLengthIn35mmFilm); fehlt sie,
    /// <see cref="DewarpSpec.DefaultFocal35"/> (Handy-Hauptkamera). Reicht für das Blattmodell — ein paar Millimeter
    /// daneben ändern vor allem das Seitenverhältnis um wenige Prozent.
    /// </summary>
    public static double Focal35Of(MagickImage image)
    {
        lock (image)
        {
            if (image.GetExifProfile()?.GetValue(ExifTag.FocalLengthIn35mmFilm)?.Value is ushort mm and >= 8 and <= 600)
                return mm;
        }
        return DewarpSpec.DefaultFocal35;
    }

    // ------------------------------------------------------------ Zwischenspeicher

    private static readonly List<(DewarpSpec Spec, long Aspect, DewarpMap? Map)> Cache = [];
    private const int CacheSize = 12;

    /// <summary>
    /// Abbildung für ein Pixelbild dieser Größe (gerechnet einmal je Linien und Seitenverhältnis, dann aus dem
    /// Zwischenspeicher). null: nichts zu tun (keine brauchbare Linie).
    /// </summary>
    public static DewarpMap? MapFor(DewarpSpec spec, int width, int height)
    {
        if (!spec.IsUsable || width <= 0 || height <= 0) return null;
        var aspect = (double)height / width;
        var key = (long)Math.Round(aspect * 1000);   // Vorschau und volle Größe: gleiches Seitenverhältnis
        lock (Cache)
        {
            var i = Cache.FindIndex(e => e.Aspect == key && e.Spec.Equals(spec));
            if (i >= 0)
            {
                var hit = Cache[i];
                Cache.RemoveAt(i);
                Cache.Insert(0, hit);
                return hit.Map;
            }
        }
        var map = Solve(spec, key / 1000.0);
        lock (Cache)
        {
            Cache.Insert(0, (spec, key, map));
            while (Cache.Count > CacheSize) Cache.RemoveAt(Cache.Count - 1);
        }
        return map;
    }

    // ------------------------------------------------------------ Rechnen

    /// <summary>Beobachtete Linie in Bildmaß (x / Breite, y / Breite — beide Achsen gleich skaliert).</summary>
    private sealed record Obs(WarpLineKind Kind, (double X, double Y)[] Pts);

    internal static DewarpMap? Solve(DewarpSpec spec, double aspect)
    {
        var (obs, cam) = Prepare(spec, aspect);
        if (obs.Count == 0) return null;
        var best = BestFit(obs, cam);
        return best is not null ? ModelMap(best, obs, cam, aspect) : FlatMap(obs, aspect);
    }

    private static (List<Obs> Obs, Camera Cam) Prepare(DewarpSpec spec, double aspect)
    {
        var obs = spec.UsableLines.Select(l => new Obs(l.Kind, l.Points.Select(p => (p.X, p.Y * aspect)).ToArray())).ToList();
        // Nur ein Falz: weitere Falzlinien zählen als senkrechte Linien
        var firstFold = obs.FindIndex(o => o.Kind == WarpLineKind.Fold);
        for (var i = 0; i < obs.Count; i++)
            if (obs[i].Kind == WarpLineKind.Fold && i != firstFold) obs[i] = obs[i] with { Kind = WarpLineKind.Vertical };
        var f = spec.Focal35 / 43.27 * Math.Sqrt(1 + aspect * aspect);   // Brennweite in Bildbreiten
        return (obs, new Camera(f, 0.5, aspect / 2));
    }

    /// <summary>Blattmodell, Biegung entlang x und entlang y gerechnet, das Bessere; null = zu wenig Linien oder unplausibel.</summary>
    private static Fit? BestFit(List<Obs> obs, Camera cam)
    {
        if (obs.Count < 2) return null;
        var fold = obs.Any(o => o.Kind == WarpLineKind.Fold);
        Fit? best = null;
        foreach (var alongY in new[] { false, true })
        {
            if (alongY && fold) continue;   // Falz: Biegung quer zum Falz
            var fit = FitModel(obs, cam, alongY);
            if (fit is not null && (best is null || fit.Cost < best.Cost)) best = fit;
        }
        return best is { Plausible: true } ? best : null;
    }

    /// <summary>
    /// Welche Vorschläge passen nicht zu den übrigen Linien? Das Blattmodell wird mit allen angewählten Linien gerechnet; die
    /// am schlechtesten passende Linie unter den <paramref name="candidates"/> fällt heraus, wenn sie deutlich schlechter ist
    /// als der Rest (mehr als das 2,5-Fache des Medians der ersten Runde, mindestens 0,8 ‰ der Bildbreite) und das Modell ohne
    /// sie trägt und mindestens 10 % besser passt — bis zu viermal. So verderben
    /// eine falsch erkannte Kante oder eine verrutschte Zeile nicht das Ergebnis. Andere Linien (von Hand) bleiben immer.
    /// </summary>
    /// <returns>Für jede Linie aus <paramref name="spec"/>: behalten?</returns>
    public static bool[] RejectOutliers(DewarpSpec spec, IReadOnlyList<bool> candidates, int width, int height)
    {
        var keep = Enumerable.Repeat(true, spec.Lines.Count).ToArray();
        var aspect = (double)height / Math.Max(1, width);
        Fit? FitOf(IEnumerable<int> lines)
        {
            var (obs, cam) = Prepare(spec with { Lines = lines.Select(i => spec.Lines[i]).ToArray() }, aspect);
            return BestFit(obs, cam);
        }
        var active = Enumerable.Range(0, spec.Lines.Count).Where(i => spec.Lines[i].Points.Count >= 2).ToList();
        var fit = FitOf(active);
        // Maßstab: der Median der ersten Runde — eine schlechte Linie verbiegt das Modell, dann passen alle schlechter
        var reference = fit is null ? 0 : fit.LineRms.OrderBy(v => v).ElementAt(fit.LineRms.Length / 2);
        for (var round = 0; round < 4 && fit is not null && active.Count >= 4; round++)
        {
            var median = Math.Min(reference, fit.LineRms.OrderBy(v => v).ElementAt(fit.LineRms.Length / 2));
            var worst = -1;
            for (var k = 0; k < active.Count; k++)
                if (candidates[active[k]] && (worst < 0 || fit.LineRms[k] > fit.LineRms[worst])) worst = k;
            if (worst < 0 || fit.LineRms[worst] < Math.Max(0.8, 2.5 * median)) break;
            // nur, wenn das Modell ohne die Linie trägt und insgesamt besser passt — sonst lag es nicht an ihr
            var without = active.Where((_, k) => k != worst).ToList();
            var next = FitOf(without);
            if (next is null || next.Rms >= 0.9 * fit.Rms) break;
            keep[active[worst]] = false;
            (active, fit) = (without, next);
        }
        return keep;
    }

    // ------------------------------------------------------------ Kamera und Blatt

    private readonly record struct Camera(double F, double Cx, double Cy)
    {
        public (double U, double V, bool Ok) Project(double x, double y, double z) =>
            z <= 1e-6 ? (0, 0, false) : (Cx + F * x / z, Cy + F * y / z, true);
    }

    /// <summary>Biegung: ein oder zwei natürliche kubische Splines über gleichabständige Knoten, außen flach auslaufend.</summary>
    private sealed class Profile
    {
        private readonly (double A, double B, double[] H, double[] M)[] _pieces;
        private readonly double _tail;
        private readonly Func<double, (double Z, double D)>? _extra;

        /// <param name="extra">Zusätzlich zum Spline (Doppelseite: am Falz steil ansteigend, wie √).</param>
        public Profile(Func<double, (double Z, double D)>? extra, params (double A, double B, double[] H)[] pieces)
        {
            _pieces = pieces.Select(p => (p.A, p.B, p.H, SecondDerivatives(p.H, (p.B - p.A) / (p.H.Length - 1)))).ToArray();
            (Min, Max) = (_pieces[0].A, _pieces[^1].B);
            _tail = Math.Max(1e-3, 0.1 * (Max - Min));
            _extra = extra;
        }

        public double Min { get; }
        public double Max { get; }

        public (double Z, double D) Eval(double a)
        {
            if (a < Min)
            {
                var (z0, d0) = Inside(Min);
                var e = Math.Exp(-(Min - a) / _tail);
                return (z0 - d0 * _tail * (1 - e), d0 * e);
            }
            if (a > Max)
            {
                var (z1, d1) = Inside(Max);
                var e = Math.Exp(-(a - Max) / _tail);
                return (z1 + d1 * _tail * (1 - e), d1 * e);
            }
            return Inside(a);
        }

        private (double Z, double D) Inside(double a)
        {
            var p = _pieces[^1];
            foreach (var q in _pieces)
                if (a <= q.B) { p = q; break; }
            var n = p.H.Length - 1;
            var dx = (p.B - p.A) / n;
            var i = Math.Clamp((int)Math.Floor((a - p.A) / dx), 0, n - 1);
            double x0 = p.A + i * dx, x1 = x0 + dx, l = x1 - a, r = a - x0;
            double c0 = p.H[i] / dx - p.M[i] * dx / 6, c1 = p.H[i + 1] / dx - p.M[i + 1] * dx / 6;
            var z = p.M[i] * l * l * l / (6 * dx) + p.M[i + 1] * r * r * r / (6 * dx) + c0 * l + c1 * r;
            var d = -p.M[i] * l * l / (2 * dx) + p.M[i + 1] * r * r / (2 * dx) - c0 + c1;
            if (_extra is not null)
            {
                var (ez, ed) = _extra(a);
                (z, d) = (z + ez, d + ed);
            }
            return (z, d);
        }

        private static double[] SecondDerivatives(double[] h, double dx)
        {
            var n = h.Length - 1;
            var m = new double[n + 1];
            if (n < 2) return m;
            // M[i-1] + 4 M[i] + M[i+1] = 6 (h[i-1] − 2 h[i] + h[i+1]) / dx², M[0] = M[n] = 0 (Thomas)
            var c = new double[n];
            var d = new double[n];
            for (var i = 1; i < n; i++)
            {
                var rhs = 6 * (h[i - 1] - 2 * h[i] + h[i + 1]) / (dx * dx);
                var den = 4 - (i > 1 ? c[i - 1] : 0);
                c[i] = 1 / den;
                d[i] = (rhs - (i > 1 ? d[i - 1] : 0)) / den;
            }
            for (var i = n - 1; i >= 1; i--) m[i] = d[i] - c[i] * (i < n - 1 ? m[i + 1] : 0);
            return m;
        }
    }

    /// <summary>Das angepasste Modell: Lage, Biegung und für jeden Punkt seine Stelle auf dem Blatt.</summary>
    private sealed class Fit
    {
        public required double[,] R;
        public required double[] T;
        public required Profile Profile;
        public required bool AlongY;
        public required double? FoldX;
        public required (double X, double Y)[][] Page;   // je Beobachtung und Punkt
        public required double Cost;
        public required double Rms;                       // Tausendstel der Bildbreite
        public required double Span;                      // Ausdehnung der Punkte auf dem Blatt
        public required double[] LineRms;                 // je Linie, Tausendstel der Bildbreite

        public bool Plausible
        {
            get
            {
                if (!double.IsFinite(Cost) || T[2] <= 0) return false;
                var maxZ = 0.0;
                for (var a = Profile.Min; a <= Profile.Max; a += (Profile.Max - Profile.Min) / 64)
                    maxZ = Math.Max(maxZ, Math.Abs(Profile.Eval(a).Z));
                return maxZ < 0.6 * Span && Rms < 25;
            }
        }

        public (double X, double Y, double Z) Camera(double x, double y)
        {
            var z = Profile.Eval(AlongY ? y : x).Z;
            return (R[0, 0] * x + R[0, 1] * y + R[0, 2] * z + T[0],
                    R[1, 0] * x + R[1, 1] * y + R[1, 2] * z + T[1],
                    R[2, 0] * x + R[2, 1] * y + R[2, 2] * z + T[2]);
        }
    }

    private static Fit? FitModel(List<Obs> obs, Camera cam, bool alongY)
    {
        if (InitialPose(obs, cam) is not { } init) return null;
        var (r0, t0, page0) = init;

        // Bereich der Biegung: Ausdehnung der Punkte entlang der gebogenen Richtung
        var bent = page0.SelectMany(p => p).Select(p => alongY ? p.Y : p.X).ToArray();
        double lo = bent.Min(), hi = bent.Max(), span = Math.Max(1e-3, hi - lo);
        lo -= 0.02 * span;
        hi += 0.02 * span;
        var foldIndex = obs.FindIndex(o => o.Kind == WarpLineKind.Fold);
        double? foldX = foldIndex >= 0 ? page0[foldIndex].Average(p => p.X) : null;
        if (foldX is { } fx && (fx <= lo + 0.1 * span || fx >= hi - 0.1 * span)) foldX = null;   // Falz am Rand: kein Falz

        // Unbekannte: Drehung (3), Verschiebung (3), Höhen, je Linie eine Konstante. Die Stelle jedes Punkts entlang seiner
        // Linie ist keine Unbekannte des Lösers, sondern wird bei jeder Auswertung bestimmt (Variable Projection, Golub/Pereyra):
        // die Stelle der Linie, deren Projektion dem Punkt am nächsten liegt. Der Löser sieht so rund 40 statt mehrere hundert
        // Unbekannte — bei 20 vorgeschlagenen Linien Sekundenbruchteile statt halber Minuten.
        const int segments = 8, half = 6;   // Doppelseite: je Seite 6 Abschnitte (am Falz steil, abgeglichen in DewarpTests)
        var nHeights = foldX is null ? segments - 1 : 2 * half - 1;   // Falz: links und rechts je 5, dazu die Höhe am Falz
        var nSteep = foldX is null ? 0 : 2;                           // Falz: je Seite ein steiler Anteil (√)
        var lineParam = new int[obs.Count];
        var n = 6 + nHeights + nSteep;
        for (var i = 0; i < obs.Count; i++) lineParam[i] = obs[i].Kind == WarpLineKind.Fold ? -1 : n++;

        var theta = new double[n];
        var rv = RotationVector(r0);
        (theta[0], theta[1], theta[2]) = (rv[0], rv[1], rv[2]);
        (theta[3], theta[4], theta[5]) = (t0[0], t0[1], t0[2]);
        for (var i = 0; i < obs.Count; i++)
        {
            if (lineParam[i] < 0) continue;
            theta[lineParam[i]] = obs[i].Kind == WarpLineKind.Horizontal ? page0[i].Average(p => p.Y) : page0[i].Average(p => p.X);
        }
        // Stelle entlang der Linie (waagerecht: x, sonst y); Startwerte aus der Anfangslage, nach jedem Schritt nachgeführt
        var along = obs.Select((o, i) => page0[i].Select(p => o.Kind == WarpLineKind.Horizontal ? p.X : p.Y).ToArray()).ToArray();

        Profile ProfileOf(double[] th)
        {
            if (foldX is not { } fold)
            {
                var hs = new double[segments + 1];
                for (var k = 1; k < segments; k++) hs[k] = th[6 + k - 1];
                return new Profile(null, (lo, hi, hs));
            }
            var left = new double[half + 1];
            var right = new double[half + 1];
            for (var k = 1; k < half; k++) left[k] = th[6 + k - 1];
            left[half] = right[0] = th[6 + half - 1];
            for (var k = 1; k < half; k++) right[k] = th[6 + half + k - 1];
            // Am Falz steigt das Papier fast senkrecht an (Bindung): g(u) = √u · (1 − u), u = Abstand vom Falz / Seitenbreite —
            // am Falz und am Außenrand 0, am Falz unendlich steil. Ein Spline mit gleichen Abständen schafft das nicht.
            double kl = th[6 + nHeights], kr = th[6 + nHeights + 1];
            (double Z, double D) Steep(double x)
            {
                var (k, width, sign) = x < fold ? (kl, fold - lo, -1.0) : (kr, hi - fold, 1.0);
                var u = Math.Clamp(Math.Abs(x - fold) / width, 1e-6, 1);
                var root = Math.Sqrt(u);
                return (k * root * (1 - u), k * ((1 - u) / (2 * root) - root) * sign / width);
            }
            return new Profile(Steep, (lo, fold, left), (fold, hi, right));
        }

        (double X, double Y) PageAt(double[] th, int i, double u) => obs[i].Kind switch
        {
            WarpLineKind.Horizontal => (u, th[lineParam[i]]),
            WarpLineKind.Vertical => (th[lineParam[i]], u),
            _ => (foldX ?? 0, u),
        };

        (double U, double V, bool Ok) ProjectAt(double[] th, double[,] rot, Profile prof, int i, double u)
        {
            var (x, y) = PageAt(th, i, u);
            var z = prof.Eval(alongY ? y : x).Z;
            return cam.Project(rot[0, 0] * x + rot[0, 1] * y + rot[0, 2] * z + th[3],
                               rot[1, 0] * x + rot[1, 1] * y + rot[1, 2] * z + th[4],
                               rot[2, 0] * x + rot[2, 1] * y + rot[2, 2] * z + th[5]);
        }

        // je Punkt die Stelle auf seiner Linie, deren Projektion am nächsten liegt (Gauß-Newton in einer Unbekannten)
        double[][] Along(double[] th, double[,] rot, Profile prof)
        {
            var result = new double[obs.Count][];
            for (var i = 0; i < obs.Count; i++)
            {
                result[i] = new double[obs[i].Pts.Length];
                for (var j = 0; j < obs[i].Pts.Length; j++)
                {
                    var u = along[i][j];
                    for (var it = 0; it < 8; it++)
                    {
                        var (pu, pv, ok) = ProjectAt(th, rot, prof, i, u);
                        const double h = 1e-6;
                        var (qu, qv, ok2) = ProjectAt(th, rot, prof, i, u + h);
                        if (!ok || !ok2) break;
                        double ju = (qu - pu) / h, jv = (qv - pv) / h, ex = pu - obs[i].Pts[j].X, ey = pv - obs[i].Pts[j].Y;
                        var den = ju * ju + jv * jv;
                        if (den < 1e-18) break;
                        var step = Math.Clamp((ju * ex + jv * ey) / den, -0.1 * span, 0.1 * span);
                        u -= step;
                        if (Math.Abs(step) < 1e-10) break;
                    }
                    result[i][j] = u;
                }
            }
            return result;
        }

        var tz0 = t0[2];
        var dataCount = obs.Sum(o => o.Pts.Length) * 2;
        double[] Evaluate(double[] th, out double[][] us)
        {
            var res = new List<double>(dataCount + nHeights + nSteep + 2);
            var rot = Rotation(th[0], th[1], th[2]);
            var prof = ProfileOf(th);
            us = Along(th, rot, prof);
            for (var i = 0; i < obs.Count; i++)
            for (var j = 0; j < obs[i].Pts.Length; j++)
            {
                var (u, v, ok) = ProjectAt(th, rot, prof, i, us[i][j]);
                if (!ok) { res.Add(1e4); res.Add(1e4); continue; }
                res.Add((u - obs[i].Pts[j].X) * Scale);
                res.Add((v - obs[i].Pts[j].Y) * Scale);
            }
            // Biegung glatt (zweite Differenzen) und klein; Maßstab festhalten (Blattgröße ↔ Abstand)
            var hs = Enumerable.Range(0, nHeights).Select(k => th[6 + k]).ToArray();
            double[] all = [0, .. hs, 0];   // Ränder 0 (mit Falz: links, Falz, rechts in einer Reihe)
            for (var k = 1; k < all.Length - 1; k++)
                if (foldX is null || k != half)   // am Falz darf das Papier knicken
                    res.Add(100 * (all[k - 1] - 2 * all[k] + all[k + 1]) / span);
            foreach (var hk in hs) res.Add(2 * hk / span);
            for (var k = 0; k < nSteep; k++) res.Add(2 * th[6 + nHeights + k] / span);
            res.Add(20 * (th[5] - tz0));
            return res.ToArray();
        }
        double[] Residuals(double[] th) => Evaluate(th, out _);

        // nach jedem angenommenen Schritt: Stellen entlang der Linien als neue Startwerte
        var (solved, cost) = LevenbergMarquardt(Residuals, theta, th => { Evaluate(th, out var us); along = us; });
        var final = Evaluate(solved, out var alongFinal);
        double sum = 0;
        for (var k = 0; k < dataCount; k++) sum += final[k] * final[k];
        // je Linie: mittlerer Abstand ihrer Punkte zur Projektion (Reihenfolge der Residuen wie in Evaluate)
        var lineRms = new double[obs.Count];
        for (int i = 0, k = 0; i < obs.Count; i++)
        {
            double s2 = 0;
            for (var j = 0; j < obs[i].Pts.Length; j++, k += 2) s2 += final[k] * final[k] + final[k + 1] * final[k + 1];
            lineRms[i] = Math.Sqrt(s2 / Math.Max(1, obs[i].Pts.Length));
        }

        var page = new (double X, double Y)[obs.Count][];
        for (var i = 0; i < obs.Count; i++)
            page[i] = alongFinal[i].Select(u => PageAt(solved, i, u)).ToArray();
        return new Fit
        {
            R = Rotation(solved[0], solved[1], solved[2]),
            T = [solved[3], solved[4], solved[5]],
            Profile = ProfileOf(solved),
            AlongY = alongY,
            FoldX = foldX,
            Page = page,
            Cost = cost,
            Rms = Math.Sqrt(sum / Math.Max(1, dataCount / 2)),
            LineRms = lineRms,
            Span = span,
        };
    }

    /// <summary>
    /// Anfangslage: aus den zwei äußersten Linien der gebogenen Richtung (bzw. der anderen) ein Viereck, daraus mit
    /// bekannter Brennweite Lage und Seitenverhältnis einer ebenen Seite (Homographie zerlegt). Sonst frontal.
    /// Liefert je Punkt seine Stelle auf dieser Ebene.
    /// </summary>
    private static (double[,] R, double[] T, (double X, double Y)[][] Page)? InitialPose(List<Obs> obs, Camera cam)
    {
        // Familien: entlang = Linien mit fester Blatt-y (waagerecht), quer = feste Blatt-x (senkrecht, Falz)
        var horizontal = obs.Where(o => o.Kind == WarpLineKind.Horizontal).ToList();
        var vertical = obs.Where(o => o.Kind != WarpLineKind.Horizontal).ToList();

        var dh = Principal(horizontal);
        var dv = Principal(vertical);
        // Blatt-x-Richtung im Bild; Blatt-y = 90° weiter (Bildschirm: y nach unten, rechtshändig wie das Bild)
        (double X, double Y) ex;
        if (dh is { } h) ex = h;
        else if (dv is { } v) ex = (v.Y, -v.X);
        else return null;
        if (ex.X < 0 || (Math.Abs(ex.X) < 1e-9 && ex.Y < 0)) ex = (-ex.X, -ex.Y);
        (double X, double Y) ey = (-ex.Y, ex.X);

        (double X, double Y)[]? quad = null;
        if (horizontal.Count >= 2)
        {
            var top = horizontal.MinBy(o => o.Pts.Average(p => Dot(p, ey)))!;
            var bottom = horizontal.MaxBy(o => o.Pts.Average(p => Dot(p, ey)))!;
            quad = [top.Pts.MinBy(p => Dot(p, ex)), top.Pts.MaxBy(p => Dot(p, ex)),
                    bottom.Pts.MaxBy(p => Dot(p, ex)), bottom.Pts.MinBy(p => Dot(p, ex))];
        }
        else if (vertical.Count >= 2)
        {
            var left = vertical.MinBy(o => o.Pts.Average(p => Dot(p, ex)))!;
            var right = vertical.MaxBy(o => o.Pts.Average(p => Dot(p, ex)))!;
            quad = [left.Pts.MinBy(p => Dot(p, ey)), right.Pts.MinBy(p => Dot(p, ey)),
                    right.Pts.MaxBy(p => Dot(p, ey)), left.Pts.MaxBy(p => Dot(p, ey))];
        }

        if (quad is not null && PerspectiveGeometry.IsValid(quad.Select(p => (p.X * 1000, p.Y * 1000)).ToArray())
            && PoseFromQuad(quad, cam) is { } pose)
        {
            var (r, t, aspectY) = pose;
            var hom = PerspectiveGeometry.Homography(quad, [(0, 0), (1, 0), (1, 1), (0, 1)]);
            var page = obs.Select(o => o.Pts.Select(p =>
            {
                var (s, q) = PerspectiveGeometry.Apply(hom, p.X, p.Y);
                return (s, q * aspectY);
            }).ToArray()).ToArray();
            return (r, t, page);
        }

        // Frontal in Abstand 1, nur um die Blickachse gedreht
        var phi = Math.Atan2(ex.Y, ex.X);
        var rz = Rotation(0, 0, phi);
        var frontal = obs.Select(o => o.Pts.Select(p =>
        {
            double xc = (p.X - cam.Cx) / cam.F, yc = (p.Y - cam.Cy) / cam.F;
            return (rz[0, 0] * xc + rz[1, 0] * yc, rz[0, 1] * xc + rz[1, 1] * yc);
        }).ToArray()).ToArray();
        return (rz, [0, 0, 1], frontal);
    }

    /// <summary>Ebene Seite aus einem Viereck (Ecken der Einheitsfläche) mit bekannter Brennweite: Lage und Seitenverhältnis.</summary>
    private static (double[,] R, double[] T, double AspectY)? PoseFromQuad((double X, double Y)[] quad, Camera cam)
    {
        double[] h;
        try { h = PerspectiveGeometry.Homography([(0, 0), (1, 0), (1, 1), (0, 1)], quad); }
        catch (ArgumentException) { return null; }
        double[] Col(double a, double b, double c) => [(a - cam.Cx * c) / cam.F, (b - cam.Cy * c) / cam.F, c];
        var c1 = Col(h[0], h[3], h[6]);
        var c2 = Col(h[1], h[4], h[7]);
        var c3 = Col(h[2], h[5], h[8]);
        var n1 = Norm(c1);
        if (n1 < 1e-12) return null;
        var lambda = 1 / n1;
        if (c3[2] * lambda < 0) lambda = -lambda;
        var r1 = c1.Select(v => v * lambda).ToArray();
        var aspectY = Norm(c2) / n1;
        var c2s = c2.Select(v => v * lambda).ToArray();
        var proj = r1[0] * c2s[0] + r1[1] * c2s[1] + r1[2] * c2s[2];
        var r2 = new[] { c2s[0] - proj * r1[0], c2s[1] - proj * r1[1], c2s[2] - proj * r1[2] };
        var n2 = Norm(r2);
        if (n2 < 1e-12) return null;
        for (var k = 0; k < 3; k++) r2[k] /= n2;
        var r3 = new[] { r1[1] * r2[2] - r1[2] * r2[1], r1[2] * r2[0] - r1[0] * r2[2], r1[0] * r2[1] - r1[1] * r2[0] };
        var r = new double[3, 3];
        for (var k = 0; k < 3; k++) (r[k, 0], r[k, 1], r[k, 2]) = (r1[k], r2[k], r3[k]);
        var t = c3.Select(v => v * lambda).ToArray();
        return t[2] > 0 ? (r, t, aspectY) : null;
    }

    /// <summary>Gemeinsame Hauptrichtung der Linien (Abweichungen von ihrem jeweiligen Schwerpunkt).</summary>
    private static (double X, double Y)? Principal(List<Obs> lines)
    {
        double sxx = 0, sxy = 0, syy = 0;
        foreach (var l in lines)
        {
            double mx = l.Pts.Average(p => p.X), my = l.Pts.Average(p => p.Y);
            foreach (var p in l.Pts)
            {
                double dx = p.X - mx, dy = p.Y - my;
                sxx += dx * dx; sxy += dx * dy; syy += dy * dy;
            }
        }
        if (sxx + syy < 1e-14) return null;
        var angle = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
        return (Math.Cos(angle), Math.Sin(angle));
    }

    // ------------------------------------------------------------ Abbildung aus dem Modell

    private static DewarpMap? ModelMap(Fit fit, List<Obs> obs, Camera cam, double aspect)
    {
        // Ausdehnung der Linien auf dem Blatt
        var pagePts = fit.Page.SelectMany(p => p).ToArray();
        double bx0 = pagePts.Min(p => p.X), bx1 = pagePts.Max(p => p.X), by0 = pagePts.Min(p => p.Y), by1 = pagePts.Max(p => p.Y);
        double bw = Math.Max(1e-3, bx1 - bx0), bh = Math.Max(1e-3, by1 - by0);

        // Abwickeln entlang der gebogenen Richtung: Bogenlänge als Tabelle (weit über das Blatt hinaus)
        double aLo, aHi;
        if (fit.AlongY) (aLo, aHi) = (by0 - 1.5 * bh, by1 + 1.5 * bh);
        else (aLo, aHi) = (bx0 - 1.5 * bw, bx1 + 1.5 * bw);
        const int n = 8192;
        var arc = new double[n + 1];
        var step = (aHi - aLo) / n;
        for (var k = 1; k <= n; k++)
        {
            var d0 = fit.Profile.Eval(aLo + (k - 1) * step).D;
            var d1 = fit.Profile.Eval(aLo + k * step).D;
            arc[k] = arc[k - 1] + step * 0.5 * (Math.Sqrt(1 + d0 * d0) + Math.Sqrt(1 + d1 * d1));
        }
        double Develop(double a) =>
            a <= aLo ? a - aLo : a >= aHi ? arc[n] + (a - aHi) : Lerp(arc, (a - aLo) / step);
        double Undevelop(double s)
        {
            if (s <= 0) return aLo + s;
            if (s >= arc[n]) return aHi + (s - arc[n]);
            int a = 0, b = n;
            while (b - a > 1) { var m = (a + b) / 2; if (arc[m] > s) b = m; else a = m; }
            var t = arc[b] > arc[a] ? (s - arc[a]) / (arc[b] - arc[a]) : 0;
            return aLo + (a + t) * step;
        }
        (double Sx, double Sy) Developed(double x, double y) => fit.AlongY ? (x, Develop(y)) : (Develop(x), y);
        (double X, double Y) Undeveloped(double sx, double sy) => fit.AlongY ? (sx, Undevelop(sy)) : (Undevelop(sx), sy);

        (double U, double V, bool Ok) ProjectPage(double x, double y)
        {
            var (px, py, pz) = fit.Camera(x, y);
            return cam.Project(px, py, pz);
        }

        // Blatt im Ergebnis so, wie es im Bild liegt (Vierteldrehung), Auflösung wie im Bild in der Blattmitte
        double cx = (bx0 + bx1) / 2, cy = (by0 + by1) / 2, delta = 0.01 * Math.Max(bw, bh);
        var c0 = ProjectPage(cx, cy);
        var cX = ProjectPage(cx + delta, cy);
        var cY = ProjectPage(cx, cy + delta);
        if (!c0.Ok || !cX.Ok || !cY.Ok) return FlatMap(obs, aspect);
        var turns = ((int)Math.Round(Math.Atan2(cX.V - c0.V, cX.U - c0.U) / (Math.PI / 2)) % 4 + 4) % 4;
        var density = Math.Sqrt(Math.Sqrt(Sq(cX.U - c0.U) + Sq(cX.V - c0.V)) / delta * (Math.Sqrt(Sq(cY.U - c0.U) + Sq(cY.V - c0.V)) / delta));

        // Seitenverhältnis: nur, wenn die Linien das Blatt umranden (je zwei waagerechte und senkrechte)
        var (dx0, dy0) = Developed(bx0, by0);
        var (dx1, dy1) = Developed(bx1, by1);
        double devW = Math.Abs(dx1 - dx0), devH = Math.Abs(dy1 - dy0), devCx = (dx0 + dx1) / 2, devCy = (dy0 + dy1) / 2;
        var framed = obs.Count(o => o.Kind == WarpLineKind.Horizontal) >= 2 && obs.Count(o => o.Kind != WarpLineKind.Horizontal) >= 2;
        double? pageAspect = null;
        var din = false;
        var stretch = 1.0;
        if (framed)
        {
            var pageW = fit.FoldX is not null ? devW / 2 : devW;
            var ratio = devH / Math.Max(1e-9, pageW);
            pageAspect = ratio;
            foreach (var target in new[] { Math.Sqrt(2), 1 / Math.Sqrt(2) })
            {
                if (Math.Abs(ratio / target - 1) > DinTolerance) continue;
                stretch = target / ratio;
                pageAspect = target;
                din = true;
                break;
            }
        }

        // Blatt (abgewickelt) → Ergebnis in Bildbreiten; und zurück
        (double Ox, double Oy) ToOut(double sx, double sy)
        {
            double a = (sx - devCx) * density, b = (sy - devCy) * stretch * density;
            return Turn(a, b, turns);
        }
        (double U, double V, bool Ok) FromOut(double ox, double oy)
        {
            var (a, b) = Turn(ox, oy, (4 - turns) % 4);
            var (x, y) = Undeveloped(a / density + devCx, b / (stretch * density) + devCy);
            return ProjectPage(x, y);
        }

        // Wohin der Bildrand fällt (Strahl schneidet das Blatt) — begrenzt auf das Blatt samt Umgebung
        var rot = fit.R;
        var origin = new[]
        {
            -(rot[0, 0] * fit.T[0] + rot[1, 0] * fit.T[1] + rot[2, 0] * fit.T[2]),
            -(rot[0, 1] * fit.T[0] + rot[1, 1] * fit.T[1] + rot[2, 1] * fit.T[2]),
            -(rot[0, 2] * fit.T[0] + rot[1, 2] * fit.T[1] + rot[2, 2] * fit.T[2]),
        };
        (double X, double Y)? Intersect(double u, double v)
        {
            double rx = (u - cam.Cx) / cam.F, ry = (v - cam.Cy) / cam.F;
            double ex = rot[0, 0] * rx + rot[1, 0] * ry + rot[2, 0];
            double ey = rot[0, 1] * rx + rot[1, 1] * ry + rot[2, 1];
            double ez = rot[0, 2] * rx + rot[1, 2] * ry + rot[2, 2];
            if (Math.Abs(ez) < 1e-9) return null;
            var lam = -origin[2] / ez;
            if (lam <= 0) return null;
            for (var it = 0; it < 30; it++)
            {
                double x = origin[0] + lam * ex, y = origin[1] + lam * ey;
                var (z, dz) = fit.Profile.Eval(fit.AlongY ? y : x);
                var g = origin[2] + lam * ez - z;
                var dg = ez - dz * (fit.AlongY ? ey : ex);
                if (Math.Abs(dg) < 1e-12) return null;
                var next = lam - g / dg;
                if (!double.IsFinite(next) || next <= 0) return null;
                if (Math.Abs(next - lam) < 1e-12) { lam = next; break; }
                lam = next;
            }
            return (origin[0] + lam * ex, origin[1] + lam * ey);
        }

        var border = new List<(double X, double Y)>();
        const int samples = 120;
        for (var k = 0; k <= samples; k++)
        {
            var t = (double)k / samples;
            foreach (var (u, v) in new[] { (t, 0.0), (t, aspect), (0.0, t * aspect), (1.0, t * aspect) })
                if (Intersect(u, v) is { } hit)
                {
                    var (sx, sy) = Developed(hit.X, hit.Y);
                    border.Add(ToOut(sx, sy));
                }
        }
        var corners = new[] { ToOut(dx0, dy0), ToOut(dx1, dy0), ToOut(dx1, dy1), ToOut(dx0, dy1) };
        double pw = corners.Max(c => c.Ox) - corners.Min(c => c.Ox), ph = corners.Max(c => c.Oy) - corners.Min(c => c.Oy);
        double lx = corners.Min(c => c.Ox) - 1.5 * pw, hx = corners.Max(c => c.Ox) + 1.5 * pw;
        double ly = corners.Min(c => c.Oy) - 1.5 * ph, hy = corners.Max(c => c.Oy) + 1.5 * ph;
        double ox0 = lx, ox1 = hx, oy0 = ly, oy1 = hy;
        if (border.Count > 8)
        {
            ox0 = Math.Max(lx, border.Min(p => p.X));
            ox1 = Math.Min(hx, border.Max(p => p.X));
            oy0 = Math.Max(ly, border.Min(p => p.Y));
            oy1 = Math.Min(hy, border.Max(p => p.Y));
        }
        if (ox1 - ox0 < 1e-3 || oy1 - oy0 < 1e-3) return FlatMap(obs, aspect);

        var grid = Grid.Over(ox0, oy0, ox1, oy1, (ox, oy) =>
        {
            var (u, v, ok) = FromOut(ox, oy);
            return ok ? (u, v) : (double.NaN, double.NaN);
        });

        // Feinkorrektur: jeder Punkt auf seine gerade Linie
        var data = new List<(double Ox, double Oy, double X, double Y)>();
        for (var i = 0; i < obs.Count; i++)
        for (var j = 0; j < obs[i].Pts.Length; j++)
        {
            var (sx, sy) = Developed(fit.Page[i][j].X, fit.Page[i][j].Y);
            var (ox, oy) = ToOut(sx, sy);
            data.Add((ox, oy, obs[i].Pts[j].X, obs[i].Pts[j].Y));
        }
        grid.Correct(data, 1, 1);

        return grid.ToMap(aspect) is { } map
            ? Annotate(map, true, fit.FoldX is not null, pageAspect, din, fit.Rms)
            : null;
    }

    // ------------------------------------------------------------ Nur die Linien gerade ziehen

    private static DewarpMap? FlatMap(List<Obs> obs, double aspect)
    {
        var grid = Grid.Over(0, 0, 1, aspect, (x, y) => (x, y));
        var data = new List<(double Ox, double Oy, double X, double Y)>();
        foreach (var o in obs)
        {
            // Sehne vom ersten zum letzten Punkt entlang der Linie; jeder Punkt soll auf ihr landen
            var dir = Principal([o]) ?? (1, 0);
            var a = o.Pts.MinBy(p => Dot(p, dir));
            var b = o.Pts.MaxBy(p => Dot(p, dir));
            double lx = b.X - a.X, ly = b.Y - a.Y, len2 = lx * lx + ly * ly;
            if (len2 < 1e-12) continue;
            foreach (var p in o.Pts)
            {
                var t = ((p.X - a.X) * lx + (p.Y - a.Y) * ly) / len2;
                data.Add((a.X + t * lx, a.Y + t * ly, p.X, p.Y));
            }
        }
        // Nur waagerechte Linien: ganze Spalten verschieben sich mit (wie ein gebogenes Blatt), nur senkrechte: Zeilen
        var hasH = obs.Any(o => o.Kind == WarpLineKind.Horizontal);
        var hasV = obs.Any(o => o.Kind != WarpLineKind.Horizontal);
        var (alongX, alongY) = hasH && !hasV ? (1.0, 300.0) : hasV && !hasH ? (300.0, 1.0) : (1.0, 1.0);
        grid.Correct(data, alongX, alongY);
        return grid.ToMap(aspect) is { } map ? Annotate(map, false, false, null, false, 0) : null;
    }

    private static DewarpMap Annotate(DewarpMap map, bool modelled, bool doublePage, double? pageAspect, bool din, double rms) =>
        map.With(modelled, doublePage, pageAspect, din, rms);

    // ------------------------------------------------------------ Gitter über das Ergebnis

    /// <summary>Knoten über einem Rechteck des Ergebnisses (in Bildbreiten), je Knoten die Stelle im Bild (Bildmaß).</summary>
    private sealed class Grid
    {
        private const int Cells = 160;   // Zellen über die längere Seite

        private readonly int _nx, _ny;
        private readonly double _x0, _y0, _dx, _dy;
        private readonly double[] _u, _v;

        private Grid(int nx, int ny, double x0, double y0, double dx, double dy)
        {
            (_nx, _ny, _x0, _y0, _dx, _dy) = (nx, ny, x0, y0, dx, dy);
            _u = new double[nx * ny];
            _v = new double[nx * ny];
        }

        public static Grid Over(double x0, double y0, double x1, double y1, Func<double, double, (double U, double V)> at)
        {
            var cell = Math.Max(x1 - x0, y1 - y0) / Cells;
            int nx = Math.Max(2, (int)Math.Ceiling((x1 - x0) / cell) + 1), ny = Math.Max(2, (int)Math.Ceiling((y1 - y0) / cell) + 1);
            var g = new Grid(nx, ny, x0, y0, (x1 - x0) / (nx - 1), (y1 - y0) / (ny - 1));
            Parallel.For(0, ny, j =>
            {
                for (var i = 0; i < nx; i++)
                    (g._u[j * nx + i], g._v[j * nx + i]) = at(x0 + i * g._dx, y0 + j * g._dy);
            });
            return g;
        }

        private (double U, double V) At(double ox, double oy)
        {
            var fx = Math.Clamp((ox - _x0) / _dx, 0, _nx - 1.000001);
            var fy = Math.Clamp((oy - _y0) / _dy, 0, _ny - 1.000001);
            int i = (int)fx, j = (int)fy, k = j * _nx + i;
            double ax = fx - i, ay = fy - j;
            double L(double[] g) => (g[k] * (1 - ax) + g[k + 1] * ax) * (1 - ay) + (g[k + _nx] * (1 - ax) + g[k + _nx + 1] * ax) * ay;
            return (L(_u), L(_v));
        }

        /// <summary>
        /// Glattes Verschiebungsfeld, damit die Stelle (Ox, Oy) des Ergebnisses genau auf (X, Y) im Bild zeigt:
        /// kleinste Quadrate mit Glätte (Nachbarknoten gleich, je Richtung gewichtet) und schwachem Zug zu null.
        /// </summary>
        public void Correct(List<(double Ox, double Oy, double X, double Y)> data, double alongX, double alongY)
        {
            var nodes = _nx * _ny;
            var taps = new List<(int K, double W)[]>();
            var du = new List<double>();
            var dv = new List<double>();
            foreach (var (ox, oy, x, y) in data)
            {
                var fx = (ox - _x0) / _dx;
                var fy = (oy - _y0) / _dy;
                if (fx < 0 || fy < 0 || fx > _nx - 1 || fy > _ny - 1) continue;
                var (u, v) = At(ox, oy);
                if (!double.IsFinite(u) || !double.IsFinite(v)) continue;
                int i = Math.Min((int)fx, _nx - 2), j = Math.Min((int)fy, _ny - 2), k = j * _nx + i;
                double ax = fx - i, ay = fy - j;
                taps.Add([(k, (1 - ax) * (1 - ay)), (k + 1, ax * (1 - ay)), (k + _nx, (1 - ax) * ay), (k + _nx + 1, ax * ay)]);
                du.Add(x - u);
                dv.Add(y - v);
            }
            if (taps.Count == 0) return;

            const double smooth = 0.02, decay = 1e-5;
            var (wx, wy) = (smooth * alongX, smooth * alongY);
            void Apply(double[] d, double[] r)
            {
                Parallel.For(0, _ny, j =>
                {
                    for (var i = 0; i < _nx; i++)
                    {
                        var k = j * _nx + i;
                        var acc = decay * d[k];
                        if (i > 0) acc += wx * (d[k] - d[k - 1]);
                        if (i < _nx - 1) acc += wx * (d[k] - d[k + 1]);
                        if (j > 0) acc += wy * (d[k] - d[k - _nx]);
                        if (j < _ny - 1) acc += wy * (d[k] - d[k + _nx]);
                        r[k] = acc;
                    }
                });
                foreach (var tap in taps)
                {
                    var s = 0.0;
                    foreach (var (k, w) in tap) s += w * d[k];
                    foreach (var (k, w) in tap) r[k] += w * s;
                }
            }

            foreach (var (target, values) in new[] { (_u, du), (_v, dv) })
            {
                var b = new double[nodes];
                for (var p = 0; p < taps.Count; p++)
                    foreach (var (k, w) in taps[p]) b[k] += w * values[p];
                var x = ConjugateGradient(Apply, b, 600);
                for (var k = 0; k < nodes; k++)
                    if (double.IsFinite(target[k])) target[k] += x[k];
            }
        }

        /// <summary>
        /// Größtes Rechteck aus Zellen, deren Ecken alle im Bild liegen (dann liegt die ganze Zelle drin) — als
        /// Abbildung mit relativen Bildkoordinaten.
        /// </summary>
        public DewarpMap? ToMap(double aspect)
        {
            const double eps = 1e-9;
            bool Inside(int k) => double.IsFinite(_u[k]) && double.IsFinite(_v[k])
                && _u[k] >= -eps && _u[k] <= 1 + eps && _v[k] >= -eps && _v[k] <= aspect + eps;
            int cw = _nx - 1, ch = _ny - 1;
            var heights = new int[cw];
            var best = (Area: 0L, I0: 0, I1: 0, J0: 0, J1: 0);
            for (var j = 0; j < ch; j++)
            {
                for (var i = 0; i < cw; i++)
                {
                    var k = j * _nx + i;
                    var ok = Inside(k) && Inside(k + 1) && Inside(k + _nx) && Inside(k + _nx + 1);
                    heights[i] = ok ? heights[i] + 1 : 0;
                }
                // größtes Rechteck im Histogramm (Stapel)
                var stack = new Stack<int>();
                for (var i = 0; i <= cw; i++)
                {
                    var hgt = i < cw ? heights[i] : 0;
                    while (stack.Count > 0 && heights[stack.Peek()] >= hgt)
                    {
                        var top = stack.Pop();
                        var left = stack.Count > 0 ? stack.Peek() + 1 : 0;
                        // Fläche in echten Maßen (Zellen können nicht quadratisch sein)
                        var area = (long)Math.Round(heights[top] * _dy * (i - left) * _dx * 1e12);
                        if (heights[top] > 0 && area > best.Area) best = (area, left, i, j - heights[top] + 1, j + 1);
                    }
                    stack.Push(i);
                }
            }
            if (best.Area <= 0) return null;

            int nx = best.I1 - best.I0 + 1, ny = best.J1 - best.J0 + 1;
            var sx = new float[nx * ny];
            var sy = new float[nx * ny];
            for (var j = 0; j < ny; j++)
            for (var i = 0; i < nx; i++)
            {
                var k = (best.J0 + j) * _nx + best.I0 + i;
                sx[j * nx + i] = (float)Math.Clamp(_u[k], 0, 1);
                sy[j * nx + i] = (float)Math.Clamp(_v[k] / aspect, 0, 1);
            }
            return new DewarpMap(nx, ny, sx, sy, (nx - 1) * _dx, (ny - 1) * _dy);
        }
    }

    private static double[] ConjugateGradient(Action<double[], double[]> apply, double[] b, int iterations)
    {
        var n = b.Length;
        var x = new double[n];
        var r = (double[])b.Clone();
        var p = (double[])r.Clone();
        var ap = new double[n];
        var rr = Dot(r, r);
        var stop = 1e-20 * Math.Max(1e-30, rr);
        for (var it = 0; it < iterations && rr > stop; it++)
        {
            apply(p, ap);
            var alpha = rr / Math.Max(1e-300, Dot(p, ap));
            for (var k = 0; k < n; k++) { x[k] += alpha * p[k]; r[k] -= alpha * ap[k]; }
            var next = Dot(r, r);
            var beta = next / rr;
            for (var k = 0; k < n; k++) p[k] = r[k] + beta * p[k];
            rr = next;
        }
        return x;
    }

    // ------------------------------------------------------------ Levenberg-Marquardt

    /// <param name="accepted">Nach jedem angenommenen Schritt (z. B. Startwerte für innere Unbekannte nachführen).</param>
    private static (double[] Theta, double Cost) LevenbergMarquardt(Func<double[], double[]> residuals, double[] theta,
        Action<double[]>? accepted = null)
    {
        var n = theta.Length;
        var r = residuals(theta);
        var cost = Dot(r, r);
        var mu = -1.0;
        for (var iter = 0; iter < 80; iter++)
        {
            // Jacobi-Matrix numerisch (Vorwärtsdifferenzen)
            var m = r.Length;
            var jac = new double[n][];
            Parallel.For(0, n, c =>
            {
                var th = (double[])theta.Clone();
                var h = 1e-6 * Math.Max(1, Math.Abs(th[c]));
                th[c] += h;
                var rc = residuals(th);
                var col = new double[m];
                for (var k = 0; k < m; k++) col[k] = (rc[k] - r[k]) / h;
                jac[c] = col;
            });
            var a = new double[n, n];
            var g = new double[n];
            Parallel.For(0, n, i =>
            {
                for (var j = i; j < n; j++)
                {
                    var s = 0.0;
                    var (ci, cj) = (jac[i], jac[j]);
                    for (var k = 0; k < m; k++) s += ci[k] * cj[k];
                    a[i, j] = a[j, i] = s;
                }
                var gs = 0.0;
                for (var k = 0; k < m; k++) gs += jac[i][k] * r[k];
                g[i] = gs;
            });
            if (mu < 0)
            {
                var maxDiag = 0.0;
                for (var i = 0; i < n; i++) maxDiag = Math.Max(maxDiag, a[i, i]);
                mu = 1e-4 * Math.Max(1e-9, maxDiag);
            }

            var improved = false;
            while (mu < 1e14)
            {
                var sys = new double[n, n];
                for (var i = 0; i < n; i++)
                {
                    for (var j = 0; j < n; j++) sys[i, j] = a[i, j];
                    sys[i, i] += mu * (1 + a[i, i]);
                }
                var delta = SolveCholesky(sys, g.Select(v => -v).ToArray());
                if (delta is null) { mu *= 10; continue; }
                var next = theta.Zip(delta, (t, d) => t + d).ToArray();
                var rn = residuals(next);
                var cn = Dot(rn, rn);
                if (double.IsFinite(cn) && cn < cost)
                {
                    var gain = (cost - cn) / Math.Max(1e-300, cost);
                    (theta, r, cost) = (next, rn, cn);
                    if (accepted is not null)
                    {
                        accepted(theta);
                        r = residuals(theta);   // mit den nachgeführten Startwerten (höchstens besser)
                        cost = Math.Min(cost, Dot(r, r));
                    }
                    mu = Math.Max(1e-12, mu / 3);
                    improved = true;
                    if (gain < 1e-10) return (theta, cost);
                    break;
                }
                mu *= 4;
            }
            if (!improved) break;
        }
        return (theta, cost);
    }

    private static double[]? SolveCholesky(double[,] a, double[] b)
    {
        var n = b.Length;
        var l = new double[n, n];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j <= i; j++)
            {
                var s = a[i, j];
                for (var k = 0; k < j; k++) s -= l[i, k] * l[j, k];
                if (i == j)
                {
                    if (s <= 0) return null;
                    l[i, i] = Math.Sqrt(s);
                }
                else l[i, j] = s / l[j, j];
            }
        }
        var y = new double[n];
        for (var i = 0; i < n; i++)
        {
            var s = b[i];
            for (var k = 0; k < i; k++) s -= l[i, k] * y[k];
            y[i] = s / l[i, i];
        }
        var x = new double[n];
        for (var i = n - 1; i >= 0; i--)
        {
            var s = y[i];
            for (var k = i + 1; k < n; k++) s -= l[k, i] * x[k];
            x[i] = s / l[i, i];
        }
        return x;
    }

    // ------------------------------------------------------------ Kleinkram

    /// <summary>Drehvektor → Drehmatrix (Rodrigues).</summary>
    private static double[,] Rotation(double wx, double wy, double wz)
    {
        var angle = Math.Sqrt(wx * wx + wy * wy + wz * wz);
        var r = new double[3, 3];
        if (angle < 1e-12)
        {
            r[0, 0] = r[1, 1] = r[2, 2] = 1;
            r[0, 1] = -wz; r[0, 2] = wy; r[1, 0] = wz; r[1, 2] = -wx; r[2, 0] = -wy; r[2, 1] = wx;
            return r;
        }
        double kx = wx / angle, ky = wy / angle, kz = wz / angle, c = Math.Cos(angle), s = Math.Sin(angle), v = 1 - c;
        r[0, 0] = c + kx * kx * v; r[0, 1] = kx * ky * v - kz * s; r[0, 2] = kx * kz * v + ky * s;
        r[1, 0] = ky * kx * v + kz * s; r[1, 1] = c + ky * ky * v; r[1, 2] = ky * kz * v - kx * s;
        r[2, 0] = kz * kx * v - ky * s; r[2, 1] = kz * ky * v + kx * s; r[2, 2] = c + kz * kz * v;
        return r;
    }

    /// <summary>Drehmatrix → Drehvektor.</summary>
    private static double[] RotationVector(double[,] r)
    {
        var cos = Math.Clamp((r[0, 0] + r[1, 1] + r[2, 2] - 1) / 2, -1, 1);
        var angle = Math.Acos(cos);
        if (angle < 1e-9) return [0, 0, 0];
        if (Math.PI - angle < 1e-6)
        {
            // 180°: Achse aus der Diagonale
            double x = Math.Sqrt(Math.Max(0, (r[0, 0] + 1) / 2)), y = Math.Sqrt(Math.Max(0, (r[1, 1] + 1) / 2)), z = Math.Sqrt(Math.Max(0, (r[2, 2] + 1) / 2));
            if (r[0, 1] < 0) y = -y;
            if (r[0, 2] < 0) z = -z;
            return [x * angle, y * angle, z * angle];
        }
        var k = angle / (2 * Math.Sin(angle));
        return [(r[2, 1] - r[1, 2]) * k, (r[0, 2] - r[2, 0]) * k, (r[1, 0] - r[0, 1]) * k];
    }

    /// <summary>Vierteldrehungen im Uhrzeigersinn (Bildschirm, y nach unten).</summary>
    private static (double, double) Turn(double a, double b, int turns) => turns switch
    {
        1 => (-b, a),
        2 => (-a, -b),
        3 => (b, -a),
        _ => (a, b),
    };

    private static double Lerp(double[] table, double f)
    {
        var i = Math.Clamp((int)Math.Floor(f), 0, table.Length - 2);
        var t = f - i;
        return table[i] + (table[i + 1] - table[i]) * t;
    }

    private static double Sq(double v) => v * v;
    private static double Dot((double X, double Y) a, (double X, double Y) b) => a.X * b.X + a.Y * b.Y;
    private static double Norm(double[] v) => Math.Sqrt(v.Sum(x => x * x));

    private static double Dot(double[] a, double[] b)
    {
        var s = 0.0;
        for (var i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }
}
