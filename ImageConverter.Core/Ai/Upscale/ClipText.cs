using ImageConverter.Core.Ai.Onnx;
using ImageConverter.Core.Platform;

namespace ImageConverter.Core.Ai.Upscale;

/// <summary>
/// Text encoder of Stable Diffusion 2.1 (OpenCLIP ViT-H, as <c>CLIPTextModel</c> with 23 layers), used only for the empty
/// prompt that PiSA-SR works with.
/// </summary>
/// <remarks>
/// It is computed once, directly in C# (77 tokens, no ONNX file needed). The UNet uses the result (1 × 77 × 1024) as fixed
/// keys and values of its cross-attention.
/// </remarks>
public static class ClipText
{
    private const int Tokens = 77, Width = 1024, Heads = 16, Layers = 23;
    private const int Bos = 49406, Eos = 49407, Pad = 0;   // SD 2.x tokenizer: padding with "!" (0)

    /// <summary>Computes the last hidden state (after final_layer_norm) for the empty prompt "".</summary>
    /// <param name="weights">Weights of the SD 2.1 text encoder (names starting with <c>text_model.</c>).</param>
    /// <returns>77 × 1024 values, token by token.</returns>
    /// <remarks>Runs on the CPU; the matrix products are parallelised per token, the attention per head.</remarks>
    public static float[] EmptyPrompt(SafeTensors weights)
    {
        var ids = new int[Tokens];
        ids[0] = Bos;
        ids[1] = Eos;
        for (var i = 2; i < Tokens; i++) ids[i] = Pad;

        const string p = "text_model.";
        var tok = weights.Get(p + "embeddings.token_embedding.weight").Data;
        var pos = weights.Get(p + "embeddings.position_embedding.weight").Data;
        var h = new float[Tokens * Width];
        for (var t = 0; t < Tokens; t++)
        for (var j = 0; j < Width; j++) h[t * Width + j] = tok[ids[t] * Width + j] + pos[t * Width + j];

        for (var l = 0; l < Layers; l++)
        {
            var b = $"{p}encoder.layers.{l}.";
            var a = LayerNorm(h, weights, b + "layer_norm1");
            var attn = SelfAttention(a, weights, b + "self_attn.");
            for (var i = 0; i < h.Length; i++) h[i] += attn[i];
            var m = LayerNorm(h, weights, b + "layer_norm2");
            var up = Linear(m, Tokens, weights, b + "mlp.fc1");
            for (var i = 0; i < up.Length; i++) up[i] = (float)(0.5 * up[i] * (1 + Erf(up[i] / Math.Sqrt(2))));
            var down = Linear(up, Tokens, weights, b + "mlp.fc2");
            for (var i = 0; i < h.Length; i++) h[i] += down[i];
        }
        return LayerNorm(h, weights, p + "final_layer_norm");
    }

    private static float[] LayerNorm(float[] x, SafeTensors w, string name)
    {
        var g = w.Get(name + ".weight").Data;
        var b = w.Get(name + ".bias").Data;
        var y = new float[x.Length];
        for (var t = 0; t < x.Length / Width; t++)
        {
            double mean = 0, var = 0;
            for (var j = 0; j < Width; j++) mean += x[t * Width + j];
            mean /= Width;
            for (var j = 0; j < Width; j++) var += (x[t * Width + j] - mean) * (x[t * Width + j] - mean);
            var inv = 1 / Math.Sqrt(var / Width + 1e-5);
            for (var j = 0; j < Width; j++) y[t * Width + j] = (float)((x[t * Width + j] - mean) * inv * g[j] + b[j]);
        }
        return y;
    }

    /// <summary>x [n, in] · Wᵀ + b → [n, out].</summary>
    private static float[] Linear(float[] x, int n, SafeTensors w, string name)
    {
        var wt = w.Get(name + ".weight");
        var bias = w.Get(name + ".bias").Data;
        int outF = wt.Shape[0], inF = wt.Shape[1];
        var y = new float[n * outF];
        Parallel.For(0, n, ComputeLimits.Options, t =>
        {
            for (var o = 0; o < outF; o++)
            {
                double s = bias[o];
                for (var i = 0; i < inF; i++) s += x[t * inF + i] * wt.Data[o * inF + i];
                y[t * outF + o] = (float)s;
            }
        });
        return y;
    }

    /// <summary>Causal self-attention (each token sees only itself and earlier ones), 16 heads of 64.</summary>
    private static float[] SelfAttention(float[] x, SafeTensors w, string prefix)
    {
        var q = Linear(x, Tokens, w, prefix + "q_proj");
        var k = Linear(x, Tokens, w, prefix + "k_proj");
        var v = Linear(x, Tokens, w, prefix + "v_proj");
        const int d = Width / Heads;
        var scale = 1 / Math.Sqrt(d);
        var o = new float[Tokens * Width];
        Parallel.For(0, Heads, ComputeLimits.Options, hh =>
        {
            var scores = new double[Tokens];
            for (var t = 0; t < Tokens; t++)
            {
                var max = double.NegativeInfinity;
                for (var u = 0; u <= t; u++)
                {
                    double s = 0;
                    for (var j = 0; j < d; j++) s += q[t * Width + hh * d + j] * k[u * Width + hh * d + j];
                    scores[u] = s * scale;
                    max = Math.Max(max, scores[u]);
                }
                double sum = 0;
                for (var u = 0; u <= t; u++) sum += scores[u] = Math.Exp(scores[u] - max);
                for (var j = 0; j < d; j++)
                {
                    double acc = 0;
                    for (var u = 0; u <= t; u++) acc += scores[u] * v[u * Width + hh * d + j];
                    o[t * Width + hh * d + j] = (float)(acc / sum);
                }
            }
        });
        return Linear(o, Tokens, w, prefix + "out_proj");
    }

    /// <summary>Error function accurate to about 1e-15 (erfc with Chebyshev series, Numerical Recipes, 3rd ed., section 6.2.2).</summary>
    private static double Erf(double x)
    {
        var z = Math.Abs(x);
        var t = 2 / (2 + z);
        var ty = 4 * t - 2;
        double[] c =
        [
            -1.3026537197817094, 6.4196979235649026e-1, 1.9476473204185836e-2, -9.561514786808631e-3, -9.46595344482036e-4,
            3.66839497852761e-4, 4.2523324806907e-5, -2.0278578112534e-5, -1.624290004647e-6, 1.303655835580e-6,
            1.5626441722e-8, -8.5238095915e-8, 6.529054439e-9, 5.059343495e-9, -9.91364156e-10, -2.27365122e-10,
            9.6467911e-11, 2.394038e-12, -6.886027e-12, 8.94487e-13, 3.13092e-13, -1.12708e-13, 3.81e-16, 7.106e-15,
            -1.523e-15, -9.4e-17, 1.21e-16, -2.8e-17,
        ];
        double d = 0, dd = 0;
        for (var j = c.Length - 1; j > 0; j--)
        {
            var tmp = d;
            d = ty * d - dd + c[j];
            dd = tmp;
        }
        var erfc = t * Math.Exp(-z * z + 0.5 * (c[0] + ty * d) - dd);
        return x >= 0 ? 1 - erfc : erfc - 1;
    }
}
