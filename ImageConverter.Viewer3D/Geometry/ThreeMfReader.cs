using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Xml;

namespace ImageConverter.Viewer3D.Geometry;

/// <summary>
/// Reads 3MF files (3D Manufacturing Format, ISO/ASTM 52915) into a <see cref="Mesh"/>: all objects placed on the
/// build plate, with their transforms, in millimetres, corners at the same position merged into one vertex.
/// </summary>
/// <remarks>
/// <para>Format: a zip archive; <c>_rels/.rels</c> names the model part (relationship type ending in
/// <c>3dmodel</c>, usually <c>3D/3dmodel.model</c>), an XML document with <c>&lt;resources&gt;</c> (objects with a
/// <c>&lt;mesh&gt;</c> of vertices and triangles, or <c>&lt;components&gt;</c> referencing other objects with a
/// transform) and <c>&lt;build&gt;</c> (items placing objects with a transform). A transform is a row-major 3 × 4 matrix
/// "m00 m01 m02 m10 m11 m12 m20 m21 m22 m30 m31 m32"; the last row is the translation. The unit of the model is given on
/// the root element and converted to millimetres here. Colours, materials, textures and slices are ignored.</para>
/// <para>Files come from anywhere, so reading is defensive: the XML is streamed without DTD or external references,
/// the unpacked size of the model part is limited, triangles and vertices are counted against the limits of
/// <see cref="StlReader"/> while reading, coordinates must be finite and within range, and component references are
/// limited in depth and guarded against cycles. Memory and time grow linearly with the file.</para>
/// </remarks>
public static class ThreeMfReader
{
    /// <summary>Largest unpacked model part accepted (bytes); a text file beyond that cannot hold a displayable model.</summary>
    public const long MaxModelPartBytes = 2L << 30;

    /// <summary>Deepest nesting of components accepted.</summary>
    private const int MaxComponentDepth = 32;

    private const string ModelRelationship = "3dmodel";

    /// <summary>Reads a 3MF file.</summary>
    /// <param name="path">Path of the file.</param>
    /// <param name="ct">Cancels reading.</param>
    /// <param name="progress">Share of the model part read so far (0 … 1); may be null.</param>
    /// <returns>The mesh and what was found on the way; <see cref="StlFile.Encoding"/> is <see cref="StlEncoding.ThreeMf"/>.</returns>
    /// <exception cref="StlFormatException">Not a 3MF file, no usable triangles, or beyond the limits.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public static StlFile Read(string path, CancellationToken ct = default, IProgress<double>? progress = null)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        return Read(stream, ct, progress);
    }

    /// <summary>Reads 3MF data from a seekable stream.</summary>
    /// <inheritdoc cref="Read(string, CancellationToken, IProgress{double}?)"/>
    /// <param name="stream">Seekable stream positioned at the start of the archive.</param>
    /// <param name="ct">Cancels reading.</param>
    /// <param name="progress">Share of the model part read so far (0 … 1); may be null.</param>
    public static StlFile Read(Stream stream, CancellationToken ct = default, IProgress<double>? progress = null)
    {
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException)
        {
            throw new StlFormatException(Tr.T("Das ist keine 3MF-Datei (kein Zip-Archiv)."));
        }
        using (archive)
        {
            var part = ModelPart(archive)
                ?? throw new StlFormatException(Tr.T("Das ist keine 3MF-Datei (kein 3D-Modell im Archiv)."));
            if (part.Length > MaxModelPartBytes)
                throw new StlFormatException(Tr.T("Das 3MF-Archiv ist zu groß ({0:N0} MB entpackt).", part.Length >> 20));
            using var xml = part.Open();
            var reporting = progress is null ? xml : new ProgressStream(xml, part.Length, progress);
            return ReadModel(reporting, ct);
        }
    }

    // ------------------------------------------------------------------ archive

    /// <summary>The model part: named by the package relationships, otherwise the first entry ending in ".model".</summary>
    private static ZipArchiveEntry? ModelPart(ZipArchive archive)
    {
        var rels = archive.GetEntry("_rels/.rels");
        if (rels is not null && rels.Length < 1 << 20)
        {
            try
            {
                using var s = rels.Open();
                using var reader = XmlReader.Create(s, Settings());
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Relationship") continue;
                    var type = reader.GetAttribute("Type") ?? "";
                    var target = reader.GetAttribute("Target") ?? "";
                    if (!type.EndsWith(ModelRelationship, StringComparison.OrdinalIgnoreCase) || target.Length == 0) continue;
                    var entry = archive.GetEntry(target.TrimStart('/'));
                    if (entry is not null) return entry;
                }
            }
            catch (XmlException)
            {
                // damaged relationships: fall back to the conventional name
            }
        }
        return archive.GetEntry("3D/3dmodel.model")
            ?? archive.Entries.FirstOrDefault(e => e.FullName.EndsWith(".model", StringComparison.OrdinalIgnoreCase));
    }

    private static XmlReaderSettings Settings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
        MaxCharactersFromEntities = 1024,
    };

    // ------------------------------------------------------------------ model document

    /// <summary>An object of the resources: either a mesh or a list of components.</summary>
    private sealed class ModelObject
    {
        public float[] Positions = [];
        public int[] Triangles = [];
        public bool[] InvalidVertex = [];
        public int InvalidTriangles;
        public List<(int ObjectId, Matrix4x4 Transform)> Components = [];
        public bool Hidden;   // support structures and "other" objects are not part of the model
    }

    private static StlFile ReadModel(Stream xmlStream, CancellationToken ct)
    {
        var objects = new Dictionary<int, ModelObject>();
        var build = new List<(int ObjectId, Matrix4x4 Transform)>();
        var scale = 1.0f;
        var title = "";
        var fewestDigits = int.MaxValue;
        long trianglesInFile = 0;

        using var reader = XmlReader.Create(xmlStream, Settings());
        try
        {
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (reader.NodeType != XmlNodeType.Element) continue;
                switch (reader.LocalName)
                {
                    case "model":
                        scale = UnitScale(reader.GetAttribute("unit"));
                        break;
                    case "metadata":
                        if (string.Equals(reader.GetAttribute("name"), "Title", StringComparison.OrdinalIgnoreCase))
                            title = reader.ReadElementContentAsString().Trim();
                        break;
                    case "object":
                        var (id, obj) = ReadObject(reader, ref fewestDigits, ref trianglesInFile, ct);
                        if (id >= 0) objects[id] = obj;
                        break;
                    case "item":
                        if (int.TryParse(reader.GetAttribute("objectid"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var itemId))
                            build.Add((itemId, Transform(reader.GetAttribute("transform"))));
                        break;
                }
            }
        }
        catch (XmlException e)
        {
            throw new StlFormatException(Tr.T("Die 3MF-Datei ist beschädigt: {0}", e.Message));
        }

        if (build.Count == 0)
            throw new StlFormatException(Tr.T("Die 3MF-Datei enthält keine Dreiecke."));
        if (trianglesInFile > StlReader.TriangleLimit)
            throw new StlFormatException(Tr.T("Das Modell hat {0:N0} Dreiecke – angezeigt werden höchstens {1:N0}.", trianglesInFile, StlReader.TriangleLimit));

        // Place every build item: components are placed inside their parent's transform (p · component · item). An
        // object may be placed several times, so the placed triangles are counted on the way.
        var welder = new VertexWelder((int)Math.Min(StlReader.MaxVertices, Math.Max(16, trianglesInFile / 2 + 16)));
        var indices = new int[3 * (int)Math.Min(StlReader.TriangleLimit, Math.Max(1, trianglesInFile))];
        var written = 0;
        long placed = 0;
        int invalid = 0, degenerate = 0;
        var unitMatrix = Matrix4x4.CreateScale(scale);
        var chain = new HashSet<int>();
        foreach (var (objectId, transform) in build)
            Place(objectId, transform * unitMatrix, 0);

        if (written == 0)
            throw new StlFormatException(Tr.T("Die 3MF-Datei enthält keine gültigen Dreiecke."));
        if (written != indices.Length) Array.Resize(ref indices, written);
        var resolution = fewestDigits == int.MaxValue
            ? StlReader.FloatResolution
            : Math.Max(StlReader.FloatResolution, 0.5 * Math.Pow(10, 1 - fewestDigits));
        return new StlFile(new Mesh(welder.TakePositions(), indices), StlEncoding.ThreeMf, title, placed,
            invalid, degenerate, Truncated: false, resolution);

        void Place(int id, Matrix4x4 m, int depth)
        {
            ct.ThrowIfCancellationRequested();
            if (!objects.TryGetValue(id, out var obj) || obj.Hidden) return;
            if (depth > MaxComponentDepth || !chain.Add(id))
                throw new StlFormatException(Tr.T("Die 3MF-Datei ist beschädigt: Bauteile verweisen im Kreis aufeinander."));
            foreach (var (childId, childTransform) in obj.Components)
                Place(childId, childTransform * m, depth + 1);
            chain.Remove(id);

            invalid += obj.InvalidTriangles;
            placed += obj.InvalidTriangles;
            if (obj.Triangles.Length == 0) return;
            placed += obj.Triangles.Length / 3;
            if (placed > StlReader.TriangleLimit)
                throw new StlFormatException(Tr.T("Das Modell hat {0:N0} Dreiecke – angezeigt werden höchstens {1:N0}.", placed, StlReader.TriangleLimit));
            if (written + obj.Triangles.Length > indices.Length)
                Array.Resize(ref indices, (int)Math.Min(3L * StlReader.TriangleLimit, Math.Max(written + obj.Triangles.Length, indices.Length * 3L / 2)));
            // A mirroring transform turns the triangles inside out; swapping two corners keeps the outside outside.
            var mirrored = Determinant3(m) < 0;
            var p = obj.Positions;
            for (var t = 0; t + 2 < obj.Triangles.Length; t += 3)
            {
                int a = obj.Triangles[t], b = obj.Triangles[t + 1], c = obj.Triangles[t + 2];
                if (obj.InvalidVertex[a] || obj.InvalidVertex[b] || obj.InvalidVertex[c]) { invalid++; continue; }
                var va = Vector3.Transform(new Vector3(p[3 * a], p[3 * a + 1], p[3 * a + 2]), m);
                var vb = Vector3.Transform(new Vector3(p[3 * b], p[3 * b + 1], p[3 * b + 2]), m);
                var vc = Vector3.Transform(new Vector3(p[3 * c], p[3 * c + 1], p[3 * c + 2]), m);
                if (mirrored) (vb, vc) = (vc, vb);
                if (!Valid(va) || !Valid(vb) || !Valid(vc)) { invalid++; continue; }
                var ia = welder.Add(va.X, va.Y, va.Z);
                var ib = welder.Add(vb.X, vb.Y, vb.Z);
                var ic = welder.Add(vc.X, vc.Y, vc.Z);
                if (ia == ib || ib == ic || ia == ic) { degenerate++; continue; }
                indices[written] = ia;
                indices[written + 1] = ib;
                indices[written + 2] = ic;
                written += 3;
            }
        }
    }

    /// <summary>Reads one &lt;object&gt; with its mesh or components; the reader is left after the element.</summary>
    private static (int Id, ModelObject Object) ReadObject(XmlReader reader, ref int fewestDigits, ref long trianglesInFile, CancellationToken ct)
    {
        var obj = new ModelObject();
        if (!int.TryParse(reader.GetAttribute("id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) id = -1;
        var type = reader.GetAttribute("type") ?? "model";
        obj.Hidden = type is "support" or "surface" or "other";
        if (reader.IsEmptyElement) return (id, obj);

        var positions = new List<float>();
        var triangles = new List<int>();
        var depth = reader.Depth;
        while (reader.Read() && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
        {
            ct.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element) continue;
            switch (reader.LocalName)
            {
                case "vertex":
                    positions.Add(Coordinate(reader.GetAttribute("x"), ref fewestDigits));
                    positions.Add(Coordinate(reader.GetAttribute("y"), ref fewestDigits));
                    positions.Add(Coordinate(reader.GetAttribute("z"), ref fewestDigits));
                    if (positions.Count / 3 > StlReader.MaxVertices)
                        throw new StlFormatException(Tr.T("Das Modell hat mehr als {0:N0} einzelne Eckpunkte – das lässt sich nicht anzeigen.", StlReader.MaxVertices));
                    break;
                case "triangle":
                    triangles.Add(Index(reader.GetAttribute("v1")));
                    triangles.Add(Index(reader.GetAttribute("v2")));
                    triangles.Add(Index(reader.GetAttribute("v3")));
                    if (!obj.Hidden && ++trianglesInFile > StlReader.TriangleLimit)
                        throw new StlFormatException(Tr.T("Das Modell hat {0:N0} Dreiecke – angezeigt werden höchstens {1:N0}.", trianglesInFile, StlReader.TriangleLimit));
                    break;
                case "component":
                    if (int.TryParse(reader.GetAttribute("objectid"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var childId))
                        obj.Components.Add((childId, Transform(reader.GetAttribute("transform"))));
                    break;
            }
        }

        var vertexCount = positions.Count / 3;
        obj.Positions = positions.ToArray();
        obj.InvalidVertex = new bool[vertexCount];
        for (var v = 0; v < vertexCount; v++)
            obj.InvalidVertex[v] = !Valid(obj.Positions[3 * v]) || !Valid(obj.Positions[3 * v + 1]) || !Valid(obj.Positions[3 * v + 2]);
        // Triangles with an index outside the vertex list are dropped here; the others keep their indices
        var kept = new List<int>(triangles.Count);
        for (var t = 0; t + 2 < triangles.Count; t += 3)
        {
            int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
            if ((uint)a >= (uint)vertexCount || (uint)b >= (uint)vertexCount || (uint)c >= (uint)vertexCount) { obj.InvalidTriangles++; continue; }
            kept.Add(a); kept.Add(b); kept.Add(c);
        }
        obj.Triangles = kept.ToArray();
        return (id, obj);
    }

    // ------------------------------------------------------------------ values

    private static bool Valid(float v) => float.IsFinite(v) && Math.Abs(v) <= StlReader.MaxCoordinate;

    private static bool Valid(Vector3 v) => Valid(v.X) && Valid(v.Y) && Valid(v.Z);

    private static int Index(string? s) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : -1;

    /// <summary>A coordinate from its text; counts its significant digits for <see cref="StlFile.Resolution"/>.</summary>
    private static float Coordinate(string? s, ref int fewestDigits)
    {
        if (s is null || !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return float.NaN;
        int digits = 0, started = 0;
        foreach (var ch in s)
        {
            if (ch is 'e' or 'E') break;
            if (ch < '0' || ch > '9') continue;
            if (ch != '0') started = 1;
            if (started == 1) digits++;
        }
        // Trailing zeros are digits the writer chose to print; a number like "12" may be exact, so at least six
        if (digits > 0) fewestDigits = Math.Min(fewestDigits, Math.Max(digits, 6));
        return (float)value;
    }

    /// <summary>Scale from the model unit to millimetres; unknown or missing units count as millimetres.</summary>
    private static float UnitScale(string? unit) => unit?.ToLowerInvariant() switch
    {
        "micron" => 0.001f,
        "centimeter" => 10f,
        "inch" => 25.4f,
        "foot" => 304.8f,
        "meter" => 1000f,
        _ => 1f,
    };

    /// <summary>The 3 × 4 row-major transform of the file as a matrix for <see cref="Vector3.Transform(Vector3, Matrix4x4)"/>
    /// (row vectors); missing or damaged = identity.</summary>
    private static Matrix4x4 Transform(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Matrix4x4.Identity;
        var parts = text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 12) return Matrix4x4.Identity;
        var m = new float[12];
        for (var i = 0; i < 12; i++)
        {
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out m[i]) || !float.IsFinite(m[i]))
                return Matrix4x4.Identity;
        }
        return new Matrix4x4(
            m[0], m[1], m[2], 0,
            m[3], m[4], m[5], 0,
            m[6], m[7], m[8], 0,
            m[9], m[10], m[11], 1);
    }

    private static float Determinant3(in Matrix4x4 m) =>
        m.M11 * (m.M22 * m.M33 - m.M23 * m.M32)
        - m.M12 * (m.M21 * m.M33 - m.M23 * m.M31)
        + m.M13 * (m.M21 * m.M32 - m.M22 * m.M31);

    /// <summary>Reports how much of the model part has been read.</summary>
    private sealed class ProgressStream(Stream inner, long length, IProgress<double> progress) : Stream
    {
        private long _read;
        private long _nextReport;

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, count);
            _read += n;
            if (_read >= _nextReport)
            {
                progress.Report(Math.Min(1.0, (double)_read / Math.Max(1, length)));
                _nextReport = _read + Math.Max(1 << 16, length / 100);
            }
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
