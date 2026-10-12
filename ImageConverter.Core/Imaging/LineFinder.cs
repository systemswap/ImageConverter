namespace ImageConverter.Core.Imaging;

/// <summary>Woher ein Vorschlag stammt.</summary>
public enum FoundLineSource { Text, Edge, Fold, Ruled, Margin }

/// <summary>Vorgeschlagene Linie in Pixeln des untersuchten Bildes.</summary>
public sealed record FoundLine(WarpLineKind Kind, FoundLineSource Source, IReadOnlyList<(double X, double Y)> Points);

/// <summary>
/// Linien für „Gewölbtes Blatt“ vorschlagen, ohne Modell: Textzeilen (Grundlinie), Blattrand und Falz.
/// </summary>
/// <remarks>
/// <para><b>Textzeilen</b> (wie page_dewarp, Zucker 2016): dunkle Stellen gegen den örtlichen Hintergrund, zusammenhängende
/// Flecken als Buchstaben (zu große und zu kleine fallen weg), Schreibrichtung aus den Richtungen zum nächsten
/// Nachbarbuchstaben (Häufigkeit), Buchstaben von links nach rechts verkettet — die Richtung darf sich dabei langsam
/// drehen, so folgt die Kette einer gebogenen Zeile. Punkte auf der Grundlinie (Unterkante, Unterlängen wie g, p, y
/// fallen als Ausreißer weg), bis zu acht Zeilen gleichmäßig über das Blatt.</para>
/// <para><b>Blattrand:</b> helles Blatt gegen dunkleren Untergrund (Otsu), Löcher (Schrift) gefüllt, Umriss verfolgt
/// und an den vier Ecken in Kanten geteilt, jeder Punkt auf den stärksten Helligkeitssprung quer zur Kante geschoben.
/// Kanten, die am Fotorand liegen, sind nicht das Blatt und fallen weg.</para>
/// <para><b>Falz:</b> schmales dunkles Tal im mittleren Drittel des Blatts, in mehreren Höhen an fast derselben Stelle; Ober- und
/// Unterkante werden dort geteilt (sie knicken).</para>
/// <para><b>Gezeichnete Linien</b> (Rahmen, Tabellen, Raster, Schreiblinien), waagerecht und senkrecht: dünne dunkle Querstücke
/// von Spalte zu Spalte verfolgt. <b>Textränder:</b> übereinanderstehende Zeilenanfänge (bzw. -enden) als senkrechte Linie.</para>
/// <para>Gefunden wird verkleinert; <see cref="Refine"/> schärft Kanten, Falz und gezeichnete Linien im Original nach. Welche
/// Vorschläge nicht zu den übrigen passen, entscheidet danach das Blattmodell (<see cref="Dewarp.RejectOutliers"/>).</para>
/// </remarks>
public static class LineFinder
{
    /// <summary>So groß wird untersucht (längere Seite) — größer bringt kaum mehr, kostet aber.</summary>
    public const int WorkEdge = 1600;

    /// <summary>Linien im Bild (B, G, R, A, 0 … 1). Punkte in Pixeln dieses Bildes.</summary>
    public static IReadOnlyList<FoundLine> Find(float[] bgra, int w, int h)
    {
        var gray = new float[w * h];
        Parallel.For(0, h, y =>
        {
            for (int x = 0, i = y * w; x < w; x++, i++)
                gray[i] = 0.114f * bgra[i * 4] + 0.587f * bgra[i * 4 + 1] + 0.299f * bgra[i * 4 + 2];
        });

        var candidates = TextLines(gray, w, h, out var direction, out var ink, out var letter);
        var page = PageMask(gray, w, h);
        var fold = page is null ? null : Fold(gray, page, w, h, direction);
        var edges = page is null ? [] : PageEdges(gray, page, w, h, direction, fold);

        // Textzeilen: je Seite gleichmäßig verteilt (Doppelseite: links und rechts vom Falz getrennt)
        var d = direction ?? (1, 0);
        var result = new List<FoundLine>();
        if (fold is null) result.AddRange(ChooseText(candidates, 8));
        else
        {
            var foldAlong = fold.Points.Average(p => p.X * d.X + p.Y * d.Y);
            result.AddRange(ChooseText(candidates.Where(c => c.AlongMid < foldAlong).ToList(), 6));
            result.AddRange(ChooseText(candidates.Where(c => c.AlongMid >= foldAlong).ToList(), 6));
        }
        result.AddRange(edges);
        // senkrechte (und weitere waagerechte) Linien: Textränder und gezeichnete Linien
        result.AddRange(Margins(candidates, d, letter));
        var textSpan = candidates.Count > 0 ? candidates.Max(c => c.Extent) : Math.Max(w, h) / 2.0;
        result.AddRange(RuledLines(ink, w, h, d, letter, textSpan));
        if (fold is not null) result.Add(fold);
        return result;
    }

    // ------------------------------------------------------------ Textzeilen

    private sealed class Blob
    {
        public int Start, Count;            // Pixel in der gemeinsamen Liste
        public int MinX, MaxX, MinY, MaxY;
        public double Cx, Cy;
        public double Along, Base;          // Lage entlang der Schrift, Grundlinie (Unterkante quer zur Schrift)
        public bool Used;
        public int Height => MaxY - MinY + 1;
        public int Width => MaxX - MinX + 1;
    }

    /// <summary>Gefundene Textzeile vor der Auswahl: Punkte, Länge, Lage quer (Grundlinie) und Mitte entlang der Schrift.</summary>
    /// Start/End: Anfang und Ende der Zeile entlang der Schrift (äußerster Buchstabe) und die Grundlinie dort — für die Textränder.
    private sealed record TextCandidate(IReadOnlyList<(double X, double Y)> Points, double Extent, double Base, double AlongMid,
        (double Along, double Base) Start = default, (double Along, double Base) End = default);

    /// <summary>Bis zu <paramref name="max"/> Zeilen gleichmäßig über das Blatt (die äußersten zuerst); kurze nur, wenn nichts Besseres.</summary>
    private static IEnumerable<FoundLine> ChooseText(List<TextCandidate> candidates, int max)
    {
        if (candidates.Count == 0) return [];
        var longest = candidates.Max(c => c.Extent);
        var good = candidates.Where(c => c.Extent >= 0.5 * longest).OrderBy(c => c.Base).ToList();
        var chosen = good.Count <= max ? good : Enumerable.Range(0, max).Select(k => good[(int)Math.Round(k * (good.Count - 1) / (max - 1.0))]).ToList();
        return chosen.Select(c => new FoundLine(WarpLineKind.Horizontal, FoundLineSource.Text, c.Points));
    }

    private static List<TextCandidate> TextLines(float[] gray, int w, int h, out (double X, double Y)? direction, out bool[] ink, out double letter)
    {
        direction = null;
        var maxDim = Math.Max(w, h);

        // Dunkel gegenüber dem örtlichen Hintergrund
        var bg = (float[])gray.Clone();
        Blur.Gauss(bg, w, h, Math.Max(6, maxDim / 80.0));
        var dark = ink = new bool[w * h];
        letter = Math.Max(6, maxDim / 100.0);   // ohne Text: grob geschätzt
        Parallel.For(0, w * h, i => dark[i] = bg[i] - gray[i] > Math.Max(0.07f, 0.18f * bg[i]));

        var (blobs, pixels) = Components(dark, w, h);
        var sizes = blobs.Where(b => b.Count >= 6 && b.Height <= maxDim / 20 && b.Width <= maxDim / 20).Select(b => b.Height).OrderBy(v => v).ToList();
        if (sizes.Count < 30) return [];
        double hm = letter = sizes[sizes.Count / 2];
        var chars = blobs.Where(b => b.Count >= 6 && b.Height >= 0.4 * hm && b.Height <= 3 * hm && b.Width <= 6 * hm).ToList();
        if (chars.Count < 30) return [];

        // Schreibrichtung: Richtung zum nächsten Nachbarn, Häufigkeit über 180°
        var grid = new SpatialGrid(chars, 3 * hm);
        var hist = new double[180];
        foreach (var c in chars)
        {
            Blob? best = null;
            var bestD = 2.5 * hm;
            foreach (var o in grid.Near(c.Cx, c.Cy))
            {
                if (ReferenceEquals(o, c)) continue;
                var d = Math.Sqrt(Sq(o.Cx - c.Cx) + Sq(o.Cy - c.Cy));
                if (d < bestD) (bestD, best) = (d, o);
            }
            if (best is null) continue;
            var angle = Math.Atan2(best.Cy - c.Cy, best.Cx - c.Cx) * 180 / Math.PI;
            var bin = ((int)Math.Round(angle) % 180 + 180) % 180;
            hist[bin] += 1;
        }
        var smooth = new double[180];
        for (var a = 0; a < 180; a++)
            for (var k = -4; k <= 4; k++) smooth[a] += hist[((a + k) % 180 + 180) % 180] * (5 - Math.Abs(k));
        var peak = Array.IndexOf(smooth, smooth.Max());
        var theta = (peak > 90 ? peak - 180 : peak) * Math.PI / 180;   // −90 … 90°
        (double X, double Y) d0 = (Math.Cos(theta), Math.Sin(theta));
        (double X, double Y) n0 = (-d0.Y, d0.X);
        direction = d0;

        // Lage entlang der Schrift und Unterkante quer dazu (aus den Pixeln)
        foreach (var c in chars)
        {
            c.Along = c.Cx * d0.X + c.Cy * d0.Y;
            var maxN = double.MinValue;
            for (var k = c.Start; k < c.Start + c.Count; k++)
            {
                var p = pixels[k];
                double px = p % w + 0.5, py = p / w + 0.5;
                maxN = Math.Max(maxN, px * n0.X + py * n0.Y + 0.5);
            }
            c.Base = maxN;
        }

        // Ketten: vom vordersten freien Buchstaben aus immer zum nächsten in Schreibrichtung (Richtung darf sich drehen)
        var chains = new List<List<Blob>>();
        foreach (var start in chars.OrderBy(c => c.Along))
        {
            if (start.Used) continue;
            start.Used = true;
            var chain = new List<Blob> { start };
            var dir = d0;
            var cur = start;
            var heights = new List<int> { start.Height };
            while (true)
            {
                // Toleranzen in der Schriftgröße dieser Zeile (kleine Fußzeilen springen sonst in die Nachbarzeile)
                var hc = Math.Clamp(heights.OrderBy(v => v).ElementAt(heights.Count / 2), 0.5 * hm, 2 * hm);
                Blob? next = null;
                var bestCost = double.MaxValue;
                foreach (var o in grid.Near(cur.Cx, cur.Cy, 2))
                {
                    if (o.Used) continue;
                    double vx = o.Cx - cur.Cx, vy = o.Cy - cur.Cy;
                    var along = vx * dir.X + vy * dir.Y;
                    var perp = Math.Abs(-vx * dir.Y + vy * dir.X);
                    if (along < 0.15 * hc || along > 3.2 * hc || perp > 0.3 * hc + 0.1 * along) continue;
                    if (o.Height < 0.45 * hc || o.Height > 2.2 * hc) continue;
                    var cost = along + 3 * perp;
                    if (cost < bestCost) (bestCost, next) = (cost, o);
                }
                if (next is null) break;
                next.Used = true;
                double sx = next.Cx - cur.Cx, sy = next.Cy - cur.Cy, len = Math.Sqrt(sx * sx + sy * sy);
                if (len > 1e-9)
                {
                    var nx = 0.75 * dir.X + 0.25 * sx / len;
                    var ny = 0.75 * dir.Y + 0.25 * sy / len;
                    var nl = Math.Sqrt(nx * nx + ny * ny);
                    dir = (nx / nl, ny / nl);
                }
                chain.Add(next);
                heights.Add(next.Height);
                cur = next;
            }
            if (chain.Count >= 6) chains.Add(chain);
        }
        if (chains.Count == 0) return [];

        // Ketten, die durch eine größere Lücke getrennt wurden (Spalten, Tabulator), wieder zusammensetzen
        chains = chains.OrderBy(c => c[0].Along).ToList();
        for (var i = 0; i < chains.Count; i++)
        {
            for (var j = 0; j < chains.Count; j++)
            {
                if (i == j || chains[i].Count == 0 || chains[j].Count == 0) continue;
                var (a, b) = (chains[i][^1], chains[j][0]);
                var gap = b.Along - a.Along;
                if (gap <= 0 || gap > 8 * hm) continue;
                if (Math.Abs(MedianBase(chains[i], fromEnd: true) - MedianBase(chains[j], fromEnd: false)) > 0.5 * Math.Min(LetterHeight(chains[i]), LetterHeight(chains[j]))) continue;
                chains[i].AddRange(chains[j]);
                chains[j].Clear();
                j = -1;   // weiter anhängen, was jetzt passt
            }
        }
        chains = chains.Where(c => c.Count >= 6).ToList();

        var allAlong = chars.Select(c => c.Along).ToArray();
        var span = allAlong.Max() - allAlong.Min();
        return chains
            .Select(c => (Chain: c, Extent: c[^1].Along - c[0].Along))
            .Where(c => c.Extent >= 0.2 * span)
            .Select(c =>
            {
                var pts = Baseline(c.Chain, d0, n0, LetterHeight(c.Chain));
                double Half(Blob b) => (b.Width * Math.Abs(d0.X) + b.Height * Math.Abs(d0.Y)) / 2;
                double BaseOf((double X, double Y) p) => p.X * n0.X + p.Y * n0.Y;
                var (first, last) = (c.Chain[0], c.Chain[^1]);
                return new TextCandidate(pts, c.Extent, c.Chain.Average(b => b.Base), (first.Along + last.Along) / 2,
                    (first.Along - Half(first), BaseOf(pts[0])), (last.Along + Half(last), BaseOf(pts[^1])));
            })
            .Where(c => c.Points.Count >= 3)
            .ToList();
    }

    // ------------------------------------------------------------ Nachschärfen in voller Auflösung

    /// <summary>
    /// Vorschläge im Original nachschärfen (gefunden wurde auf einer verkleinerten Fassung): Punkte von Blattkanten und Falz auf
    /// den stärksten Helligkeitssprung bzw. das dunkelste Tal quer zur Linie, gezeichnete Linien auf ihre Mitte — mit
    /// Unterpixel, danach wieder geglättet. Textzeilen bleiben (ihre Grundlinie ist schon über viele Buchstaben gemittelt).
    /// </summary>
    /// <param name="gray">Helligkeit des Originals (0 … 1), <paramref name="w"/> × <paramref name="h"/>.</param>
    /// <param name="workScale">Wie stark für die Suche verkleinert wurde (&lt; 1) — so weit kann ein Punkt danebenliegen.</param>
    public static IReadOnlyList<FoundLine> Refine(IReadOnlyList<FoundLine> lines, float[] gray, int w, int h, double workScale)
    {
        var reach = (int)Math.Ceiling(2 / Math.Max(0.05, Math.Min(1, workScale))) + 1;
        return lines.Select(line =>
        {
            if (line.Source is FoundLineSource.Text or FoundLineSource.Margin || line.Points.Count < 3) return line;
            var dark = line.Source is FoundLineSource.Ruled or FoundLineSource.Fold;
            var pts = line.Points;
            var refined = new (double X, double Y)[pts.Count];
            for (var i = 0; i < pts.Count; i++)
            {
                var (a, b) = (pts[Math.Max(0, i - 1)], pts[Math.Min(pts.Count - 1, i + 1)]);
                double tx = b.X - a.X, ty = b.Y - a.Y, tl = Math.Sqrt(tx * tx + ty * ty);
                refined[i] = tl < 1e-9 ? pts[i] : Snap(gray, w, h, pts[i], (-ty / tl, tx / tl), reach, dark);
            }
            return line with { Points = SmoothCurve(refined, 1) };
        }).ToList();
    }

    /// <summary>Punkt quer zur Linie (± <paramref name="reach"/> px) auf den stärksten Sprung oder, mit <paramref name="dark"/>, das dunkelste Tal.</summary>
    private static (double X, double Y) Snap(float[] g, int w, int h, (double X, double Y) p, (double X, double Y) normal, int reach, bool dark)
    {
        double Sample(double x, double y)
        {
            // Punkte meinen Flächen (Pixel i reicht von i bis i + 1), der Wert eines Pixels gilt in seiner Mitte
            x = Math.Clamp(x - 0.5, 0, w - 1.001); y = Math.Clamp(y - 0.5, 0, h - 1.001);
            int ix = (int)x, iy = (int)y;
            double fx = x - ix, fy = y - iy;
            var i = iy * w + ix;
            return (g[i] * (1 - fx) + g[i + 1] * fx) * (1 - fy) + (g[i + w] * (1 - fx) + g[i + w + 1] * fx) * fy;
        }
        // entlang der Linie ein wenig mitteln (± 1 px), damit Rauschen und JPEG nicht stören
        double Profile(double k)
        {
            double x = p.X + k * normal.X, y = p.Y + k * normal.Y, s = 0;
            for (var t = -1; t <= 1; t++) s += Sample(x - t * normal.Y, y + t * normal.X);
            return s / 3;
        }
        var score = new double[2 * reach + 1];
        for (var k = -reach; k <= reach; k++)
            score[k + reach] = dark ? -Profile(k) : Math.Abs(Profile(k + 0.5) - Profile(k - 0.5));
        var best = Array.IndexOf(score, score.Max());
        double offset = best - reach;
        if (best > 0 && best < score.Length - 1)
        {
            var den = score[best - 1] - 2 * score[best] + score[best + 1];
            if (Math.Abs(den) > 1e-12) offset += Math.Clamp(0.5 * (score[best - 1] - score[best + 1]) / den, -0.5, 0.5);
        }
        return (p.X + offset * normal.X, p.Y + offset * normal.Y);
    }

    // ------------------------------------------------------------ Textränder

    /// <summary>
    /// Textränder als senkrechte Linien: Zeilenanfänge (bzw. -enden bei Blocksatz), die übereinander stehen. Robust: die
    /// größte Gruppe von Zeilen, deren Anfänge auf einer Geraden quer zur Schrift liegen (Einrückungen, Aufzählungen fallen
    /// heraus) — mindestens 5 Zeilen über ein Viertel der Texthöhe.
    /// </summary>
    private static List<FoundLine> Margins(List<TextCandidate> lines, (double X, double Y) d, double hm)
    {
        (double X, double Y) n = (-d.Y, d.X);
        var result = new List<FoundLine>();
        if (lines.Count < 5) return result;
        var height = lines.Max(l => l.Base) - lines.Min(l => l.Base);
        foreach (var useEnd in new[] { false, true })
        {
            var ends = lines.Select(l => useEnd ? l.End : l.Start).ToList();
            List<(double Along, double Base)>? best = null;
            // Gerade durch je zwei Zeilenenden; die mit den meisten Zeilen in ±0,6 Buchstabenhöhen
            for (var i = 0; i < ends.Count; i++)
            for (var j = i + 1; j < ends.Count; j++)
            {
                var db = ends[j].Base - ends[i].Base;
                if (Math.Abs(db) < 2 * hm) continue;
                var slope = (ends[j].Along - ends[i].Along) / db;
                if (Math.Abs(slope) > 0.35) continue;
                var inliers = ends.Where(e => Math.Abs(ends[i].Along + slope * (e.Base - ends[i].Base) - e.Along) < 0.6 * hm).ToList();
                if (best is null || inliers.Count > best.Count) best = inliers;
            }
            if (best is null || best.Count < 5) continue;
            if (best.Max(e => e.Base) - best.Min(e => e.Base) < 0.25 * height) continue;
            var points = best.OrderBy(e => e.Base).Select(e => (e.Along * d.X + e.Base * n.X, e.Along * d.Y + e.Base * n.Y)).ToArray();
            result.Add(new FoundLine(WarpLineKind.Vertical, FoundLineSource.Margin, SmoothCurve(points, 0.4 * hm)));
        }
        return result;
    }

    // ------------------------------------------------------------ Gezeichnete Linien

    /// <summary>
    /// Gezeichnete Linien (Rahmen, Tabellen, Raster, Schreiblinien): im Rahmen der Schrift Spalte für Spalte die dünnen
    /// dunklen Querstücke suchen und von Spalte zu Spalte verfolgen — so folgt die Spur auch einer gebogenen Linie. Buchstaben
    /// geben nur kurze Spuren. Senkrechte Linien genauso Zeile für Zeile.
    /// </summary>
    private static List<FoundLine> RuledLines(bool[] ink, int w, int h, (double X, double Y) d, double hm, double textSpan)
    {
        (double X, double Y) n = (-d.Y, d.X);
        // Raster im Rahmen der Schrift: a entlang, b quer (gedreht, nächster Nachbar)
        var corners = new[] { (0.0, 0.0), (w, 0.0), (w, (double)h), (0.0, (double)h) };
        double a0 = corners.Min(c => c.Item1 * d.X + c.Item2 * d.Y), a1 = corners.Max(c => c.Item1 * d.X + c.Item2 * d.Y);
        double b0 = corners.Min(c => c.Item1 * n.X + c.Item2 * n.Y), b1 = corners.Max(c => c.Item1 * n.X + c.Item2 * n.Y);
        int rw = (int)Math.Ceiling(a1 - a0) + 1, rh = (int)Math.Ceiling(b1 - b0) + 1;
        var grid = new bool[rw * rh];
        Parallel.For(0, rh, j =>
        {
            for (var i = 0; i < rw; i++)
            {
                double a = a0 + i, b = b0 + j;
                int x = (int)Math.Round(a * d.X + b * n.X), y = (int)Math.Round(a * d.Y + b * n.Y);
                if (x >= 0 && y >= 0 && x < w && y < h) grid[j * rw + i] = ink[y * w + x];
            }
        });

        var thin = Math.Max(3, 0.3 * hm);
        var minLength = Math.Max(4 * hm, 0.15 * textSpan);
        var result = new List<FoundLine>();
        foreach (var vertical in new[] { false, true })
        {
            // „Spalten“ entlang der Linie, „Zeilen“ quer dazu
            int along = vertical ? rh : rw, across = vertical ? rw : rh;
            bool At(int s, int t) => vertical ? grid[s * rw + t] : grid[t * rw + s];
            var tracks = new List<List<(double S, double T)>>();
            var open = new List<(List<(double S, double T)> Track, int Last)>();
            for (var s = 0; s < along; s++)
            {
                // dünne Stücke quer: Mitte
                var centers = new List<double>();
                for (var t = 0; t < across; t++)
                {
                    if (!At(s, t)) continue;
                    var t0 = t;
                    while (t < across && At(s, t)) t++;
                    if (t - t0 <= thin) centers.Add((t0 + t - 1) / 2.0);
                }
                var used = new bool[centers.Count];
                foreach (var (track, last) in open.ToArray())
                {
                    if (s - last > 3) continue;
                    var prev = track[^1].T;
                    var k = -1;
                    for (var c = 0; c < centers.Count; c++)
                        if (!used[c] && Math.Abs(centers[c] - prev) <= 1.5 && (k < 0 || Math.Abs(centers[c] - prev) < Math.Abs(centers[k] - prev))) k = c;
                    if (k < 0) continue;
                    used[k] = true;
                    track.Add((s, centers[k]));
                    open[open.FindIndex(o => ReferenceEquals(o.Track, track))] = (track, s);
                }
                for (var c = 0; c < centers.Count; c++)
                    if (!used[c]) open.Add(([(s, centers[c])], s));
                // abgeschlossene Spuren (seit 3 Schritten nichts mehr) ablegen
                foreach (var done in open.Where(o => s - o.Last > 3).ToList())
                {
                    open.Remove(done);
                    if (done.Track[^1].S - done.Track[0].S >= 2 * hm) tracks.Add(done.Track);
                }
            }
            tracks.AddRange(open.Select(o => o.Track).Where(t => t[^1].S - t[0].S >= 2 * hm));

            // Stücke, die an Kreuzungen oder Lücken abreißen, wieder verbinden (in Verlängerung, Lücke bis 2 Buchstabenhöhen)
            tracks = tracks.OrderBy(t => t[0].S).ToList();
            for (var i = 0; i < tracks.Count; i++)
            {
                for (var j = 0; j < tracks.Count; j++)
                {
                    if (i == j || tracks[i].Count == 0 || tracks[j].Count == 0) continue;
                    var (end, start) = (tracks[i][^1], tracks[j][0]);
                    var gap = start.S - end.S;
                    if (gap <= 0 || gap > 2 * hm) continue;
                    var slope = EndSlope(tracks[i]);
                    if (Math.Abs(end.T + slope * gap - start.T) > 2) continue;
                    tracks[i].AddRange(tracks[j]);
                    tracks[j].Clear();
                    j = -1;
                }
            }

            var lines = tracks.Where(t => t.Count > 0 && t[^1].S - t[0].S >= minLength)
                .Where(t => Math.Abs(t[^1].T - t[0].T) <= 0.35 * (t[^1].S - t[0].S))   // ungefähr in Richtung, keine Schrägen
                .OrderByDescending(t => t[^1].S - t[0].S).ToList();
            // doppelte (Rahmen mit zwei Strichen, Kanten eines dicken Strichs) weg: Abstand quer unter einer Buchstabenhöhe
            var chosen = new List<List<(double S, double T)>>();
            foreach (var t in lines)
                if (chosen.All(c => !Overlaps(c, t) || Math.Abs(MeanT(c) - MeanT(t)) > hm)) chosen.Add(t);
            chosen = chosen.Take(vertical ? 6 : 4).ToList();

            foreach (var t in chosen)
            {
                const int samples = 15;
                var pts = new List<(double X, double Y)>();
                for (var k = 0; k < samples; k++)
                {
                    var target = t[0].S + (t[^1].S - t[0].S) * (0.02 + 0.96 * k / (samples - 1));
                    var idx = t.FindIndex(p => p.S >= target);
                    var p = t[Math.Max(0, idx)];
                    double a = vertical ? a0 + p.T : a0 + p.S, b = vertical ? b0 + p.S : b0 + p.T;
                    pts.Add((a * d.X + b * n.X + 0.5 * (d.X + n.X), a * d.Y + b * n.Y + 0.5 * (d.Y + n.Y)));
                }
                result.Add(new FoundLine(vertical ? WarpLineKind.Vertical : WarpLineKind.Horizontal, FoundLineSource.Ruled, SmoothCurve(pts, 1)));
            }
        }
        return result;

        static double EndSlope(List<(double S, double T)> t)
        {
            var k = Math.Max(0, t.Count - 10);
            var ds = t[^1].S - t[k].S;
            return ds > 0 ? (t[^1].T - t[k].T) / ds : 0;
        }
        static double MeanT(List<(double S, double T)> t) => t.Average(p => p.T);
        static bool Overlaps(List<(double S, double T)> a, List<(double S, double T)> b) =>
            Math.Min(a[^1].S, b[^1].S) - Math.Max(a[0].S, b[0].S) > 0;
    }

    /// <summary>Typische Buchstabenhöhe einer Zeile (Median).</summary>
    private static double LetterHeight(List<Blob> chain) => chain.Select(b => b.Height).OrderBy(v => v).ElementAt(chain.Count / 2);

    private static double MedianBase(List<Blob> chain, bool fromEnd)
    {
        var part = (fromEnd ? chain.Skip(Math.Max(0, chain.Count - 7)) : chain.Take(7)).Select(b => b.Base).OrderBy(v => v).ToList();
        return part[part.Count / 2];
    }

    /// <summary>
    /// Punkte auf der Grundlinie: eine glatte Kurve durch die Unterkanten aller Buchstaben der Zeile (Knoten alle fünf
    /// Buchstabenhöhen, dazwischen linear, Glätte über zweite Differenzen). Robust in mehreren Durchgängen: was deutlich
    /// tiefer liegt (Unterlängen wie g, p, y) oder höher (Satzzeichen), zählt nicht mehr mit — auch wenn sich Unterlängen
    /// an einer Stelle häufen, hält die Glätte die Kurve auf der Linie der Nachbarn.
    /// </summary>
    private static IReadOnlyList<(double X, double Y)> Baseline(List<Blob> chain, (double X, double Y) d, (double X, double Y) n, double hm)
    {
        double a0 = chain[0].Along, a1 = chain[^1].Along;
        var count = Math.Clamp((int)Math.Round((a1 - a0) / (5 * hm)), 4, 13);
        var inset = Math.Min(1.5 * hm, (a1 - a0) / 8);
        var knots = Enumerable.Range(0, count).Select(k => a0 + inset + (a1 - a0 - 2 * inset) * k / (count - 1)).ToArray();
        var step = knots[1] - knots[0];

        // je Buchstabe: zwischen welchen Knoten, mit welchem Anteil (außerhalb: linear fortgesetzt)
        var taps = chain.Select(b =>
        {
            var f = (b.Along - knots[0]) / step;
            var s = Math.Clamp((int)Math.Floor(f), 0, count - 2);
            return (S: s, T: f - s, b.Base);
        }).ToArray();

        // Anfang: unteres Drittel der Unterkanten überall gleich
        var values = Enumerable.Repeat(taps.Select(t => t.Base).OrderBy(v => v).ElementAt((taps.Length - 1) / 3), count).ToArray();
        const double smooth = 2.0;
        for (var iter = 0; iter < 5; iter++)
        {
            var a = new double[count, count];
            var rhs = new double[count];
            foreach (var (s, t, value) in taps)
            {
                var fit = values[s] * (1 - t) + values[s + 1] * t;
                var r = value - fit;
                if (iter > 0 && (r > 0.2 * hm || r < -0.35 * hm)) continue;   // Unterlänge oder Ausreißer
                double w0 = 1 - t, w1 = t;
                a[s, s] += w0 * w0; a[s, s + 1] += w0 * w1; a[s + 1, s] += w0 * w1; a[s + 1, s + 1] += w1 * w1;
                rhs[s] += w0 * value; rhs[s + 1] += w1 * value;
            }
            for (var k = 1; k < count - 1; k++)
            {
                int[] idx = [k - 1, k, k + 1];
                double[] c = [1, -2, 1];
                for (var i = 0; i < 3; i++)
                for (var j = 0; j < 3; j++) a[idx[i], idx[j]] += smooth * c[i] * c[j];
            }
            for (var k = 0; k < count; k++) a[k, k] += 1e-6;
            values = SolveSmall(a, rhs) ?? values;
        }
        return knots.Select((at, k) => (at * d.X + values[k] * n.X, at * d.Y + values[k] * n.Y)).ToList();
    }

    /// <summary>Kleines Gleichungssystem (Gauß mit Spaltenpivot); null = singulär.</summary>
    private static double[]? SolveSmall(double[,] a, double[] b)
    {
        var n = b.Length;
        var m = (double[,])a.Clone();
        var x = (double[])b.Clone();
        for (var col = 0; col < n; col++)
        {
            var pivot = col;
            for (var row = col + 1; row < n; row++)
                if (Math.Abs(m[row, col]) > Math.Abs(m[pivot, col])) pivot = row;
            if (Math.Abs(m[pivot, col]) < 1e-12) return null;
            for (var k = 0; k < n; k++) (m[col, k], m[pivot, k]) = (m[pivot, k], m[col, k]);
            (x[col], x[pivot]) = (x[pivot], x[col]);
            for (var row = col + 1; row < n; row++)
            {
                var f = m[row, col] / m[col, col];
                for (var k = col; k < n; k++) m[row, k] -= f * m[col, k];
                x[row] -= f * x[col];
            }
        }
        for (var row = n - 1; row >= 0; row--)
        {
            for (var k = row + 1; k < n; k++) x[row] -= m[row, k] * x[k];
            x[row] /= m[row, row];
        }
        return x;
    }

    /// <summary>Zusammenhängende Flecken (8er-Nachbarschaft) mit ihren Pixeln.</summary>
    private static (List<Blob> Blobs, int[] Pixels) Components(bool[] mask, int w, int h)
    {
        var label = new int[w * h];
        var blobs = new List<Blob>();
        var order = new List<int>(w * h / 10);
        var stack = new Stack<int>();
        for (var i = 0; i < mask.Length; i++)
        {
            if (!mask[i] || label[i] != 0) continue;
            var b = new Blob { Start = order.Count, MinX = int.MaxValue, MinY = int.MaxValue, MaxX = -1, MaxY = -1 };
            blobs.Add(b);
            var id = blobs.Count;
            label[i] = id;
            stack.Push(i);
            double sx = 0, sy = 0;
            while (stack.Count > 0)
            {
                var p = stack.Pop();
                order.Add(p);
                int x = p % w, y = p / w;
                sx += x; sy += y;
                b.MinX = Math.Min(b.MinX, x); b.MaxX = Math.Max(b.MaxX, x);
                b.MinY = Math.Min(b.MinY, y); b.MaxY = Math.Max(b.MaxY, y);
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if ((dx == 0 && dy == 0) || nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    var q = ny * w + nx;
                    if (!mask[q] || label[q] != 0) continue;
                    label[q] = id;
                    stack.Push(q);
                }
            }
            b.Count = order.Count - b.Start;
            (b.Cx, b.Cy) = (sx / b.Count + 0.5, sy / b.Count + 0.5);
        }
        return (blobs, order.ToArray());
    }

    /// <summary>Raster über die Buchstaben für die Nachbarsuche.</summary>
    private sealed class SpatialGrid
    {
        private readonly Dictionary<(int, int), List<Blob>> _cells = [];
        private readonly double _cell;

        public SpatialGrid(IEnumerable<Blob> blobs, double cell)
        {
            _cell = Math.Max(1, cell);
            foreach (var b in blobs)
            {
                var key = ((int)Math.Floor(b.Cx / _cell), (int)Math.Floor(b.Cy / _cell));
                if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = [];
                list.Add(b);
            }
        }

        public IEnumerable<Blob> Near(double x, double y, int reach = 1)
        {
            int cx = (int)Math.Floor(x / _cell), cy = (int)Math.Floor(y / _cell);
            for (var dy = -reach; dy <= reach; dy++)
            for (var dx = -reach; dx <= reach; dx++)
                if (_cells.TryGetValue((cx + dx, cy + dy), out var list))
                    foreach (var b in list) yield return b;
        }
    }

    // ------------------------------------------------------------ Blattrand

    /// <summary>Das Blatt: hell gegen den Untergrund, Löcher gefüllt, größter Fleck. null = keins zu sehen.</summary>
    private static bool[]? PageMask(float[] gray, int w, int h)
    {
        var blurred = (float[])gray.Clone();
        Blur.Gauss(blurred, w, h, 2);
        var t = Otsu(blurred);

        // außen = dunkel und mit dem Bildrand verbunden; alles andere gehört zum Blatt (Schrift sind Löcher darin)
        var outside = new bool[w * h];
        var stack = new Stack<int>();
        void Seed(int i)
        {
            if (outside[i] || blurred[i] > t) return;
            outside[i] = true;
            stack.Push(i);
        }
        for (var x = 0; x < w; x++) { Seed(x); Seed((h - 1) * w + x); }
        for (var y = 0; y < h; y++) { Seed(y * w); Seed(y * w + w - 1); }
        while (stack.Count > 0)
        {
            var p = stack.Pop();
            int x = p % w, y = p / w;
            if (x > 0) Seed(p - 1);
            if (x < w - 1) Seed(p + 1);
            if (y > 0) Seed(p - w);
            if (y < h - 1) Seed(p + w);
        }

        // Schließen: schmale dunkle Schlitze ins Blatt (Rahmenlinien, die den Rand berühren) und der Schatten am Falz
        // gehören zum Blatt — sonst läuft der Umriss hinein, und eine Doppelseite zerfiele in zwei Blätter
        var inside = Close(outside.Select(o => !o).ToArray(), w, h, Math.Max(3, Math.Max(w, h) / 90));
        var (blobs, pixels) = Components(inside, w, h);
        if (blobs.Count == 0) return null;
        var page = blobs.MaxBy(b => b.Count)!;
        var share = (double)page.Count / (w * h);
        if (share < 0.08 || share > 0.97) return null;
        var mask = new bool[w * h];
        for (var k = page.Start; k < page.Start + page.Count; k++) mask[pixels[k]] = true;
        return mask;
    }

    /// <summary>Morphologisches Schließen (erst wachsen, dann schrumpfen) mit einem Quadrat ±<paramref name="r"/> — über Summenbilder.</summary>
    private static bool[] Close(bool[] mask, int w, int h, int r)
    {
        bool[] Filter(bool[] m, bool grow)
        {
            var sum = new int[(w + 1) * (h + 1)];
            for (var y = 0; y < h; y++)
            {
                var row = 0;
                for (var x = 0; x < w; x++)
                {
                    row += m[y * w + x] ? 1 : 0;
                    sum[(y + 1) * (w + 1) + x + 1] = sum[y * (w + 1) + x + 1] + row;
                }
            }
            var result = new bool[w * h];
            Parallel.For(0, h, y =>
            {
                int y0 = Math.Max(0, y - r), y1 = Math.Min(h, y + r + 1);
                for (var x = 0; x < w; x++)
                {
                    int x0 = Math.Max(0, x - r), x1 = Math.Min(w, x + r + 1);
                    var count = sum[y1 * (w + 1) + x1] - sum[y0 * (w + 1) + x1] - sum[y1 * (w + 1) + x0] + sum[y0 * (w + 1) + x0];
                    // wachsen: irgendein Pixel im Quadrat; schrumpfen: alle (am Bildrand zählt nur, was im Bild liegt)
                    result[y * w + x] = grow ? count > 0 : count == (y1 - y0) * (x1 - x0);
                }
            });
            return result;
        }
        return Filter(Filter(mask, grow: true), grow: false);
    }

    private static float Otsu(float[] v)
    {
        var hist = new double[256];
        foreach (var x in v) hist[Math.Clamp((int)(x * 255), 0, 255)]++;
        double total = v.Length, sum = 0;
        for (var i = 0; i < 256; i++) sum += i * hist[i];
        double wb = 0, sb = 0, best = -1;
        var threshold = 128;
        for (var i = 0; i < 256; i++)
        {
            wb += hist[i];
            if (wb == 0) continue;
            var wf = total - wb;
            if (wf == 0) break;
            sb += i * hist[i];
            double mb = sb / wb, mf = (sum - sb) / wf, between = wb * wf * (mb - mf) * (mb - mf);
            if (between > best) (best, threshold) = (between, i);
        }
        return (threshold + 0.5f) / 255;
    }

    private static List<FoundLine> PageEdges(float[] gray, bool[] page, int w, int h, (double X, double Y)? direction, FoundLine? fold)
    {
        var contour = TraceContour(page, w, h);
        if (contour.Count < 40) return [];

        // Ecken im Rahmen der Schrift (sonst des Bildes): oben links, oben rechts, unten rechts, unten links
        var d = direction ?? (1, 0);
        (double X, double Y) n = (-d.Y, d.X);
        double A(int i) => contour[i].X * d.X + contour[i].Y * d.Y;
        double B(int i) => contour[i].X * n.X + contour[i].Y * n.Y;
        var idx = Enumerable.Range(0, contour.Count).ToArray();
        var corners = new[]
        {
            idx.MinBy(i => A(i) + B(i)), idx.MaxBy(i => A(i) - B(i)), idx.MaxBy(i => A(i) + B(i)), idx.MinBy(i => A(i) - B(i)),
        };
        // Umlaufrichtung: von oben links nach oben rechts muss der kurze Weg über die Oberkante gehen
        var forward = Forward(corners[0], corners[1], contour.Count) <= Forward(corners[1], corners[0], contour.Count);
        if (!forward)
        {
            contour.Reverse();
            corners = corners.Select(c => contour.Count - 1 - c).ToArray();
        }

        var blurred = (float[])gray.Clone();
        Blur.Gauss(blurred, w, h, 1.2);
        var lines = new List<FoundLine>();
        for (var e = 0; e < 4; e++)
        {
            int from = corners[e], to = corners[(e + 1) % 4];
            var length = Forward(from, to, contour.Count);
            if (length < 30) continue;
            var kind = e % 2 == 0 ? WarpLineKind.Horizontal : WarpLineKind.Vertical;
            // Doppelseite: Ober- und Unterkante knicken am Falz — je Seite eine eigene Kante (eine glatte Kurve über den
            // Knick läge bis zu 50 px daneben)
            var split = kind == WarpLineKind.Horizontal && fold is not null ? FoldCrossing(contour, from, length, fold) : -1;
            var parts = split > 0 ? new[] { (from, split), (from + split, length - split) } : [(from, length)];
            foreach (var (start, count) in parts)
                if (count >= 30 && EdgeLine(contour, start, count, blurred, w, h) is { } points)
                    lines.Add(new FoundLine(kind, FoundLineSource.Edge, points));
        }
        return lines;
    }

    /// <summary>
    /// Punkte auf einem Stück des Umrisses: 15 Stellen (nicht ganz bis in die Ecken), geglättet über die Nachbarn, auf den
    /// stärksten Sprung quer zur Kante geschoben und durch eine glatte Kurve entrauscht. null: liegt am Fotorand oder der
    /// Umriss läuft hin und her (dann ist es nicht der Blattrand).
    /// </summary>
    private static IReadOnlyList<(double X, double Y)>? EdgeLine(List<(double X, double Y)> contour, int from, int length,
        float[] blurred, int w, int h)
    {
        var points = new List<(double X, double Y)>();
        var onBorder = 0;
        const int samples = 15;
        var window = Math.Max(2, length / 40);
        (double X, double Y) At(int i) => contour[((i % contour.Count) + contour.Count) % contour.Count];
        for (var k = 0; k < samples; k++)
        {
            var at = from + (int)Math.Round(length * (0.02 + 0.96 * k / (samples - 1)));   // nicht ganz in die Ecken
            double sx = 0, sy = 0;
            for (var j = -window; j <= window; j++)
            {
                var p = At(at + j);
                sx += p.X; sy += p.Y;
            }
            var c = (X: sx / (2 * window + 1), Y: sy / (2 * window + 1));
            if (c.X < 3 || c.Y < 3 || c.X > w - 4 || c.Y > h - 4) onBorder++;
            // quer zur Kante auf den stärksten Sprung
            var (a, b) = (At(at - window), At(at + window));
            double tx = b.X - a.X, ty = b.Y - a.Y, tl = Math.Sqrt(tx * tx + ty * ty);
            if (tl > 1e-9) c = Refine(blurred, w, h, c, (-ty / tl, tx / tl));
            points.Add(c);
        }
        if (onBorder > samples * 0.3) return null;   // liegt am Fotorand: nicht das Blatt
        // der Umriss muss der Kante folgen, nicht hin und her laufen (höchstens 30 % länger als die Verbindung)
        double path = 0;
        for (var j = 0; j < length; j++)
        {
            var (p, q) = (At(from + j), At(from + j + 1));
            path += Math.Sqrt(Sq(q.X - p.X) + Sq(q.Y - p.Y));
        }
        var (s, e) = (At(from), At(from + length));
        var chord = Math.Sqrt(Sq(e.X - s.X) + Sq(e.Y - s.Y));
        if (path > 1.3 * chord * 1.08) return null;   // 8 % für die Treppen der Pixel
        return SmoothCurve(points, 1.5);
    }

    /// <summary>Wo kreuzt das Stück des Umrisses die Falzlinie (Index ab <paramref name="from"/>)? −1 = gar nicht.</summary>
    private static int FoldCrossing(List<(double X, double Y)> contour, int from, int length, FoundLine fold)
    {
        // Falz als Gerade (Hauptrichtung seiner Punkte)
        double cx = fold.Points.Average(p => p.X), cy = fold.Points.Average(p => p.Y);
        double sxx = 0, sxy = 0, syy = 0;
        foreach (var p in fold.Points) { sxx += Sq(p.X - cx); sxy += (p.X - cx) * (p.Y - cy); syy += Sq(p.Y - cy); }
        var angle = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
        (double X, double Y) dir = (Math.Cos(angle), Math.Sin(angle));
        double Side(int i)
        {
            var p = contour[(from + i) % contour.Count];
            return (p.X - cx) * dir.Y - (p.Y - cy) * dir.X;
        }
        var best = -1;
        for (var i = 1; i < length; i++)
            if (Math.Sign(Side(i)) != Math.Sign(Side(i - 1)) && (best < 0 || Math.Abs(i - length / 2) < Math.Abs(best - length / 2))) best = i;
        return best;
    }

    /// <summary>
    /// Punkte einer glatten Linie (Papierkante, Falz) entrauschen: Ausgleichskurve 5. Grades quer zur Verbindung von erstem zu
    /// letztem Punkt, robust (was mehr als <paramref name="tolerance"/> Pixel daneben liegt, zählt weniger), jeder Punkt
    /// auf die Kurve geschoben.
    /// </summary>
    internal static IReadOnlyList<(double X, double Y)> SmoothCurve(IReadOnlyList<(double X, double Y)> points, double tolerance)
    {
        if (points.Count < 5) return points;
        var (a, b) = (points[0], points[^1]);
        double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-9) return points;
        (double X, double Y) u = (dx / len, dy / len), v = (-u.Y, u.X);
        var t = points.Select(p => ((p.X - a.X) * u.X + (p.Y - a.Y) * u.Y) / len).ToArray();
        var o = points.Select(p => (p.X - a.X) * v.X + (p.Y - a.Y) * v.Y).ToArray();
        var weights = Enumerable.Repeat(1.0, points.Count).ToArray();
        const int degree = 5;   // eingerollte Kanten unter Schrägsicht sind mehr als kubisch
        var terms = Math.Min(degree + 1, points.Count - 1);
        double Poly(double[] cc, double x) { double s = 0, p = 1; foreach (var v in cc) { s += v * p; p *= x; } return s; }
        var c = new double[terms];
        for (var iter = 0; iter < 6; iter++)
        {
            var m = new double[terms, terms];
            var r = new double[terms];
            for (var i = 0; i < t.Length; i++)
            {
                var basis = new double[terms];
                for (int j = 0, p = 0; j < terms; j++, p++) basis[j] = Math.Pow(t[i], p);
                for (var j = 0; j < terms; j++)
                {
                    r[j] += weights[i] * basis[j] * o[i];
                    for (var k = 0; k < terms; k++) m[j, k] += weights[i] * basis[j] * basis[k];
                }
            }
            for (var j = 0; j < terms; j++) m[j, j] += 1e-9;
            c = SolveSmall(m, r) ?? c;
            for (var i = 0; i < t.Length; i++)
            {
                var res = Math.Abs(o[i] - Poly(c, t[i]));
                weights[i] = res <= tolerance ? 1 : tolerance / res;   // Huber
            }
        }
        return t.Select(ti =>
        {
            var oi = Poly(c, ti);
            return (a.X + ti * len * u.X + oi * v.X, a.Y + ti * len * u.Y + oi * v.Y);
        }).ToArray();
    }

    private static int Forward(int from, int to, int count) => ((to - from) % count + count) % count;

    /// <summary>Punkt entlang der Normalen (± 4 px) auf den stärksten Helligkeitssprung, mit Unterpixel (Parabel).</summary>
    private static (double X, double Y) Refine(float[] g, int w, int h, (double X, double Y) p, (double X, double Y) normal)
    {
        double Sample(double x, double y)
        {
            // Punkte meinen Flächen (Pixel i reicht von i bis i + 1), der Wert eines Pixels gilt in seiner Mitte
            x = Math.Clamp(x - 0.5, 0, w - 1.001); y = Math.Clamp(y - 0.5, 0, h - 1.001);
            int ix = (int)x, iy = (int)y;
            double fx = x - ix, fy = y - iy;
            var i = iy * w + ix;
            return (g[i] * (1 - fx) + g[i + 1] * fx) * (1 - fy) + (g[i + w] * (1 - fx) + g[i + w + 1] * fx) * fy;
        }
        const int reach = 4;
        var grad = new double[2 * reach + 1];
        for (var k = -reach; k <= reach; k++)
        {
            double x = p.X + k * normal.X, y = p.Y + k * normal.Y;
            grad[k + reach] = Math.Abs(Sample(x + 0.5 * normal.X, y + 0.5 * normal.Y) - Sample(x - 0.5 * normal.X, y - 0.5 * normal.Y));
        }
        var best = Array.IndexOf(grad, grad.Max());
        double offset = best - reach;
        if (best > 0 && best < grad.Length - 1)
        {
            var den = grad[best - 1] - 2 * grad[best] + grad[best + 1];
            if (Math.Abs(den) > 1e-9) offset += 0.5 * (grad[best - 1] - grad[best + 1]) / den;
        }
        return (p.X + offset * normal.X, p.Y + offset * normal.Y);
    }

    /// <summary>Umriss eines Flecks (Moore-Nachbarschaft), als Folge von Randpixeln (Mitte).</summary>
    private static List<(double X, double Y)> TraceContour(bool[] mask, int w, int h)
    {
        var start = Array.IndexOf(mask, true);
        if (start < 0) return [];
        bool In(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && mask[y * w + x];
        int[] dx = [1, 1, 0, -1, -1, -1, 0, 1], dy = [0, 1, 1, 1, 0, -1, -1, -1];
        var result = new List<(double X, double Y)>();
        int cx = start % w, cy = start / w, dir = 7;
        var limit = 4 * (w + h) * 4;
        do
        {
            result.Add((cx + 0.5, cy + 0.5));
            var found = false;
            for (var k = 0; k < 8; k++)
            {
                var nd = (dir + 6 + k) % 8;   // links vom Rückweg beginnen
                int nx = cx + dx[nd], ny = cy + dy[nd];
                if (!In(nx, ny)) continue;
                (cx, cy, dir) = (nx, ny, nd);
                found = true;
                break;
            }
            if (!found || result.Count > limit) break;
        } while (cx != start % w || cy != start / w);
        return result;
    }

    // ------------------------------------------------------------ Falz

    private static FoundLine? Fold(float[] gray, bool[] page, int w, int h, (double X, double Y)? direction)
    {
        var d = direction ?? (1, 0);
        (double X, double Y) n = (-d.Y, d.X);
        // Blatt im Rahmen der Schrift: a entlang, b quer
        double aMin = double.MaxValue, aMax = double.MinValue, bMin = double.MaxValue, bMax = double.MinValue;
        for (var y = 0; y < h; y += 2)
        for (var x = 0; x < w; x += 2)
        {
            if (!page[y * w + x]) continue;
            double a = x * d.X + y * d.Y, b = x * n.X + y * n.Y;
            aMin = Math.Min(aMin, a); aMax = Math.Max(aMax, a); bMin = Math.Min(bMin, b); bMax = Math.Max(bMax, b);
        }
        double width = aMax - aMin, height = bMax - bMin;
        if (width < 100 || height < 100) return null;

        const int bands = 9;
        var bins = (int)Math.Ceiling(width);
        var found = new List<(double A, double B)>();
        for (var band = 0; band < bands; band++)
        {
            double b0 = bMin + height * (0.08 + 0.84 * band / bands), b1 = bMin + height * (0.08 + 0.84 * (band + 1) / bands);
            var sum = new double[bins];
            var cnt = new double[bins];
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                if (!page[y * w + x]) continue;
                var b = x * n.X + y * n.Y;
                if (b < b0 || b >= b1) continue;
                var bin = Math.Clamp((int)(x * d.X + y * d.Y - aMin), 0, bins - 1);
                sum[bin] += gray[y * w + x];
                cnt[bin]++;
            }
            var profile = new double[bins];
            for (var i = 0; i < bins; i++) profile[i] = cnt[i] > 0 ? sum[i] / cnt[i] : double.NaN;
            // glätten (±2), nur das mittlere Drittel
            int lo = (int)(0.3 * bins), hi = (int)(0.7 * bins);
            var sm = new double[bins];
            for (var i = lo; i < hi; i++)
            {
                double s = 0, c = 0;
                for (var k = -2; k <= 2; k++)
                    if (i + k >= 0 && i + k < bins && double.IsFinite(profile[i + k])) { s += profile[i + k]; c++; }
                sm[i] = c > 0 ? s / c : double.NaN;
            }
            var mid = Enumerable.Range(lo, hi - lo).Where(i => double.IsFinite(sm[i])).ToList();
            if (mid.Count < 20) continue;
            var reference = mid.Select(i => sm[i]).OrderBy(v => v).ElementAt(mid.Count / 2);
            var at = mid.MinBy(i => sm[i]);
            var depth = reference - sm[at];
            if (reference <= 0 || depth / reference < 0.08) continue;
            // schmal: halbe Tiefe höchstens 6 % der Blattbreite breit
            var half = reference - depth / 2;
            int l = at, r = at;
            while (l > lo && sm[l] < half) l--;
            while (r < hi - 1 && sm[r] < half) r++;
            if (r - l > 0.06 * bins) continue;
            found.Add((aMin + at + 0.5, (b0 + b1) / 2));
        }
        if (found.Count < 6) return null;
        var median = found.Select(f => f.A).OrderBy(v => v).ElementAt(found.Count / 2);
        var line = found.Where(f => Math.Abs(f.A - median) < 0.04 * width).ToList();
        if (line.Count < 6) return null;
        return new FoundLine(WarpLineKind.Fold, FoundLineSource.Fold,
            SmoothCurve(line.Select(f => (f.A * d.X + f.B * n.X, f.A * d.Y + f.B * n.Y)).ToArray(), 1.5));
    }

    private static double Sq(double v) => v * v;
}
