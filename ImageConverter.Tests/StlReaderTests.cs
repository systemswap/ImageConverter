using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Text;
using ImageConverter.Core.Localization;
using ImageConverter.Viewer3D.Geometry;
using static ImageConverter.Tests.StlTestModels;

namespace ImageConverter.Tests;

/// <summary>Reading STL files: both encodings, shared corners, figures, and damaged or hostile files.</summary>
public class StlReaderTests
{
    private static readonly List<Tri> Cube = Box(new Vector3(0, 0, 0), new Vector3(20, 10, 5));

    [Fact]
    public void Binary_SharesCorners_AndGivesTheFigures()
    {
        var file = Read(Binary(Cube, "made by a test"));
        Assert.Equal(StlEncoding.Binary, file.Encoding);
        Assert.Equal("made by a test", file.Header);
        Assert.Equal(8, file.Mesh.VertexCount);
        Assert.Equal(12, file.Mesh.TriangleCount);
        Assert.Equal(new Vector3(20, 10, 5), file.Mesh.Bounds.Size);

        var topo = MeshTopology.Build(file.Mesh);
        Assert.True(topo.IsClosed);
        Assert.Equal(1, topo.ShellCount);
        Assert.Equal(1000, topo.Volume, 6);
        Assert.Equal(2 * (200 + 100 + 50), topo.Area, 6);
    }

    [Fact]
    public void Ascii_GivesTheSameMeshAsBinary()
    {
        var model = Extrude(Circle(new Vector2(3, 4), 7.25f, 37), 12.5f);
        var bin = Read(Binary(model)).Mesh;
        var txt = Read(Ascii(model, "part one"));
        Assert.Equal(StlEncoding.Ascii, txt.Encoding);
        Assert.Equal("part one", txt.Header);
        Assert.Equal(bin.Positions, txt.Mesh.Positions);
        Assert.Equal(bin.Indices, txt.Mesh.Indices);
    }

    /// <summary>Some programs write binary files whose header starts with "solid"; the size decides.</summary>
    [Fact]
    public void BinaryWithSolidHeader_IsReadAsBinary()
    {
        var file = Read(Binary(Cube, "solid cube exported"));
        Assert.Equal(StlEncoding.Binary, file.Encoding);
        Assert.Equal(12, file.Mesh.TriangleCount);
    }

    [Fact]
    public void CutOffBinary_ReadsWhatIsThere_AndSaysSo()
    {
        var bytes = Binary(Cube);
        var file = Read(bytes[..(84 + 50 * 7 + 20)]);
        Assert.True(file.Truncated);
        Assert.Equal(7, file.Mesh.TriangleCount);
        Assert.Equal(12, file.FileTriangles);
    }

    [Fact]
    public void SurplusBytesAfterTheTriangles_AreIgnored()
    {
        var bytes = Binary(Cube).Concat(new byte[37]).ToArray();
        var file = Read(bytes);
        Assert.False(file.Truncated);
        Assert.Equal(12, file.Mesh.TriangleCount);
    }

    [Fact]
    public void InvalidNumbers_AreSkipped_AndCounted()
    {
        var bytes = Binary(Cube);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(84 + 50 * 3 + 12), float.NaN);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(84 + 50 * 5 + 28), float.PositiveInfinity);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(84 + 50 * 6 + 40), 3e10f);
        var file = Read(bytes);
        Assert.Equal(3, file.Invalid);
        Assert.Equal(9, file.Mesh.TriangleCount);
    }

    [Fact]
    public void DegenerateTriangles_AreDropped()
    {
        var model = new List<Tri>(Cube) { new(new Vector3(1, 1, 1), new Vector3(1, 1, 1), new Vector3(2, 2, 2)) };
        var file = Read(Binary(model));
        Assert.Equal(1, file.Degenerate);
        Assert.Equal(12, file.Mesh.TriangleCount);
    }

    [Fact]
    public void AsciiFacetWithFourCorners_IsSplitIntoTwoTriangles()
    {
        var text = "solid q\nfacet normal 0 0 1\nouter loop\nvertex 0 0 0\nvertex 1 0 0\nvertex 1 1 0\nvertex 0 1 0\nendloop\nendfacet\nendsolid\n";
        var file = Read(Encoding.ASCII.GetBytes(text));
        Assert.Equal(2, file.Mesh.TriangleCount);
        Assert.Equal(4, file.Mesh.VertexCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("solid nothing\nendsolid\n")]
    public void NoTriangles_IsAClearError(string content) =>
        Assert.Throws<StlFormatException>(() => Read(Encoding.ASCII.GetBytes(content)));

    [Fact]
    public void LongLine_IsRefusedWithoutReadingOn()
    {
        var text = "solid x\nvertex " + new string('1', StlReader.MaxLineLength + 10) + "\n";
        Assert.Throws<StlFormatException>(() => Read(Encoding.ASCII.GetBytes(text)));
    }

    [Fact]
    public void BinaryWithoutAnyTriangle_IsAnError()
    {
        var bytes = new byte[84];
        Assert.Throws<StlFormatException>(() => Read(bytes));
    }

    /// <summary>A file that announces more than the limit is refused before anything is allocated or read.</summary>
    [Fact]
    public void TooManyTriangles_IsRefusedBeforeReading()
    {
        const long count = StlReader.MaxTriangles + 1L;
        using var stream = new HugeStlStream(count);
        var watch = Stopwatch.StartNew();
        var e = Assert.Throws<StlFormatException>(() => StlReader.Read(stream));
        // formatted like the message (language of the program), not by the culture of the computer the tests run on
        Assert.Contains(Tr.T("{0:N0}", StlReader.TriangleLimit), e.Message);
        Assert.True(watch.ElapsedMilliseconds < 500);
        Assert.True(stream.BytesRead < 10_000);
    }

    [Fact]
    public void Cancelling_StopsReading()
    {
        var bytes = Binary(Sphere(Vector3.Zero, 10, 200, 100));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => StlReader.Read(new MemoryStream(bytes), cts.Token));
    }

    /// <summary>
    /// Corners at the same spot share one vertex even when they differ only in the sign of zero, and the vertex table
    /// handles many distinct corners (it grows several times on the way).
    /// </summary>
    [Fact]
    public void Welder_MergesSignedZero_AndGrows()
    {
        var w = new VertexWelder(4);
        Assert.Equal(w.Add(0f, 1f, 2f), w.Add(-0f, 1f, 2f));
        for (var i = 0; i < 100_000; i++) w.Add(i, i * 0.5f, -i);
        Assert.Equal(100_001, w.Count);
        Assert.Equal(5, w.Add(4, 2, -4));
    }

    /// <summary>Random damage to a valid file never ends in anything but a mesh or a clear error, and never hangs.</summary>
    [Fact]
    public void DamagedFiles_GiveAMeshOrAClearError()
    {
        var rng = new Random(7);
        var sources = new[] { Binary(Cube), Ascii(Cube), Binary(Tube(2, 3, 4, 12)) };
        var watch = Stopwatch.StartNew();
        for (var round = 0; round < 3000; round++)
        {
            var bytes = (byte[])sources[round % sources.Length].Clone();
            var changes = rng.Next(1, 20);
            for (var c = 0; c < changes; c++) bytes[rng.Next(bytes.Length)] = (byte)rng.Next(256);
            if (rng.Next(4) == 0) bytes = bytes[..rng.Next(bytes.Length)];
            try
            {
                var file = Read(bytes);
                Assert.True(file.Mesh.TriangleCount > 0);
                var topo = MeshTopology.Build(file.Mesh);
                FaceFinder.Find(file.Mesh, topo);
            }
            catch (StlFormatException)
            {
                // refused with a message: fine
            }
        }
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}");
    }

    /// <summary>Stream that pretends to be a binary STL file of the given triangle count (header only, zeros after it).</summary>
    private sealed class HugeStlStream(long triangles) : Stream
    {
        private long _position;

        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => 84 + 50 * triangles;

        public override long Position
        {
            get => _position;
            set => _position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, Length - _position);
            for (var i = 0; i < n; i++)
            {
                var p = _position + i;
                buffer[offset + i] = p is >= 80 and < 84 ? (byte)(triangles >> (int)(8 * (p - 80))) : (byte)0;
            }
            _position += n;
            BytesRead += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => Length + offset,
        };

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
