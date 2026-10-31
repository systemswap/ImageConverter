using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace ImageConverter.Viewer3D.Geometry;

/// <summary>How the STL file stores its triangles.</summary>
public enum StlEncoding
{
    Binary,
    Ascii,
}

/// <summary>Result of reading an STL file.</summary>
/// <param name="Mesh">The triangles with shared vertices.</param>
/// <param name="Encoding">Binary or text.</param>
/// <param name="Header">Printable part of the header (binary) or the solid name (text); may be empty.</param>
/// <param name="FileTriangles">Triangles the file announces (binary) or contains (text).</param>
/// <param name="Invalid">Triangles skipped because a coordinate was not a finite number or out of range.</param>
/// <param name="Degenerate">Triangles skipped because two corners are the same point.</param>
/// <param name="Truncated">The binary file ends before all announced triangles.</param>
/// <param name="Resolution">
/// Relative rounding of the coordinates in the file (half a step, relative to the value): 2⁻²⁴ for binary files (32-bit
/// floats); for text files from the fewest significant digits found, for example 5·10⁻⁷ for seven digits. Faces are
/// recognised with this tolerance.
/// </param>
public sealed record StlFile(Mesh Mesh, StlEncoding Encoding, string Header, long FileTriangles, int Invalid, int Degenerate, bool Truncated,
    double Resolution = StlReader.FloatResolution);

/// <summary>The file is no STL file, is damaged beyond use or exceeds the limits; the message is meant for the user.</summary>
public sealed class StlFormatException(string message) : Exception(message);

/// <summary>
/// Reads binary and text STL files into a <see cref="Mesh"/>, merging corners at the same position into one vertex.
/// </summary>
/// <remarks>
/// <para>Format: binary = 80-byte header, 32-bit little-endian triangle count, then 50 bytes per triangle (normal,
/// three corners as 32-bit floats, 16-bit attribute); text = <c>solid … facet normal … outer loop vertex x y z …
/// endloop endfacet … endsolid</c>. The stored normals are ignored: they are often missing or wrong, and the
/// viewer derives its own from the corners.</para>
/// <para>Files come from anywhere, so reading is defensive: the triangle count is checked against
/// <see cref="TriangleLimit"/> and the file size before anything is allocated, text lines are limited to
/// <see cref="MaxLineLength"/> bytes, coordinates must be finite and within ±<see cref="MaxCoordinate"/>, and the
/// vertex table hashes with a random per-process seed, so that a crafted file cannot force long collision chains.
/// Memory and time therefore grow linearly with the file, and reading can be cancelled at any point.</para>
/// </remarks>
public static class StlReader
{
    /// <summary>Most triangles accepted on any computer (about 1 GB as a binary file).</summary>
    public const int MaxTriangles = 20_000_000;

    /// <summary>
    /// Memory the viewer needs per triangle at its peak (reading, neighbourhood, faces and hierarchy at once), with
    /// margin: measured 116–147 bytes on the managed heap for 1 and 5 million triangles, about 180 including the runtime.
    /// </summary>
    public const int PeakBytesPerTriangle = 200;

    /// <summary>
    /// Most triangles accepted on this computer: <see cref="MaxTriangles"/>, or fewer if half of the memory available to
    /// the program would not hold the peak – a large but genuine file then gets a clear message instead of running the
    /// program out of memory.
    /// </summary>
    public static int TriangleLimit { get; } =
        (int)Math.Clamp(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 2 / PeakBytesPerTriangle, 100_000, MaxTriangles);

    /// <summary>Most distinct vertices accepted; bounds the memory of files whose triangles share no corners.</summary>
    public const int MaxVertices = 24_000_000;

    /// <summary>Longest line accepted in a text file, in bytes.</summary>
    public const int MaxLineLength = 4096;

    /// <summary>Largest coordinate accepted (in file units). Beyond that the float precision of the file is gone anyway.</summary>
    public const double MaxCoordinate = 1e9;

    /// <summary>Half a float step relative to the value: the rounding of coordinates stored as 32-bit floats.</summary>
    public const double FloatResolution = 1.0 / (1 << 24);

    private const int BinaryHeader = 84;
    private const int BinaryRecord = 50;
    private const int MaxFacetCorners = 64;

    /// <summary>Reads an STL file.</summary>
    /// <param name="path">Path of the file.</param>
    /// <param name="ct">Cancels reading.</param>
    /// <param name="progress">Share of the file read so far (0 … 1); may be null.</param>
    /// <returns>The mesh and what was found on the way.</returns>
    /// <exception cref="StlFormatException">Not an STL file, no usable triangles, or beyond the limits.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public static StlFile Read(string path, CancellationToken ct = default, IProgress<double>? progress = null)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        return Read(stream, ct, progress);
    }

    /// <summary>Reads STL data from a seekable stream, from its current position to its end.</summary>
    /// <inheritdoc cref="Read(string, CancellationToken, IProgress{double}?)"/>
    /// <param name="stream">Seekable stream positioned at the start of the STL data.</param>
    /// <param name="ct">Cancels reading.</param>
    /// <param name="progress">Share of the data read so far (0 … 1); may be null.</param>
    /// <exception cref="ArgumentException">The stream cannot seek.</exception>
    public static StlFile Read(Stream stream, CancellationToken ct = default, IProgress<double>? progress = null)
    {
        if (!stream.CanSeek) throw new ArgumentException("The stream must be seekable.", nameof(stream));
        var start = stream.Position;
        var length = stream.Length - start;

        // The first bytes decide: binary if the announced count matches the size exactly, text if it starts with
        // "solid" and contains only text, otherwise binary with surplus bytes at the end or cut off.
        var probe = new byte[(int)Math.Min(length, 4096)];
        stream.ReadExactly(probe);
        stream.Position = start;

        uint announced = probe.Length >= BinaryHeader ? BinaryPrimitives.ReadUInt32LittleEndian(probe.AsSpan(80, 4)) : 0;
        var sizeMatches = probe.Length >= BinaryHeader && BinaryHeader + BinaryRecord * (long)announced == length;
        if (!sizeMatches && LooksLikeText(probe))
            return ReadAscii(stream, length, ct, progress);
        if (probe.Length < BinaryHeader)
            throw new StlFormatException(Tr.T("Das ist keine STL-Datei (zu kurz)."));
        if (announced == 0)
            throw new StlFormatException(Tr.T("Die STL-Datei enthält keine Dreiecke."));
        return ReadBinary(stream, length, announced, probe, ct, progress);
    }

    // ------------------------------------------------------------------ binary

    private static StlFile ReadBinary(Stream stream, long length, uint announced, byte[] probe, CancellationToken ct, IProgress<double>? progress)
    {
        var available = (length - BinaryHeader) / BinaryRecord;
        var count = Math.Min(announced, available);
        if (count > TriangleLimit)
            throw TooLarge(count);
        if (count == 0)
            throw new StlFormatException(Tr.T("Die STL-Datei ist beschädigt: Sie endet vor dem ersten Dreieck."));

        var header = PrintablePrefix(probe.AsSpan(0, 80));
        stream.Seek(BinaryHeader, SeekOrigin.Current);

        var n = (int)count;
        var welder = new VertexWelder(Math.Max(16, n / 2 + 16));
        var indices = new int[3 * n];
        var written = 0;
        int invalid = 0, degenerate = 0;

        const int batch = 8192;
        var buffer = ArrayPool<byte>.Shared.Rent(batch * BinaryRecord);
        try
        {
            var done = 0;
            var nextReport = 0;
            while (done < n)
            {
                ct.ThrowIfCancellationRequested();
                var take = Math.Min(batch, n - done);
                var span = buffer.AsSpan(0, take * BinaryRecord);
                stream.ReadExactly(span);
                for (var r = 0; r < take; r++)
                {
                    var rec = span.Slice(r * BinaryRecord + 12, 36);   // skip the stored normal
                    float x0 = F(rec, 0), y0 = F(rec, 4), z0 = F(rec, 8);
                    float x1 = F(rec, 12), y1 = F(rec, 16), z1 = F(rec, 20);
                    float x2 = F(rec, 24), y2 = F(rec, 28), z2 = F(rec, 32);
                    if (!Valid(x0) || !Valid(y0) || !Valid(z0) || !Valid(x1) || !Valid(y1) || !Valid(z1)
                        || !Valid(x2) || !Valid(y2) || !Valid(z2))
                    {
                        invalid++;
                        continue;
                    }
                    if (!AddTriangle(welder, indices, ref written, x0, y0, z0, x1, y1, z1, x2, y2, z2)) degenerate++;
                }
                done += take;
                if (progress is not null && done >= nextReport)
                {
                    progress.Report((double)done / n);
                    nextReport = done + Math.Max(batch, n / 100);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var mesh = Finish(welder, indices, written);
        return new StlFile(mesh, StlEncoding.Binary, header, announced, invalid, degenerate, announced > available);
    }

    private static float F(ReadOnlySpan<byte> s, int offset) => BinaryPrimitives.ReadSingleLittleEndian(s.Slice(offset, 4));

    // ------------------------------------------------------------------ text

    private static StlFile ReadAscii(Stream stream, long length, CancellationToken ct, IProgress<double>? progress)
    {
        // Text needs about 250 bytes per triangle; the size gives a fair first guess for the arrays.
        var guess = (int)Math.Clamp(length / 250, 16, TriangleLimit);
        var welder = new VertexWelder(guess / 2 + 16);
        var indices = new int[3 * guess];
        var written = 0;
        int invalid = 0, degenerate = 0;
        long triangles = 0;
        string header = "";

        Span<double> corners = stackalloc double[3 * MaxFacetCorners];
        var digits = 17;   // fewest significant digits of a coordinate seen so far
        var cornerCount = 0;
        var facetBad = false;

        var lines = new LineReader(stream);
        long nextReport = 0;
        var lineNo = 0;
        while (lines.Next(out var line))
        {
            if ((++lineNo & 0x3FFF) == 0)
            {
                ct.ThrowIfCancellationRequested();
                if (progress is not null && lines.Consumed >= nextReport)
                {
                    progress.Report(Math.Min(1, (double)lines.Consumed / Math.Max(1, length)));
                    nextReport = lines.Consumed + Math.Max(1 << 20, length / 100);
                }
            }

            line = Trim(line);
            if (line.IsEmpty) continue;
            var word = NextToken(ref line);

            if (Is(word, "vertex"))
            {
                if (cornerCount >= MaxFacetCorners)
                {
                    facetBad = true;
                    continue;
                }
                if (!Number(ref line, out var x, ref digits) || !Number(ref line, out var y, ref digits) || !Number(ref line, out var z, ref digits))
                {
                    facetBad = true;
                    continue;
                }
                corners[3 * cornerCount] = x;
                corners[3 * cornerCount + 1] = y;
                corners[3 * cornerCount + 2] = z;
                cornerCount++;
            }
            else if (Is(word, "endloop") || Is(word, "endfacet"))
            {
                Flush(corners, ref cornerCount, ref facetBad);
            }
            else if (Is(word, "solid") && lineNo == 1)
            {
                header = PrintablePrefix(line.Length > 80 ? line[..80] : line);
            }
        }
        Flush(corners, ref cornerCount, ref facetBad);

        if (triangles == 0)
            throw new StlFormatException(Tr.T("Die STL-Datei enthält keine Dreiecke."));
        var mesh = Finish(welder, indices, written);
        var resolution = Math.Max(FloatResolution, 0.5 * Math.Pow(10, 1 - digits));
        return new StlFile(mesh, StlEncoding.Ascii, header, triangles, invalid, degenerate, false, resolution);

        // One facet: normally three corners; more are split into a fan, fewer are skipped.
        void Flush(Span<double> c, ref int count, ref bool bad)
        {
            if (count == 0 && !bad) return;
            if (bad || count < 3)
            {
                invalid += Math.Max(1, count - 2);
                triangles += Math.Max(1, count - 2);
            }
            else
            {
                for (var k = 1; k + 1 < count; k++)
                {
                    if (++triangles > TriangleLimit) throw TooLarge(triangles);
                    if (written + 3 > indices.Length)
                        Array.Resize(ref indices, (int)Math.Min(3L * TriangleLimit, Math.Max(indices.Length + 3, indices.Length * 3L / 2)));
                    if (!AddTriangle(welder, indices, ref written,
                            (float)c[0], (float)c[1], (float)c[2],
                            (float)c[3 * k], (float)c[3 * k + 1], (float)c[3 * k + 2],
                            (float)c[3 * k + 3], (float)c[3 * k + 4], (float)c[3 * k + 5]))
                        degenerate++;
                }
            }
            count = 0;
            bad = false;
        }
    }

    /// <summary>Starts with "solid" (after optional spaces) and the probe holds only text characters.</summary>
    private static bool LooksLikeText(ReadOnlySpan<byte> probe)
    {
        var s = probe;
        while (!s.IsEmpty && s[0] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') s = s[1..];
        if (s.Length < 5 || !Ascii.EqualsIgnoreCase(s[..5], "solid"u8)) return false;
        foreach (var b in probe)
            if (b < 9 || b is > 13 and < 32 || b == 127) return false;   // control characters: binary
        return true;
    }

    private static bool Is(ReadOnlySpan<byte> word, string keyword) => Ascii.EqualsIgnoreCase(word, keyword);

    private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> s)
    {
        var a = 0;
        var b = s.Length;
        while (a < b && IsSpace(s[a])) a++;
        while (b > a && IsSpace(s[b - 1])) b--;
        return s[a..b];
    }

    private static bool IsSpace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\f' or (byte)'\v';

    private static ReadOnlySpan<byte> NextToken(ref ReadOnlySpan<byte> s)
    {
        var a = 0;
        while (a < s.Length && IsSpace(s[a])) a++;
        var b = a;
        while (b < s.Length && !IsSpace(s[b])) b++;
        var token = s[a..b];
        s = s[b..];
        return token;
    }

    private static bool Number(ref ReadOnlySpan<byte> s, out double value, ref int fewestDigits)
    {
        var token = NextToken(ref s);
        if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            || !double.IsFinite(value) || Math.Abs(value) > MaxCoordinate) return false;
        if (value != 0)
        {
            // Significant digits of the mantissa: leading zeros do not count, the exponent is not part of it
            var count = 0;
            var started = false;
            foreach (var b in token)
            {
                if (b is (byte)'e' or (byte)'E') break;
                if (b is < (byte)'0' or > (byte)'9') continue;
                if (b != (byte)'0') started = true;
                if (started) count++;
            }
            // Trailing zeros are digits the writer chose to print; a number like "12" may be exact, so at least six
            if (count > 0) fewestDigits = Math.Min(fewestDigits, Math.Max(count, 6));
        }
        return true;
    }

    // ------------------------------------------------------------------ shared

    private static bool Valid(float v) => float.IsFinite(v) && Math.Abs(v) <= MaxCoordinate;

    /// <summary>Adds a triangle unless two of its corners coincide; returns false for such a degenerate triangle.</summary>
    private static bool AddTriangle(VertexWelder welder, int[] indices, ref int written,
        float x0, float y0, float z0, float x1, float y1, float z1, float x2, float y2, float z2)
    {
        var a = welder.Add(x0, y0, z0);
        var b = welder.Add(x1, y1, z1);
        var c = welder.Add(x2, y2, z2);
        if (a == b || b == c || a == c) return false;
        indices[written] = a;
        indices[written + 1] = b;
        indices[written + 2] = c;
        written += 3;
        return true;
    }

    private static Mesh Finish(VertexWelder welder, int[] indices, int written)
    {
        if (written == 0)
            throw new StlFormatException(Tr.T("Die STL-Datei enthält keine gültigen Dreiecke."));
        if (written != indices.Length) Array.Resize(ref indices, written);
        return new Mesh(welder.TakePositions(), indices);
    }

    private static StlFormatException TooLarge(long triangles) =>
        new(Tr.T("Das Modell hat {0:N0} Dreiecke – angezeigt werden höchstens {1:N0}.", triangles, TriangleLimit));

    /// <summary>Printable ASCII from the start up to the first other byte, trimmed.</summary>
    private static string PrintablePrefix(ReadOnlySpan<byte> s)
    {
        var n = 0;
        while (n < s.Length && s[n] is >= 32 and < 127) n++;
        return Encoding.ASCII.GetString(s[..n]).Trim();
    }

    /// <summary>Splits a stream into lines without allocating per line; refuses lines longer than <see cref="MaxLineLength"/>.</summary>
    private sealed class LineReader(Stream stream)
    {
        private readonly byte[] _buffer = new byte[1 << 20];
        private int _start;
        private int _end;
        private bool _eof;

        /// <summary>Bytes handed out as lines so far.</summary>
        public long Consumed { get; private set; }

        public bool Next(out ReadOnlySpan<byte> line)
        {
            while (true)
            {
                var nl = _buffer.AsSpan(_start, _end - _start).IndexOf((byte)'\n');
                if (nl >= 0)
                {
                    line = _buffer.AsSpan(_start, nl);
                    _start += nl + 1;
                    Consumed += nl + 1;
                    if (nl > MaxLineLength) throw LineTooLong();
                    return true;
                }
                if (_end - _start > MaxLineLength) throw LineTooLong();
                if (_eof)
                {
                    if (_end == _start)
                    {
                        line = default;
                        return false;
                    }
                    line = _buffer.AsSpan(_start, _end - _start);
                    Consumed += _end - _start;
                    _start = _end;
                    return true;
                }
                // Move the unfinished line to the front and read on
                if (_start > 0)
                {
                    _buffer.AsSpan(_start, _end - _start).CopyTo(_buffer);
                    _end -= _start;
                    _start = 0;
                }
                var read = stream.Read(_buffer, _end, _buffer.Length - _end);
                if (read == 0) _eof = true;
                _end += read;
            }
        }

        private static StlFormatException LineTooLong() =>
            new(Tr.T("Das ist keine gültige STL-Datei: Eine Zeile ist länger als {0} Zeichen.", MaxLineLength));
    }
}

/// <summary>
/// Gives each distinct position one vertex index: open addressing with linear probing over the vertex array itself
/// (4 bytes per slot, table at most half full), so that millions of corners merge without a dictionary per corner.
/// </summary>
/// <remarks>
/// Positions compare bit for bit after turning −0 into +0: STL writers repeat the very same floats for shared corners.
/// The hash is seeded per process at random, so a file cannot be built to collide (hash flooding).
/// </remarks>
internal sealed class VertexWelder
{
    private static readonly uint Seed = (uint)Random.Shared.Next() | 1u;

    private float[] _positions;
    private int[] _slots;
    private int _mask;

    public VertexWelder(int expectedVertices)
    {
        var capacity = (int)Math.Min(StlReader.MaxVertices, Math.Max(16, expectedVertices));
        _positions = new float[3 * capacity];
        var slots = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)(2 * capacity));
        _slots = new int[slots];
        Array.Fill(_slots, -1);
        _mask = slots - 1;
    }

    public int Count { get; private set; }

    /// <summary>Index of the vertex at this position, created if new.</summary>
    /// <exception cref="StlFormatException">More than <see cref="StlReader.MaxVertices"/> distinct positions.</exception>
    public int Add(float x, float y, float z)
    {
        if (x == 0) x = 0;   // −0 → +0
        if (y == 0) y = 0;
        if (z == 0) z = 0;
        var i = (int)(Hash(x, y, z) & (uint)_mask);
        while (true)
        {
            var s = _slots[i];
            if (s < 0) break;
            if (_positions[3 * s] == x && _positions[3 * s + 1] == y && _positions[3 * s + 2] == z) return s;
            i = (i + 1) & _mask;
        }

        if (Count >= StlReader.MaxVertices)
            throw new StlFormatException(Tr.T("Das Modell hat mehr als {0:N0} einzelne Eckpunkte – das lässt sich nicht anzeigen.", StlReader.MaxVertices));
        var index = Count++;
        if (3 * Count > _positions.Length)
            Array.Resize(ref _positions, (int)Math.Min(3L * StlReader.MaxVertices, _positions.Length * 3L / 2 + 48));
        _positions[3 * index] = x;
        _positions[3 * index + 1] = y;
        _positions[3 * index + 2] = z;

        if (2L * Count > _slots.Length)
        {
            Grow();
        }
        else
        {
            _slots[i] = index;
        }
        return index;
    }

    /// <summary>The positions, exactly 3 × <see cref="Count"/> long; the welder must not be used afterwards.</summary>
    public float[] TakePositions()
    {
        var p = _positions;
        if (p.Length != 3 * Count) Array.Resize(ref p, 3 * Count);
        _positions = [];
        _slots = [];
        return p;
    }

    private void Grow()
    {
        var slots = new int[_slots.Length * 2];
        Array.Fill(slots, -1);
        var mask = slots.Length - 1;
        for (var v = 0; v < Count; v++)
        {
            var i = (int)(Hash(_positions[3 * v], _positions[3 * v + 1], _positions[3 * v + 2]) & (uint)mask);
            while (slots[i] >= 0) i = (i + 1) & mask;
            slots[i] = v;
        }
        _slots = slots;
        _mask = mask;
    }

    private static uint Hash(float x, float y, float z)
    {
        var h = (BitConverter.SingleToUInt32Bits(x) ^ Seed) * 0x9E3779B1u;
        h = (h ^ BitConverter.SingleToUInt32Bits(y)) * 0x85EBCA77u;
        h = (h ^ BitConverter.SingleToUInt32Bits(z)) * 0xC2B2AE3Du;
        h ^= h >> 15;
        h *= 0x2C1B3C6Du;
        h ^= h >> 13;
        return h;
    }
}
