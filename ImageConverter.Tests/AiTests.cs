using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using ImageConverter.Core.Ai;
using ImageMagick;

namespace ImageConverter.Tests;

public class AiTests
{
    /// <summary>Ersatzmodell: vergrößert jedes Pixel auf scale × scale (nächster Nachbar).</summary>
    private sealed class NearestModel(int scale, int tile) : ITileModel
    {
        public int Scale => scale;
        public int TileSize => tile;
        public int Calls { get; private set; }

        public float[] Run(float[] input, int width, int height, CancellationToken ct)
        {
            Calls++;
            int ow = width * scale, oh = height * scale;
            var output = new float[3 * ow * oh];
            for (var c = 0; c < 3; c++)
            for (var y = 0; y < oh; y++)
            for (var x = 0; x < ow; x++)
                output[c * ow * oh + y * ow + x] = input[c * width * height + y / scale * width + x / scale];
            return output;
        }
    }

    /// <summary>Ersatzmodell, das alles schwarz macht — zeigt, ob Stärke und Alpha stimmen.</summary>
    private sealed class BlackModel(int scale, int tile) : ITileModel
    {
        public int Scale => scale;
        public int TileSize => tile;
        public float[] Run(float[] input, int width, int height, CancellationToken ct) => new float[3 * width * scale * height * scale];
    }

    private static MagickImage Noise(int w, int h, bool alpha = false)
    {
        var rng = new Random(7);
        var bytes = new byte[w * h * 4];
        rng.NextBytes(bytes);
        if (!alpha) for (var i = 3; i < bytes.Length; i += 4) bytes[i] = 255;
        var img = new MagickImage();
        img.ReadPixels(bytes, new PixelReadSettings((uint)w, (uint)h, StorageType.Char, "RGBA"));
        if (!alpha) img.Alpha(AlphaOption.Off);
        return img;
    }

    // ------------------------------------------------------------ Kacheln

    [Theory]
    [InlineData(300, 170, 1)]
    [InlineData(97, 211, 2)]
    [InlineData(64, 64, 4)]
    public void Tiles_Seamless_NearestModelReproducesImage(int w, int h, int factor)
    {
        using var src = Noise(w, h);
        var model = new NearestModel(4, 48);

        using var result = AiEnhancer.Enhance(src, model, factor, 1, null, default);

        Assert.Equal((uint)(w * factor), result.Width);
        Assert.Equal((uint)(h * factor), result.Height);
        Assert.True(model.Calls > 1);

        var a = AiEnhancer.ReadPixels(src, "RGB");
        var b = AiEnhancer.ReadPixels(result, "RGB");
        for (var y = 0; y < h * factor; y++)
        for (var x = 0; x < w * factor; x++)
        for (var c = 0; c < 3; c++)
        {
            var expected = a[((y / factor) * w + x / factor) * 3 + c];
            var actual = b[(y * w * factor + x) * 3 + c];
            Assert.InRange(actual - expected, -1, 1);   // nur Rundung
        }
    }

    [Fact]
    public void Strength_Zero_KeepsImage_AndAlphaStays()
    {
        using var src = Noise(80, 60, alpha: true);
        using var result = AiEnhancer.Enhance(src, new BlackModel(4, 32), 1, 0, null, default);

        Assert.True(result.HasAlpha);
        Assert.Equal(AiEnhancer.ReadPixels(src, "RGBA"), AiEnhancer.ReadPixels(result, "RGBA"));
    }

    [Fact]
    public void Strength_Half_MixesWithOriginal()
    {
        using var src = new MagickImage(new MagickColor("#FFFFFF"), 40, 40);
        using var result = AiEnhancer.Enhance(src, new BlackModel(4, 32), 1, 0.5, null, default);

        var px = AiEnhancer.ReadPixels(result, "RGB");
        Assert.InRange(px[0], 32700, 32800);
    }

    [Fact]
    public void Cancel_StopsBetweenTiles()
    {
        using var src = Noise(200, 200);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            AiEnhancer.Enhance(src, new NearestModel(4, 32), 1, 1, null, cts.Token));
    }

    [Fact]
    public void Factors_LimitedByModelAndSize()
    {
        Assert.Equal([1, 2, 4], AiEnhancer.PossibleFactors(4, 1000, 800));
        Assert.Equal([1, 2], AiEnhancer.PossibleFactors(4, 4000, 3000));   // 4× wären 192 MP
    }

    // ------------------------------------------------------------ Gesichter

    [Fact]
    public void Similarity_RecoversTransform()
    {
        var truth = new Similarity(0.8, 0.3, 12, -5);
        (double X, double Y)[] from = [(10, 20), (50, 22), (30, 45), (15, 70), (48, 69)];
        var to = from.Select(p => truth.Apply(p.X, p.Y)).ToArray();

        var est = Similarity.Estimate(from, to);
        Assert.Equal(truth.A, est.A, 6);
        Assert.Equal(truth.B, est.B, 6);
        Assert.Equal(truth.Tx, est.Tx, 6);
        Assert.Equal(truth.Ty, est.Ty, 6);

        var (x, y) = est.Inverse().Apply(to[2].X, to[2].Y);
        Assert.Equal(30, x, 6);
        Assert.Equal(45, y, 6);
    }

    /// <summary>Gesichtsmodell, das nichts ändert — Ausschneiden und Zurücksetzen dürfen kaum Spuren hinterlassen.</summary>
    private sealed class IdentityFace : IFaceModel
    {
        public int Size => 128;
        public float[] Run(float[] input, CancellationToken ct) => input;
    }

    [Fact]
    public void FaceRoundTrip_WithIdentityModel_LeavesImageAlmostUnchanged()
    {
        // weicher Verlauf: bilineares Hin und Zurück ist dort fast verlustfrei
        using var src = new MagickImage("gradient:#203040-#E0C0A0", 300, 260);
        src.Rotate(20);
        src.Crop(new MagickGeometry(20, 20, 300, 260));
        src.ResetPage();

        var face = new DetectedFace(100, 80, 200, 200, 0.9, [(130, 120), (170, 121), (150, 145), (135, 170), (166, 169)]);
        using var result = AiFaces.Restore(src, [face], new IdentityFace(), 1, null, default);

        var a = AiEnhancer.ReadPixels(src, "RGB");
        var b = AiEnhancer.ReadPixels(result, "RGB");
        var maxDiff = a.Zip(b, (p, q) => Math.Abs(p - q)).Max();
        Assert.True(maxDiff < 65535 * 0.02, $"Abweichung {maxDiff / 655.35:0.0} %");
    }

    /// <summary>Gesichtsmodell, das glättet (Mittel über 9 × 9) — wie ein Modell, das Haut glattbügelt.</summary>
    private sealed class SmoothFace : IFaceModel
    {
        public int Size => 128;

        public float[] Run(float[] input, CancellationToken ct)
        {
            var n = Size * Size;
            var output = new float[input.Length];
            for (var c = 0; c < 3; c++)
            for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                float sum = 0;
                var count = 0;
                for (var dy = -4; dy <= 4; dy++)
                for (var dx = -4; dx <= 4; dx++)
                {
                    int yy = Math.Clamp(y + dy, 0, Size - 1), xx = Math.Clamp(x + dx, 0, Size - 1);
                    sum += input[c * n + yy * Size + xx];
                    count++;
                }
                output[c * n + y * Size + x] = sum / count;
            }
            return output;
        }
    }

    /// <summary>Mittlere Abweichung zum Original in der Gesichtsmitte (0…1).</summary>
    private static double FaceDistance(MagickImage src, MagickImage result)
    {
        var a = AiEnhancer.ReadPixels(src, "RGB");
        var b = AiEnhancer.ReadPixels(result, "RGB");
        double sum = 0;
        var n = 0;
        for (var y = 130; y < 170; y++)
        for (var x = 130; x < 170; x++)
        for (var c = 0; c < 3; c++, n++)
        {
            var i = (y * (int)src.Width + x) * 3 + c;
            sum += Math.Abs(a[i] - b[i]) / 65535.0;
        }
        return sum / n;
    }

    [Fact]
    public void FaceRun_Texture_BringsFineDetailBack_AndZeroMatchesRestore()
    {
        using var src = Noise(300, 300);
        var face = new DetectedFace(100, 100, 100, 100, 0.9, [(130, 130), (170, 130), (150, 150), (135, 172), (165, 172)]);
        var run = AiFaces.Run(src, [face], new SmoothFace(), null, default);

        using var smooth = run.Compose(1, 0);
        using var textured = run.Compose(1, 1);
        using var restored = AiFaces.Restore(src, [face], new SmoothFace(), 1, null, default);

        Assert.Equal(AiEnhancer.ReadPixels(restored, "RGB"), AiEnhancer.ReadPixels(smooth, "RGB"));
        var (dSmooth, dTextured) = (FaceDistance(src, smooth), FaceDistance(src, textured));
        Assert.True(dTextured < dSmooth * 0.6, $"glatt {dSmooth:0.000}, mit Struktur {dTextured:0.000}");

        // Vorschau: verkleinert, aber dieselbe Richtung
        var preview = run.Preview(1, 1, maxEdge: 150);
        Assert.Equal(150, preview.Width);
        Assert.Equal(150 * 150 * 4, preview.Bgra.Length);
    }

    [Fact]
    public void EnhanceRun_StrengthLive_MatchesEnhance()
    {
        using var src = Noise(90, 70);
        var run = AiEnhancer.Run(src, new BlackModel(4, 32), 1, null, default);

        using var half = run.Compose(0.5, 0);
        using var expected = AiEnhancer.Enhance(src, new BlackModel(4, 32), 1, 0.5, null, default);
        Assert.Equal(AiEnhancer.ReadPixels(expected, "RGB"), AiEnhancer.ReadPixels(half, "RGB"));

        // Stärke 0 in der Vorschau = Original, Stärke 1 = schwarz
        Assert.Contains(run.Preview(0, 0).Bgra.Where((_, i) => i % 4 != 3), v => v > 0);
        Assert.DoesNotContain(run.Preview(1, 0).Bgra.Where((_, i) => i % 4 != 3), v => v > 0);
    }

    [Fact]
    public void Nms_KeepsBestOfOverlappingFaces()
    {
        (double, double)[] lm = [(0, 0), (0, 0), (0, 0), (0, 0), (0, 0)];
        var faces = new List<DetectedFace>
        {
            new(0, 0, 100, 100, 0.7, lm),
            new(5, 5, 105, 105, 0.9, lm),
            new(300, 300, 380, 380, 0.6, lm),
        };

        var kept = AiFaces.Nms(faces, 0.4);
        Assert.Equal(2, kept.Count);
        Assert.Equal(0.9, kept[0].Score);
    }

    [Fact]
    public void YuNetDecode_AnchorAndScale()
    {
        // ein Treffer im 8er-Raster, Zelle (x=10, y=5) → Anker (80, 40)
        const int size = 640;
        float[][] outputs = new float[12][];
        int[] strides = [8, 16, 32];
        for (var l = 0; l < 3; l++)
        {
            var n = (size / strides[l]) * (size / strides[l]);
            outputs[l] = new float[n];
            outputs[l + 3] = new float[n];
            outputs[l + 6] = new float[n * 4];
            outputs[l + 9] = new float[n * 10];
        }

        var i = 5 * 80 + 10;
        outputs[0][i] = 0.9f;
        outputs[3][i] = 0.9f;
        outputs[6][i * 4 + 2] = (float)Math.Log(4);   // Breite 4 × 8 = 32
        outputs[6][i * 4 + 3] = (float)Math.Log(5);   // Höhe 40
        outputs[9][i * 10] = 1;                      // erstes Auge: Anker + 8

        var faces = AiFaces.Decode(outputs, size, 0.5, ratio: 2);
        var f = Assert.Single(faces);
        Assert.Equal((80 - 16) * 2, f.X1, 3);
        Assert.Equal((40 - 20) * 2, f.Y1, 3);
        Assert.Equal(32 * 2, f.Width, 3);
        Assert.Equal((80 + 8) * 2, f.Landmarks[0].X, 3);
    }

    // ------------------------------------------------------------ Download

    private sealed class FakeHandler(byte[] payload, int failAfterBytes = -1) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Stream body = new MemoryStream(payload);
            if (failAfterBytes >= 0) body = new CancelingStream(payload, failAfterBytes);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
        }
    }

    /// <summary>Liefert ein Stück und wirft dann — wie ein abgebrochener Download.</summary>
    private sealed class CancelingStream(byte[] data, int after) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (Position >= after) throw new OperationCanceledException();
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, after - (int)Position)], ct);
        }
    }

    private static AiModel FakeModel(byte[] payload, bool zip = false) => new(
        "test", "Test", "", "", AiModelKind.Enhance, "https://example.invalid/model", payload.Length,
        Convert.ToHexStringLower(SHA256.HashData(payload)), zip, "model.onnx", "MIT", false, "");

    private static string TempFolder() => Path.Combine(Path.GetTempPath(), "ic-ai-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Download_VerifiesAndStores()
    {
        var payload = RandomNumberGenerator.GetBytes(200_000);
        var store = new AiModelStore([TempFolder()]);
        var model = FakeModel(payload);
        var reported = new List<double>();

        try
        {
            await store.DownloadAsync(model, new HttpClient(new FakeHandler(payload)), new SyncProgress(reported), default);

            Assert.True(store.IsInstalled(model));
            Assert.Equal(payload, await File.ReadAllBytesAsync(store.PathOf(model)!));
            Assert.Equal(1, reported[^1], 6);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(store.PathOf(model)!)!, "*.part"));
        }
        finally { Directory.Delete(store.Folders[0], true); }
    }

    [Fact]
    public async Task Download_WrongChecksum_LeavesNothing()
    {
        var payload = RandomNumberGenerator.GetBytes(10_000);
        var store = new AiModelStore([TempFolder()]);
        var model = FakeModel(payload) with { Sha256 = new string('0', 64) };

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.DownloadAsync(model, new HttpClient(new FakeHandler(payload)), null, default));
            Assert.False(store.IsInstalled(model));
            Assert.False(Directory.Exists(Path.Combine(store.Folders[0], model.Id)));
        }
        finally { if (Directory.Exists(store.Folders[0])) Directory.Delete(store.Folders[0], true); }
    }

    [Fact]
    public async Task Download_Cancelled_LeavesNothing()
    {
        var payload = RandomNumberGenerator.GetBytes(300_000);
        var store = new AiModelStore([TempFolder()]);
        var model = FakeModel(payload);

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.DownloadAsync(model, new HttpClient(new FakeHandler(payload, failAfterBytes: 100_000)), null, default));
            Assert.False(store.IsInstalled(model));
            Assert.False(Directory.Exists(Path.Combine(store.Folders[0], model.Id)));
        }
        finally { if (Directory.Exists(store.Folders[0])) Directory.Delete(store.Folders[0], true); }
    }

    [Fact]
    public async Task Download_Zip_ExtractsFlat()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            await using (var s = zip.CreateEntry("sub/model.onnx").Open()) s.Write([1, 2, 3]);
            await using (var s = zip.CreateEntry("sub/model.data").Open()) s.Write([4, 5]);
            await using (var s = zip.CreateEntry("../böse.txt").Open()) s.Write([6]);
        }

        var payload = ms.ToArray();
        var store = new AiModelStore([TempFolder()]);
        var model = FakeModel(payload, zip: true);

        try
        {
            await store.DownloadAsync(model, new HttpClient(new FakeHandler(payload)), null, default);

            var dir = Path.Combine(store.Folders[0], model.Id);
            Assert.True(store.IsInstalled(model));
            Assert.True(File.Exists(Path.Combine(dir, "model.data")));
            Assert.True(File.Exists(Path.Combine(dir, "böse.txt")));            // im Modellordner, nicht darüber
            Assert.False(File.Exists(Path.Combine(store.Folders[0], "böse.txt")));
            Assert.Empty(Directory.GetFiles(dir, "*.zip*"));
        }
        finally { Directory.Delete(store.Folders[0], true); }
    }

    [Fact]
    public void Store_FindsModelInAnyFolder()
    {
        string first = TempFolder(), second = TempFolder();
        var model = AiModelCatalog.Allround;
        try
        {
            Directory.CreateDirectory(Path.Combine(second, model.Id));
            File.WriteAllBytes(Path.Combine(second, model.Id, model.MainFile), [0]);

            var store = new AiModelStore([first, second]);
            Assert.True(store.IsInstalled(model));
            Assert.False(store.IsInstalled(AiModelCatalog.Gfpgan));
        }
        finally { Directory.Delete(second, true); }
    }

    private sealed class SyncProgress(List<double> values) : IProgress<double>
    {
        public void Report(double value) => values.Add(value);
    }
}
