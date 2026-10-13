using ImageMagick;

namespace ImageConverter.Core.Imaging;

/// <summary>Stufen des Ausrichtens — jede lässt sich in der Liste „Bearbeitungen“ ausschalten.</summary>
[Flags]
public enum GeometryStages
{
    None = 0,
    /// <summary>Entzerren: ein Viereck wird zum geraden Rechteck.</summary>
    Straighten = 1,
    /// <summary>Gerade richten: freie Drehung um die Mitte.</summary>
    Level = 2,
    /// <summary>Zuschnitt (mit runden Ecken).</summary>
    Crop = 4,
    /// <summary>Drehen um 90°-Schritte und Spiegeln.</summary>
    Orient = 8,
    /// <summary>Gewölbtes Blatt gerade ziehen (nachgezeichnete Linien) — die erste Stufe.</summary>
    Dewarp = 16,
    All = Dewarp | Straighten | Level | Crop | Orient,
}

/// <summary>Viereck in relativen Koordinaten (0 … 1 der Bildbreite bzw. -höhe): oben links, oben rechts, unten rechts, unten links.</summary>
public readonly record struct RelQuad(double X0, double Y0, double X1, double Y1, double X2, double Y2, double X3, double Y3)
{
    public IReadOnlyList<(double X, double Y)> ToPixels(double width, double height) =>
        [(X0 * width, Y0 * height), (X1 * width, Y1 * height), (X2 * width, Y2 * height), (X3 * width, Y3 * height)];

    public static RelQuad FromPixels(IReadOnlyList<(double X, double Y)> q, double width, double height) =>
        new(q[0].X / width, q[0].Y / height, q[1].X / width, q[1].Y / height,
            q[2].X / width, q[2].Y / height, q[3].X / width, q[3].Y / height);
}

/// <summary>Rechteck in relativen Koordinaten (Anteile von Breite und Höhe).</summary>
public readonly record struct RelRect(double X, double Y, double Width, double Height);

/// <summary>
/// Das Ausrichten eines Bildes als Rezept, nicht in die Pixel eingerechnet: Das Pixelbild (Original, KI, Entfernen)
/// bleibt ungedreht und unbeschnitten; angezeigt und gespeichert wird Pixelbild + Rezept. Feste Reihenfolge:
/// <b>Wölbung → Entzerren → Gerade richten (mit Zuschnitt) → Drehen/Spiegeln</b> — gleich, in welcher Reihenfolge man
/// die Werkzeuge benutzt hat. So lässt sich jede Stufe ausschalten, ohne die anderen zu verlieren, und eine 90°-Drehung
/// hängt an nichts anderem.
/// </summary>
/// <remarks>
/// Koordinaten sind relativ gespeichert: die Linien der Wölbung zum Pixelbild, das Viereck zum Bild nach der Wölbung,
/// der Zuschnitt zum Bild nach dem Entzerren.
/// Damit bleibt eine spätere Stufe ungefähr an ihrer Stelle, wenn eine frühere ausgeschaltet wird, und dasselbe Rezept
/// passt auf eine verkleinerte Fassung (Vorschau der KI).
/// Der Zuschnitt gilt wie bei <see cref="ImageOps.RotateAndCrop"/> im Rahmen der Drehung: achsparallel im gedrehten
/// Bild, Ursprung oben links des ungedrehten Bildes, gedreht wird um die Bildmitte (positiv = im Uhrzeigersinn).
/// Drehen/Spiegeln: erst <see cref="Turns"/> Vierteldrehungen im Uhrzeigersinn, dann links ↔ rechts gespiegelt.
/// </remarks>
/// <param name="CornerRadius">Runde Ecken des Zuschnitts als Anteil der kürzeren Seite des Zuschnitts (0 = eckig).</param>
/// <param name="Dewarp">Gewölbtes Blatt: nachgezeichnete Linien (<see cref="Imaging.Dewarp"/>); null = keine.</param>
public sealed record GeometryRecipe(
    RelQuad? Quad = null,
    double Angle = 0,
    RelRect? Crop = null,
    double CornerRadius = 0,
    int Turns = 0,
    bool Flip = false,
    DewarpSpec? Dewarp = null)
{
    public static readonly GeometryRecipe None = new();

    public bool IsNone => this == None with { Turns = 0 } && Turns % 4 == 0;

    public bool HasDewarp => Dewarp is { IsUsable: true };
    public bool HasStraighten => Quad is not null;
    public bool HasLevel => Math.Abs(Angle) > 1e-9;
    public bool HasCrop => Crop is not null;
    public bool HasOrient => NormalTurns != 0 || Flip;

    /// <summary>Vierteldrehungen 0 … 3.</summary>
    public int NormalTurns => ((Turns % 4) + 4) % 4;

    /// <summary>Welche Stufen etwas tun.</summary>
    public GeometryStages Present =>
        (HasDewarp ? GeometryStages.Dewarp : 0) | (HasStraighten ? GeometryStages.Straighten : 0) | (HasLevel ? GeometryStages.Level : 0)
        | (HasCrop ? GeometryStages.Crop : 0) | (HasOrient ? GeometryStages.Orient : 0);

    // ------------------------------------------------------------ Aktionen (für die Werkzeuge)

    /// <summary>Danach um 90° drehen. Nach dem Spiegeln dreht eine Rechtsdrehung des Bildes intern links herum.</summary>
    public GeometryRecipe Rotated(bool clockwise) =>
        this with { Turns = (NormalTurns + ((clockwise ^ Flip) ? 1 : 3)) % 4 };

    /// <summary>Danach links ↔ rechts spiegeln.</summary>
    public GeometryRecipe Flipped() => this with { Flip = !Flip };

    // ------------------------------------------------------------ Ablauf

    /// <summary>Alle Zwischengrößen und Rahmen für ein Pixelbild der Größe <paramref name="width"/> × <paramref name="height"/>.</summary>
    internal readonly record struct Plan(
        int SourceWidth, int SourceHeight,
        DewarpMap? Map,
        int FlatWidth, int FlatHeight,
        IReadOnlyList<(double X, double Y)>? QuadPixels,
        int StraightWidth, int StraightHeight,
        double Angle,
        (int X, int Y, int W, int H)? Rect,
        double Radius,
        int Turns, bool Flip,
        int Width, int Height);

    internal Plan PlanFor(int width, int height, GeometryStages stages = GeometryStages.All)
    {
        // Wölbung: das Pixelbild gerade gezogen, eigene Größe; alles Weitere gilt für dieses Bild
        var map = (stages & GeometryStages.Dewarp) != 0 && Dewarp is { IsUsable: true } spec ? Imaging.Dewarp.MapFor(spec, width, height) : null;
        var (fw, fh) = map?.SizeFor(width) ?? (width, height);

        IReadOnlyList<(double X, double Y)>? quad = null;
        int sw = fw, sh = fh;
        if ((stages & GeometryStages.Straighten) != 0 && Quad is { } q)
        {
            var px = q.ToPixels(fw, fh);
            if (PerspectiveGeometry.IsValid(px))
            {
                quad = px;
                (sw, sh) = PerspectiveGeometry.TargetSize(px);
            }
        }

        var angle = (stages & GeometryStages.Level) != 0 ? RotationGeometry.Normalize(Angle) : 0;
        var levelled = Math.Abs(angle) > 1e-9;
        (int X, int Y, int W, int H)? rect = null;
        if ((stages & GeometryStages.Crop) != 0 && Crop is { } c)
        {
            rect = Whole(c.X * sw, c.Y * sh, c.Width * sw, c.Height * sh, sw, sh, clamp: !levelled);
        }
        else if (levelled)
        {
            // Gerade gerichtet ohne (sichtbaren) Zuschnitt: größtes Rechteck im gedrehten Bild, sonst leere Ecken
            var (x, y, w, h) = RotationGeometry.LargestInside(sw, sh, angle, (double)sw / sh);
            rect = Whole(Math.Ceiling(x + 1), Math.Ceiling(y + 1), Math.Floor(w - 2), Math.Floor(h - 2), sw, sh, clamp: false);
        }

        int cw = rect?.W ?? sw, ch = rect?.H ?? sh;
        var radius = rect is { } r && (stages & GeometryStages.Crop) != 0 ? CornerRadius * Math.Min(r.W, r.H) : 0;
        var turns = (stages & GeometryStages.Orient) != 0 ? NormalTurns : 0;
        var flip = (stages & GeometryStages.Orient) != 0 && Flip;
        var (ow, oh) = turns % 2 == 1 ? (ch, cw) : (cw, ch);
        return new Plan(width, height, map, fw, fh, quad, sw, sh, angle, rect, radius, turns, flip, ow, oh);
    }

    /// <summary>Größe nach der Wölbung (vor dem Entzerren) für ein Pixelbild dieser Größe.</summary>
    public (int Width, int Height) FlatSize(int width, int height)
    {
        var p = PlanFor(width, height, GeometryStages.Dewarp);
        return (p.FlatWidth, p.FlatHeight);
    }

    /// <summary>Die Abbildung der Wölbung für ein Pixelbild dieser Größe (Seitenverhältnis, DIN …); null = keine.</summary>
    public DewarpMap? DewarpMapFor(int width, int height) => PlanFor(width, height, GeometryStages.Dewarp).Map;

    private static (int, int, int, int) Whole(double x, double y, double w, double h, int sw, int sh, bool clamp)
    {
        int ix = (int)Math.Round(x), iy = (int)Math.Round(y), iw = Math.Max(1, (int)Math.Round(w)), ih = Math.Max(1, (int)Math.Round(h));
        if (!clamp) return (ix, iy, iw, ih);
        // wie ImageOps.Crop: ganz im Bild
        ix = Math.Clamp(ix, 0, sw - 1);
        iy = Math.Clamp(iy, 0, sh - 1);
        return (ix, iy, Math.Clamp(iw, 1, sw - ix), Math.Clamp(ih, 1, sh - iy));
    }

    /// <summary>Größe des Ergebnisses für ein Pixelbild dieser Größe.</summary>
    public (int Width, int Height) SizeFor(int width, int height, GeometryStages stages = GeometryStages.All)
    {
        var p = PlanFor(width, height, stages);
        return (p.Width, p.Height);
    }

    /// <summary>
    /// Das Rezept auf ein Pixelbild anwenden. Liefert ein neues Bild — oder null, wenn keine der Stufen etwas tut
    /// (dann gilt das Pixelbild selbst; der Aufrufer darf es nicht freigeben).
    /// </summary>
    public MagickImage? Apply(MagickImage source, GeometryStages stages = GeometryStages.All)
    {
        int width, height;
        lock (source) (width, height) = ((int)source.Width, (int)source.Height);
        var p = PlanFor(width, height, stages);
        if (p.Map is null && p.QuadPixels is null && p.Rect is null && p.Turns == 0 && !p.Flip) return null;

        MagickImage? work = null;
        MagickImage Current() => work ?? source;
        void Replace(MagickImage next)
        {
            work?.Dispose();
            work = next;
        }

        if (p.Map is { } map) Replace(map.Render(Current()));
        if (p.QuadPixels is { } quad) Replace(ImageOps.Straighten(Current(), quad));
        if (p.Rect is { } r)
        {
            Replace(Math.Abs(p.Angle) > 1e-9
                ? ImageOps.RotateAndCrop(Current(), p.Angle, r.X, r.Y, r.W, r.H, p.Radius)
                : ImageOps.Crop(Current(), r.X, r.Y, r.W, r.H, p.Radius));
        }
        if (p.Turns != 0 || p.Flip)
        {
            var next = ImageOps.CloneOf(Current());
            if (p.Turns != 0) next.Rotate(90 * p.Turns);
            if (p.Flip) next.Flop();
            next.ResetPage();
            Replace(next);
        }
        return work;
    }

    // ------------------------------------------------------------ Umrechnen

    /// <summary>Punkt im Ergebnis → Punkt im Pixelbild (Pipette, Pinsel beim Entfernen).</summary>
    public (double X, double Y) ToSource(double x, double y, int width, int height, GeometryStages stages = GeometryStages.All)
    {
        var p = PlanFor(width, height, stages);
        (x, y) = UnOrient(x, y, p);
        (x, y) = UnLevel(x, y, p);
        if (p.QuadPixels is { } quad)
            (x, y) = PerspectiveGeometry.Apply(PerspectiveGeometry.Homography(Corners(p), quad), x, y);
        if (p.Map is { } map)
        {
            var (rx, ry) = map.Source(x / p.FlatWidth, y / p.FlatHeight);
            (x, y) = (rx * width, ry * height);
        }
        return (x, y);
    }

    /// <summary>Punkt im Pixelbild → Punkt im Ergebnis.</summary>
    public (double X, double Y) ToResult(double x, double y, int width, int height, GeometryStages stages = GeometryStages.All)
    {
        var p = PlanFor(width, height, stages);
        if (p.Map is { } map)
        {
            var (u, v) = map.Result(x / width, y / height);
            (x, y) = (u * p.FlatWidth, v * p.FlatHeight);
        }
        if (p.QuadPixels is { } quad)
            (x, y) = PerspectiveGeometry.Apply(PerspectiveGeometry.Homography(quad, Corners(p)), x, y);
        (x, y) = Level(x, y, p);
        return Orient(x, y, p);
    }

    private static (double X, double Y)[] Corners(Plan p) =>
        [(0, 0), (p.StraightWidth, 0), (p.StraightWidth, p.StraightHeight), (0, p.StraightHeight)];

    /// <summary>Entzerrtes Bild → zugeschnittenes (gedrehtes) Bild.</summary>
    private static (double, double) Level(double x, double y, Plan p)
    {
        if (p.Rect is not { } r) return (x, y);
        if (Math.Abs(p.Angle) > 1e-9)
        {
            double cx = p.StraightWidth / 2.0, cy = p.StraightHeight / 2.0, a = p.Angle * Math.PI / 180;
            double dx = x - cx, dy = y - cy;
            (x, y) = (cx + dx * Math.Cos(a) - dy * Math.Sin(a), cy + dx * Math.Sin(a) + dy * Math.Cos(a));
        }
        return (x - r.X, y - r.Y);
    }

    private static (double, double) UnLevel(double x, double y, Plan p)
    {
        if (p.Rect is not { } r) return (x, y);
        x += r.X;
        y += r.Y;
        if (Math.Abs(p.Angle) > 1e-9)
        {
            double cx = p.StraightWidth / 2.0, cy = p.StraightHeight / 2.0, a = -p.Angle * Math.PI / 180;
            double dx = x - cx, dy = y - cy;
            (x, y) = (cx + dx * Math.Cos(a) - dy * Math.Sin(a), cy + dx * Math.Sin(a) + dy * Math.Cos(a));
        }
        return (x, y);
    }

    /// <summary>Größe vor dem Drehen/Spiegeln (nach Zuschnitt).</summary>
    private static (int W, int H) BeforeOrient(Plan p) => p.Rect is { } r ? (r.W, r.H) : (p.StraightWidth, p.StraightHeight);

    private static (double, double) Orient(double x, double y, Plan p) => OrientPoint(x, y, BeforeOrient(p), p.Turns, p.Flip);

    private static (double, double) UnOrient(double x, double y, Plan p) => UnOrientPoint(x, y, BeforeOrient(p), p.Turns, p.Flip);

    /// <summary>Punkt auf einer Fläche w × h durch Vierteldrehungen (im Uhrzeigersinn) und danach Spiegeln.</summary>
    public static (double X, double Y) OrientPoint(double x, double y, (int W, int H) size, int turns, bool flip)
    {
        var (w, h) = size;
        for (var i = 0; i < ((turns % 4) + 4) % 4; i++)
        {
            (x, y) = (h - y, x);
            (w, h) = (h, w);
        }
        if (flip) x = w - x;
        return (x, y);
    }

    /// <summary>Umkehrung von <see cref="OrientPoint"/>: <paramref name="size"/> ist die Fläche <b>vor</b> dem Drehen.</summary>
    public static (double X, double Y) UnOrientPoint(double x, double y, (int W, int H) size, int turns, bool flip)
    {
        var n = ((turns % 4) + 4) % 4;
        var (w, h) = n % 2 == 1 ? (size.H, size.W) : size;   // Fläche nach dem Drehen
        if (flip) x = w - x;
        for (var i = 0; i < n; i++)
        {
            // Umkehrung von (x, y) → (h − y, x) auf einer Fläche, die vorher h × w war
            (x, y) = (y, w - x);
            (w, h) = (h, w);
        }
        return (x, y);
    }

    /// <summary>Rechteck durch Drehen/Spiegeln (achsparallel bleibt achsparallel).</summary>
    public static (double X, double Y, double W, double H) OrientRect(double x, double y, double w, double h, (int W, int H) size, int turns, bool flip)
    {
        var (ax, ay) = OrientPoint(x, y, size, turns, flip);
        var (bx, by) = OrientPoint(x + w, y + h, size, turns, flip);
        return (Math.Min(ax, bx), Math.Min(ay, by), Math.Abs(bx - ax), Math.Abs(by - ay));
    }

    /// <summary>Umkehrung von <see cref="OrientRect"/>; <paramref name="size"/> ist die Fläche vor dem Drehen.</summary>
    public static (double X, double Y, double W, double H) UnOrientRect(double x, double y, double w, double h, (int W, int H) size, int turns, bool flip)
    {
        var (ax, ay) = UnOrientPoint(x, y, size, turns, flip);
        var (bx, by) = UnOrientPoint(x + w, y + h, size, turns, flip);
        return (Math.Min(ax, bx), Math.Min(ay, by), Math.Abs(bx - ax), Math.Abs(by - ay));
    }

    // ------------------------------------------------------------ Werkzeuge

    /// <summary>
    /// Zuschneiden im angezeigten Ergebnis: <paramref name="x"/> … <paramref name="h"/> in Pixeln des Ergebnisses (mit
    /// den angezeigten Stufen). Ist der Zuschnitt sichtbar, wird enger geschnitten; ist er ausgeschaltet (oder gibt es
    /// keinen), ersetzt der neue ihn. Die Drehung bleibt.
    /// </summary>
    /// <param name="radius">Runde Ecken in Pixeln.</param>
    public GeometryRecipe CroppedInResult(double x, double y, double w, double h, double radius, int width, int height,
        GeometryStages shown)
    {
        var p = PlanFor(width, height, shown);
        // zurück vor das Drehen/Spiegeln: in die Fläche nach dem Zuschnitt
        var (rx, ry, rw, rh) = UnOrientRect(x, y, w, h, BeforeOrient(p), p.Turns, p.Flip);
        double fx, fy;
        if (p.Rect is { } old)
            (fx, fy) = (old.X + rx, old.Y + ry);   // im Rahmen der Drehung: einfach verschieben
        else
            (fx, fy) = (rx, ry);
        var crop = new RelRect(fx / p.StraightWidth, fy / p.StraightHeight, rw / p.StraightWidth, rh / p.StraightHeight);
        // Bei ausgeschalteter Drehung gilt der neue Zuschnitt ohne sie — dann gehört er auch ohne Drehung ins Rezept
        var angle = (shown & GeometryStages.Level) != 0 ? Angle : 0;
        return this with { Crop = crop, Angle = angle, CornerRadius = radius / Math.Max(1, Math.Min(rw, rh)) };
    }

    /// <summary>
    /// Ansicht für „Frei drehen“: Pixelbild entzerrt und gedreht/gespiegelt, aber ohne freie Drehung und Zuschnitt.
    /// Winkel und Rahmen darin für die Anzeige (Rahmen in Pixeln dieser Ansicht, wie <see cref="ImageOps.RotateAndCrop"/>).
    /// </summary>
    public const GeometryStages LevelView = GeometryStages.Dewarp | GeometryStages.Straighten | GeometryStages.Orient;

    /// <summary>Winkel und Rahmen des Rezepts in der Ansicht <see cref="LevelView"/>; Rahmen null = keiner.</summary>
    public (double Angle, (double X, double Y, double W, double H)? Frame) LevelInView(int width, int height)
    {
        var p = PlanFor(width, height);
        var angle = Flip ? -p.Angle : p.Angle;
        if (Crop is not { } c) return (angle, null);
        var size = (p.StraightWidth, p.StraightHeight);
        var frame = OrientRect(c.X * p.StraightWidth, c.Y * p.StraightHeight, c.Width * p.StraightWidth, c.Height * p.StraightHeight,
            size, p.Turns, p.Flip);
        return (angle, frame);
    }

    /// <summary>
    /// Winkel und Rahmen aus der Ansicht <see cref="LevelView"/> übernehmen (freie Drehung und Zuschnitt werden ersetzt).
    /// Drehungen um die Mitte vertauschen sich mit Vierteldrehungen, beim Spiegeln kehrt sich die Richtung um.
    /// </summary>
    public GeometryRecipe LevelledInView(double angle, double x, double y, double w, double h, double radius, int width, int height)
    {
        var p = PlanFor(width, height, GeometryStages.Dewarp | GeometryStages.Straighten);
        var size = (p.StraightWidth, p.StraightHeight);
        var (rx, ry, rw, rh) = UnOrientRect(x, y, w, h, size, NormalTurns, Flip);
        return this with
        {
            Angle = RotationGeometry.Normalize(Flip ? -angle : angle),
            Crop = new RelRect(rx / p.StraightWidth, ry / p.StraightHeight, rw / p.StraightWidth, rh / p.StraightHeight),
            CornerRadius = radius / Math.Max(1, Math.Min(rw, rh)),
        };
    }

    /// <summary>Ansicht für „Entzerren“: gerade gezogen (Wölbung) und gedreht/gespiegelt — das Viereck liegt auf dem Bild nach der Wölbung.</summary>
    public const GeometryStages StraightenView = GeometryStages.Dewarp | GeometryStages.Orient;

    /// <summary>
    /// Das Viereck des Rezepts in der Ansicht <see cref="StraightenView"/> (Pixel); null = keins.
    /// <paramref name="width"/> × <paramref name="height"/> ist das Pixelbild; ist die Wölbung in <paramref name="stages"/>
    /// eingeschaltet, liegt das Viereck auf dem Bild danach.
    /// </summary>
    public IReadOnlyList<(double X, double Y)>? QuadInView(int width, int height, GeometryStages stages = GeometryStages.All)
    {
        if (Quad is not { } q) return null;
        var size = (stages & GeometryStages.Dewarp) != 0 ? FlatSize(width, height) : (width, height);
        return q.ToPixels(size.Item1, size.Item2).Select(pt => OrientPoint(pt.X, pt.Y, size, NormalTurns, Flip)).ToArray();
    }

    /// <summary>Viereck aus der Ansicht <see cref="StraightenView"/> übernehmen (ersetzt das bisherige).</summary>
    public GeometryRecipe StraightenedInView(IReadOnlyList<(double X, double Y)> quadInView, int width, int height,
        GeometryStages stages = GeometryStages.All)
    {
        var size = (stages & GeometryStages.Dewarp) != 0 ? FlatSize(width, height) : (width, height);
        var src = quadInView.Select(pt => UnOrientPoint(pt.X, pt.Y, size, NormalTurns, Flip)).ToArray();
        // Nach dem Spiegeln ist die Reihenfolge der Ecken links ↔ rechts vertauscht; ungerade Drehungen verschieben sie
        var ordered = Reorder(src);
        return this with { Quad = RelQuad.FromPixels(ordered, size.Item1, size.Item2) };
    }

    /// <summary>Ansicht für „Gewölbtes Blatt“: das Pixelbild, nur gedreht/gespiegelt — die Linien liegen auf dem Original.</summary>
    public const GeometryStages DewarpView = GeometryStages.Orient;

    /// <summary>Die Linien der Wölbung in der Ansicht <see cref="DewarpView"/> (Pixel); leer = keine.</summary>
    public IReadOnlyList<(WarpLineKind Kind, IReadOnlyList<(double X, double Y)> Points)> WarpLinesInView(int width, int height) =>
        Dewarp is not { } spec
            ? []
            : spec.Lines.Select(l => (l.Kind, (IReadOnlyList<(double X, double Y)>)l.Points
                .Select(p => OrientPoint(p.X * width, p.Y * height, (width, height), NormalTurns, Flip)).ToArray())).ToArray();

    /// <summary>
    /// Linien aus der Ansicht <see cref="DewarpView"/> übernehmen (ersetzen die bisherigen; ohne brauchbare Linie: keine
    /// Wölbung). <paramref name="focal35"/>: Brennweite der Aufnahme (Kleinbild), aus den EXIF-Daten.
    /// </summary>
    public GeometryRecipe DewarpedInView(IEnumerable<(WarpLineKind Kind, IReadOnlyList<(double X, double Y)> Points)> lines,
        int width, int height, double focal35)
    {
        var spec = new DewarpSpec(lines
            .Where(l => l.Points.Count >= 2)
            .Select(l => new WarpLine(l.Kind, l.Points
                .Select(p => UnOrientPoint(p.X, p.Y, (width, height), NormalTurns, Flip))
                .Select(p => (p.X / width, p.Y / height)).ToArray()))
            .ToArray(), focal35);
        return this with { Dewarp = spec.IsUsable ? spec : null };
    }

    /// <summary>
    /// Gitter einer KI-Begradigung aus der Ansicht <see cref="DewarpView"/> übernehmen (ersetzt die Linien). Das Gitter gilt
    /// für die gedrehte/gespiegelte Ansicht, die Stufe rechnet aber am Pixelbild: Knoten und Ausdehnung werden zurückgedreht.
    /// </summary>
    public GeometryRecipe AiDewarpedInView(DewarpGrid viewGrid, int width, int height, double focal35)
    {
        var odd = (((NormalTurns % 4) + 4) % 4) % 2 == 1;
        var (viewW, viewH) = odd ? (height, width) : (width, height);
        var view = new DewarpMap(viewGrid.Nx, viewGrid.Ny, viewGrid.Sx, viewGrid.Sy, viewGrid.RelWidth, viewGrid.RelHeight);
        double wv = viewGrid.RelWidth * viewW, hv = viewGrid.RelHeight * viewW;   // Ergebnis in der Ansicht, Pixel
        var (ws, hs) = odd ? (hv, wv) : (wv, hv);                                  // Ergebnis vor dem Drehen
        var (nx, ny) = odd ? (viewGrid.Ny, viewGrid.Nx) : (viewGrid.Nx, viewGrid.Ny);
        var sx = new float[nx * ny];
        var sy = new float[nx * ny];
        for (var b = 0; b < ny; b++)
        for (var a = 0; a < nx; a++)
        {
            // Stelle im Ergebnis vor dem Drehen → in der gedrehten Ansicht (Einheitsquadrat) → im Ansichtsbild → im Pixelbild
            var (u, v) = OrientPoint(a / (double)(nx - 1), b / (double)(ny - 1), (1, 1), NormalTurns, Flip);
            var (rx, ry) = view.Source(u, v);
            var (px, py) = UnOrientPoint(rx * viewW, ry * viewH, (width, height), NormalTurns, Flip);
            (sx[b * nx + a], sy[b * nx + a]) = ((float)(px / width), (float)(py / height));
        }
        var aspect = viewGrid.PageAspect is { } pa ? (odd ? 1 / pa : pa) : (double?)null;
        // Schattenkarte liegt über dem Ergebnis wie das Gitter: genauso umordnen
        DewarpShading? shading = null;
        if (viewGrid.Shading is { } vs)
        {
            var (tw, th) = odd ? (vs.Height, vs.Width) : (vs.Width, vs.Height);
            var rgb = new float[tw * th * 3];
            for (var b = 0; b < th; b++)
            for (var a = 0; a < tw; a++)
            {
                var (u, v) = OrientPoint(a / (double)Math.Max(1, tw - 1), b / (double)Math.Max(1, th - 1), (1, 1), NormalTurns, Flip);
                var k = (b * tw + a) * 3;
                (rgb[k], rgb[k + 1], rgb[k + 2]) = vs.Sample(u, v);
            }
            shading = vs with { Width = tw, Height = th, Rgb = rgb };
        }
        var grid = new DewarpGrid(nx, ny, sx, sy, ws / width, hs / width, aspect, viewGrid.Din, shading);
        return this with { Dewarp = new DewarpSpec([], focal35, Grid: grid) };
    }

    /// <summary>Ecken neu ordnen: oben links, oben rechts, unten rechts, unten links (nach Winkel um den Schwerpunkt).</summary>
    private static (double X, double Y)[] Reorder((double X, double Y)[] q)
    {
        double cx = q.Average(p => p.X), cy = q.Average(p => p.Y);
        var sorted = q.OrderBy(p => Math.Atan2(p.Y - cy, p.X - cx)).ToArray();   // von links über oben im Uhrzeigersinn (y nach unten)
        // Startpunkt: die Ecke mit kleinstem x + y (oben links)
        var start = Array.IndexOf(sorted, sorted.OrderBy(p => p.X + p.Y).First());
        return Enumerable.Range(0, 4).Select(i => sorted[(start + i) % 4]).ToArray();
    }
}
