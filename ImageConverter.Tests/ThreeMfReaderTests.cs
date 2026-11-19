using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using ImageConverter.Viewer3D.Geometry;
using static ImageConverter.Tests.StlTestModels;

namespace ImageConverter.Tests;

/// <summary>Reading 3MF files: objects, components with transforms, units, metadata, and damaged or hostile archives.</summary>
public class ThreeMfReaderTests
{
    // ------------------------------------------------------------------ building 3MF archives in memory

    /// <summary>Vertices and triangles of a model as the XML of a &lt;mesh&gt;.</summary>
    private static string MeshXml(IEnumerable<Tri> model, string decimals = "0.###")
    {
        var vertices = new List<Vector3>();
        var index = new Dictionary<Vector3, int>();
        var triangles = new StringBuilder();
        int Of(Vector3 v)
        {
            if (!index.TryGetValue(v, out var i)) { i = vertices.Count; vertices.Add(v); index[v] = i; }
            return i;
        }
        foreach (var t in model)
            triangles.Append(CultureInfo.InvariantCulture, $"<triangle v1=\"{Of(t.A)}\" v2=\"{Of(t.B)}\" v3=\"{Of(t.C)}\"/>");
        var sb = new StringBuilder("<mesh><vertices>");
        foreach (var v in vertices)
            sb.Append(CultureInfo.InvariantCulture, $"<vertex x=\"{v.X.ToString(decimals, CultureInfo.InvariantCulture)}\" y=\"{v.Y.ToString(decimals, CultureInfo.InvariantCulture)}\" z=\"{v.Z.ToString(decimals, CultureInfo.InvariantCulture)}\"/>");
        sb.Append("</vertices><triangles>").Append(triangles).Append("</triangles></mesh>");
        return sb.ToString();
    }

    private static string ModelXml(string resources, string build, string unit = "millimeter", string metadata = "") =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><model unit=\"{unit}\" xml:lang=\"en-US\" xmlns=\"http://schemas.microsoft.com/3dmanufacturing/core/2015/02\">"
        + metadata + "<resources>" + resources + "</resources><build>" + build + "</build></model>";

    private static byte[] Archive(string modelXml, bool withRels = true, string modelName = "3D/3dmodel.model")
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string content)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                w.Write(content);
            }
            Add("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"model\" ContentType=\"application/vnd.ms-package.3dmanufacturing-3dmodel+xml\"/></Types>");
            if (withRels)
                Add("_rels/.rels", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Target=\"/{modelName}\" Id=\"rel0\" Type=\"http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel\"/></Relationships>");
            Add(modelName, modelXml);
        }
        return ms.ToArray();
    }

    private static StlFile Read(byte[] archive) => ThreeMfReader.Read(new MemoryStream(archive));

    private const string ProductionNs = "http://schemas.microsoft.com/3dmanufacturing/production/2015/06";

    /// <summary>A model part with the production extension declared, as slicers write it.</summary>
    private static string ProductionModelXml(string resources, string build, string unit = "millimeter") =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><model unit=\"{unit}\" xml:lang=\"en-US\" xmlns=\"http://schemas.microsoft.com/3dmanufacturing/core/2015/02\" xmlns:p=\"{ProductionNs}\" requiredextensions=\"p\">"
        + "<resources>" + resources + "</resources><build>" + build + "</build></model>";

    /// <summary>An archive whose root part may refer to further model parts.</summary>
    private static byte[] Package(string rootXml, params (string Name, string Xml)[] parts)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string content)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                w.Write(content);
            }
            Add("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"model\" ContentType=\"application/vnd.ms-package.3dmanufacturing-3dmodel+xml\"/></Types>");
            Add("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Target=\"/3D/3dmodel.model\" Id=\"rel0\" Type=\"http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel\"/></Relationships>");
            Add("3D/3dmodel.model", rootXml);
            foreach (var (name, xml) in parts) Add(name, xml);
        }
        return ms.ToArray();
    }

    private static readonly List<Tri> Cube = Box(new Vector3(0, 0, 0), new Vector3(20, 10, 5));

    // ------------------------------------------------------------------ tests

    [Fact]
    public void Cube_SharesCorners_AndGivesTheFigures()
    {
        var file = Read(Archive(ModelXml($"<object id=\"1\" type=\"model\">{MeshXml(Cube)}</object>", "<item objectid=\"1\"/>",
            metadata: "<metadata name=\"Title\">Testwürfel</metadata>")));
        Assert.Equal(StlEncoding.ThreeMf, file.Encoding);
        Assert.Equal("Testwürfel", file.Header);
        Assert.Equal(12, file.FileTriangles);
        Assert.Equal(8, file.Mesh.VertexCount);
        Assert.Equal(12, file.Mesh.TriangleCount);
        Assert.Equal(new Vector3(20, 10, 5), file.Mesh.Bounds.Size);
        Assert.Equal(0, file.Invalid);
        Assert.Equal(0, file.Degenerate);

        var topo = MeshTopology.Build(file.Mesh);
        Assert.True(topo.IsClosed);
        Assert.Equal(1, topo.ShellCount);
        Assert.Equal(1000, topo.Volume, 6);
    }

    [Fact]
    public void Components_AreTransformed_AndAMirrorKeepsTheOutsideOut()
    {
        var unit = Box(new Vector3(0, 0, 0), new Vector3(10, 10, 10));
        var resources =
            $"<object id=\"1\" type=\"model\">{MeshXml(unit)}</object>"
            + "<object id=\"2\" type=\"model\"><components>"
            + "<component objectid=\"1\"/>"
            + "<component objectid=\"1\" transform=\"1 0 0 0 1 0 0 0 1 30 0 0\"/>"   // moved by 30 in x
            + "</components></object>";
        // The build mirrors the pair in x and lifts it by 5
        var build = "<item objectid=\"2\" transform=\"-1 0 0 0 1 0 0 0 1 0 0 5\"/>";
        var file = Read(Archive(ModelXml(resources, build)));

        Assert.Equal(24, file.Mesh.TriangleCount);
        Assert.Equal(new Vector3(-40, 0, 5), file.Mesh.Bounds.Min);
        Assert.Equal(new Vector3(0, 10, 15), file.Mesh.Bounds.Max);
        var topo = MeshTopology.Build(file.Mesh);
        Assert.True(topo.IsClosed);
        Assert.Equal(2, topo.ShellCount);
        Assert.Equal(2000, topo.Volume, 6);   // positive: still facing outwards after the mirror
    }

    [Fact]
    public void Units_AreConvertedToMillimetres()
    {
        var cm = Read(Archive(ModelXml($"<object id=\"1\">{MeshXml(Box(new Vector3(1, 1, 1), new Vector3(2, 3, 4)))}</object>", "<item objectid=\"1\"/>", unit: "centimeter")));
        Assert.Equal(new Vector3(10, 20, 30), cm.Mesh.Bounds.Size);
        Assert.Equal(new Vector3(10, 10, 10), cm.Mesh.Bounds.Min);
        var inch = Read(Archive(ModelXml($"<object id=\"1\">{MeshXml(Box(new Vector3(0, 0, 0), new Vector3(1, 1, 1)))}</object>", "<item objectid=\"1\"/>", unit: "inch")));
        Assert.Equal(25.4f, inch.Mesh.Bounds.Size.X, 4);
    }

    [Fact]
    public void SupportObjects_AreNotPartOfTheModel()
    {
        var resources = $"<object id=\"1\" type=\"model\">{MeshXml(Cube)}</object>"
            + $"<object id=\"2\" type=\"support\">{MeshXml(Box(new Vector3(100, 100, 100), new Vector3(110, 110, 110)))}</object>";
        var file = Read(Archive(ModelXml(resources, "<item objectid=\"1\"/><item objectid=\"2\"/>")));
        Assert.Equal(12, file.Mesh.TriangleCount);
        Assert.Equal(new Vector3(20, 10, 5), file.Mesh.Bounds.Max);
    }

    [Fact]
    public void Resolution_FollowsTheDigitsOfTheFile()
    {
        // Coordinates 1 … 6 with three decimals have four significant digits (counted as six), with seven decimals eight
        var box = Box(new Vector3(1, 2, 3), new Vector3(4, 5, 6));
        var three = Read(Archive(ModelXml($"<object id=\"1\">{MeshXml(box, "0.000")}</object>", "<item objectid=\"1\"/>")));
        var seven = Read(Archive(ModelXml($"<object id=\"1\">{MeshXml(box, "0.0000000")}</object>", "<item objectid=\"1\"/>")));
        Assert.Equal(0.5 * Math.Pow(10, 1 - 6), three.Resolution, 12);
        Assert.Equal(StlReader.FloatResolution, seven.Resolution, 12);   // finer than a float: the float decides
    }

    [Fact]
    public void ModelReader_PicksTheReaderByExtension()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ic-3mf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "teil.3mf");
            File.WriteAllBytes(path, Archive(ModelXml($"<object id=\"1\">{MeshXml(Cube)}</object>", "<item objectid=\"1\"/>")));
            var file = ModelReader.Read(path);
            Assert.Equal(StlEncoding.ThreeMf, file.Encoding);
            Assert.Equal(12, file.Mesh.TriangleCount);
            Assert.Contains(".3mf", ModelReader.Extensions);
            Assert.Contains(".stl", ModelReader.Extensions);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void BadIndices_AreSkippedAndCounted()
    {
        var mesh = MeshXml(Cube).Replace("</triangles>", "<triangle v1=\"0\" v2=\"1\" v3=\"99\"/><triangle v1=\"2\" v2=\"2\" v3=\"3\"/></triangles>");
        var file = Read(Archive(ModelXml($"<object id=\"1\">{mesh}</object>", "<item objectid=\"1\"/>")));
        Assert.Equal(12, file.Mesh.TriangleCount);
        Assert.Equal(1, file.Invalid);      // index 99 does not exist
        Assert.Equal(1, file.Degenerate);   // two corners the same
        Assert.Equal(14, file.FileTriangles);   // placed triangles, including the two skipped ones
    }

    [Fact]
    public void ProductionExtension_ObjectsInOtherParts_ArePlaced()
    {
        // As a slicer saves a project: the root part holds only a component with p:path and the build item; the mesh
        // lies in its own part. The same ID 1 exists in both parts and names different objects. Inside the referenced
        // part, object 1 builds in object 2 of that part (no p:path). A second reference spells the part name in other
        // letter case and with percent-encoding; it is the same part.
        var unit = Box(new Vector3(0, 0, 0), new Vector3(10, 10, 10));
        var root = ProductionModelXml(
            "<object id=\"1\" type=\"model\"><components>"
            + "<component p:path=\"/3D/Objects/teil%201.model\" objectid=\"1\" transform=\"1 0 0 0 1 0 0 0 1 0 0 0\"/>"
            + "<component p:path=\"/3d/objects/TEIL 1.model\" objectid=\"1\" transform=\"1 0 0 0 1 0 0 0 1 30 0 0\"/>"
            + "</components></object>",
            "<item objectid=\"1\" transform=\"0.5 0 0 0 0.5 0 0 0 0.5 100 0 0\"/>");
        var part = ProductionModelXml(
            "<object id=\"1\" type=\"model\"><components><component objectid=\"2\" transform=\"1 0 0 0 1 0 0 0 1 0 0 4\"/></components></object>"
            + $"<object id=\"2\" type=\"model\">{MeshXml(unit)}</object>", "");

        var file = Read(Package(root, ("3D/Objects/teil 1.model", part)));

        Assert.Equal(24, file.Mesh.TriangleCount);
        Assert.Equal(24, file.FileTriangles);
        // x: (0 … 10 and 30 … 40) · 0.5 + 100; z: (0 … 10 + 4) · 0.5
        Assert.Equal(new Vector3(100, 0, 2), file.Mesh.Bounds.Min);
        Assert.Equal(new Vector3(120, 5, 7), file.Mesh.Bounds.Max);
        var topo = MeshTopology.Build(file.Mesh);
        Assert.Equal(2, topo.ShellCount);
        Assert.Equal(2 * 125, topo.Volume, 6);
    }

    [Fact]
    public void ProductionExtension_PartInAnotherUnit_IsConverted()
    {
        var root = ProductionModelXml(
            "<object id=\"5\"><components><component p:path=\"/3D/Objects/cm.model\" objectid=\"1\"/></components></object>",
            "<item objectid=\"5\"/>");
        var part = ProductionModelXml($"<object id=\"1\">{MeshXml(Box(new Vector3(0, 0, 0), new Vector3(1, 2, 3)))}</object>", "", unit: "centimeter");
        var file = Read(Package(root, ("3D/Objects/cm.model", part)));
        Assert.Equal(new Vector3(10, 20, 30), file.Mesh.Bounds.Size);
    }

    [Fact]
    public void ProductionExtension_MissingPart_NamesThePart()
    {
        var root = ProductionModelXml(
            "<object id=\"2\"><components><component p:path=\"/3D/Objects/object_1.model\" objectid=\"1\"/></components></object>",
            "<item objectid=\"2\"/>");
        var e = Assert.Throws<StlFormatException>(() => Read(Package(root)));
        Assert.Contains("3D/Objects/object_1.model", e.Message);
    }

    [Fact]
    public void Damaged_Files_GiveAMessage_NotACrash()
    {
        Assert.Throws<StlFormatException>(() => Read(Encoding.ASCII.GetBytes("solid not a zip at all")));
        Assert.Throws<StlFormatException>(() => Read(Archive(ModelXml("", ""), withRels: false, modelName: "3D/other.txt")));   // no model part
        Assert.Throws<StlFormatException>(() => Read(Archive(ModelXml($"<object id=\"1\">{MeshXml(Cube)}</object>", ""))));   // nothing built
        Assert.Throws<StlFormatException>(() => Read(Archive(ModelXml("<object id=\"1\"><components><component objectid=\"2\"/></components></object><object id=\"2\"><components><component objectid=\"1\"/></components></object>", "<item objectid=\"1\"/>"))));   // cycle
        Assert.Throws<StlFormatException>(() => Read(Archive("<model><resources><object id=\"1\"><mesh><vertices><vertex x=\"0\" y=\"0\"")));   // cut off XML
        Assert.Throws<StlFormatException>(() => Read(Archive(ModelXml("<object id=\"1\"><mesh><vertices><vertex x=\"1e400\" y=\"0\" z=\"0\"/><vertex x=\"0\" y=\"1\" z=\"0\"/><vertex x=\"0\" y=\"0\" z=\"1\"/></vertices><triangles><triangle v1=\"0\" v2=\"1\" v3=\"2\"/></triangles></mesh></object>", "<item objectid=\"1\"/>"))));   // only invalid coordinates
        // A DTD must not be processed (entity expansion, external references)
        Assert.Throws<StlFormatException>(() => Read(Archive("<?xml version=\"1.0\"?><!DOCTYPE model [<!ENTITY a \"aaaa\">]><model><resources/><build/></model>")));
    }
}
