using ImageConverter.Core.Imaging;
using Microsoft.ML.OnnxRuntime;

namespace ImageConverter.Core.Ai;

/// <summary>
/// Tiefenkarte mit Depth Anything V2 (ONNX): Eingang RGB, lange Seite 518, beide Seiten ein Vielfaches von 14,
/// normiert mit Mittelwert und Streuung von ImageNet; Ausgang relative Nähe (größer = näher).
/// </summary>
public sealed class OnnxDepthEstimator(Func<InferenceSession> session) : IDepthEstimator
{
    private const int LongSide = 518, Multiple = 14;
    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f], Std = [0.229f, 0.224f, 0.225f];

    public float[]? Estimate(float[] bgr, int width, int height)
    {
        var k = LongSide / (double)Math.Max(width, height);
        int w = Math.Max(Multiple, (int)Math.Round(width * k / Multiple) * Multiple);
        int h = Math.Max(Multiple, (int)Math.Round(height * k / Multiple) * Multiple);

        // Eingang: bilinear auf w × h, Ebenen R, G, B
        var input = new float[3 * w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var (b, g, r) = Bilinear(bgr, width, height, (x + 0.5f) * width / w - 0.5f, (y + 0.5f) * height / h - 0.5f);
            var i = y * w + x;
            input[i] = (r - Mean[0]) / Std[0];
            input[w * h + i] = (g - Mean[1]) / Std[1];
            input[2 * w * h + i] = (b - Mean[2]) / Std[2];
        }

        var s = session();
        using var value = OrtValue.CreateTensorValueFromMemory(input, [1, 3, h, w]);
        float[] near;
        using (var runOptions = new RunOptions())
        using (var outputs = s.Run(runOptions, [s.InputNames[0]], [value], [s.OutputNames[0]]))
            near = outputs[0].GetTensorDataAsSpan<float>().ToArray();
        if (near.Length != w * h) return null;

        // Auf 0 (fern) … 1 (nah), robust über 2 %- und 98 %-Wert; zurück auf die Größe der Karten
        var sorted = (float[])near.Clone();
        Array.Sort(sorted);
        float lo = sorted[(int)(sorted.Length * 0.02)], hi = sorted[(int)(sorted.Length * 0.98)];
        if (hi - lo < 1e-6f) return null;
        var result = new float[width * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var v = Sample(near, w, h, (x + 0.5f) * w / width - 0.5f, (y + 0.5f) * h / height - 0.5f);
            result[y * width + x] = Math.Clamp((v - lo) / (hi - lo), 0, 1);
        }
        return result;
    }

    private static (float B, float G, float R) Bilinear(float[] bgr, int w, int h, float fx, float fy)
    {
        fx = Math.Clamp(fx, 0, w - 1);
        fy = Math.Clamp(fy, 0, h - 1);
        int x0 = (int)fx, y0 = (int)fy, x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
        float tx = fx - x0, ty = fy - y0;
        float C(int c) =>
            (bgr[(y0 * w + x0) * 3 + c] * (1 - tx) + bgr[(y0 * w + x1) * 3 + c] * tx) * (1 - ty) +
            (bgr[(y1 * w + x0) * 3 + c] * (1 - tx) + bgr[(y1 * w + x1) * 3 + c] * tx) * ty;
        return (C(0), C(1), C(2));
    }

    private static float Sample(float[] m, int w, int h, float fx, float fy)
    {
        fx = Math.Clamp(fx, 0, w - 1);
        fy = Math.Clamp(fy, 0, h - 1);
        int x0 = (int)fx, y0 = (int)fy, x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
        float tx = fx - x0, ty = fy - y0;
        return (m[y0 * w + x0] * (1 - tx) + m[y0 * w + x1] * tx) * (1 - ty) +
               (m[y1 * w + x0] * (1 - tx) + m[y1 * w + x1] * tx) * ty;
    }
}
