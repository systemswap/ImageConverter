using ImageConverter.Core.Ai.Onnx;
using ImageConverter.Core.Platform;

namespace ImageConverter.Core.Ai.Upscale;

/// <summary>
/// Builds the three ONNX parts of PiSA-SR (default mode, one step) from the weights of Stable Diffusion 2.1-base and the
/// PiSA-SR LoRA, in C# without Python.
/// </summary>
/// <remarks>
/// Re-implements diffusers (<c>UNet2DConditionModel</c>, <c>AutoencoderKL</c>) with the settings of SD 2.1 (linear
/// projection in the transformer, head width 64). Because the time step (t = 1) and the text (empty prompt) are fixed,
/// the time embedding and the keys and values of the cross-attention are computed while building: the time ends up as
/// bias in the convolutions, the text as constants.
/// </remarks>
public static class PiSaSrNet
{
    /// <summary>Scaling of the SD 2.1 latent space (<c>vae.config.scaling_factor</c>).</summary>
    public const float LatentScale = 0.18215f;

    /// <summary>LoRA alpha (PEFT default 8); the scale factor is alpha / rank.</summary>
    private const float LoraAlpha = 8;

    /// <summary>Source of weights by name (diffusers naming scheme).</summary>
    public interface IWeights
    {
        /// <summary>Loads a weight.</summary>
        /// <param name="name">Weight name, for example "conv_in.weight".</param>
        /// <returns>A new tensor on every call; the builders modify its data in place (for example to add a bias).</returns>
        TorchTensor Get(string name);

        /// <summary>Returns whether a weight of this name exists.</summary>
        /// <param name="name">Weight name.</param>
        /// <returns>True if the weight exists.</returns>
        bool Has(string name);
    }

    /// <summary>A <c>.safetensors</c> file as weight source.</summary>
    /// <param name="file">Open file; it stays owned by the caller.</param>
    public sealed class FileWeights(SafeTensors file) : IWeights
    {
        /// <inheritdoc/>
        public TorchTensor Get(string name) => file.Get(name);

        /// <inheritdoc/>
        public bool Has(string name) => file.Contains(name);
    }

    /// <summary>
    /// SD 2.1 UNet with the PiSA-SR LoRA merged in: W' = W + Σ (alpha/rank) · B·A over the pixel and the semantic adapters
    /// (in default mode both are active, as in <c>PiSASR_eval</c> with <c>--default</c>). See docs/FEHLERGEDAECHTNIS.md, No. 61.
    /// </summary>
    public sealed class MergedUnet : IWeights
    {
        private readonly SafeTensors _base;
        private readonly Dictionary<string, List<(TorchTensor A, TorchTensor B, float Scale)>> _lora = new(StringComparer.Ordinal);

        /// <summary>Collects the LoRA adapters of the PiSA-SR checkpoint for the UNet weights.</summary>
        /// <param name="unet">SD 2.1 UNet weights; the file stays owned by the caller.</param>
        /// <param name="pisa">PiSA-SR checkpoint with <c>state_dict_unet</c> and the LoRA ranks.</param>
        /// <exception cref="InvalidDataException"><c>state_dict_unet</c>, a rank or a <c>lora_B</c> weight is missing.</exception>
        public MergedUnet(SafeTensors unet, TorchCheckpoint pisa)
        {
            _base = unet;
            var sd = pisa.Get("state_dict_unet") as Dictionary<object, object?>
                     ?? throw new InvalidDataException("PiSA-SR: state_dict_unet fehlt.");
            float Rank(string key) => Convert.ToSingle(pisa.Get(key) ?? throw new InvalidDataException($"PiSA-SR: {key} fehlt."));
            var scales = new Dictionary<string, float> { ["pix"] = LoraAlpha / Rank("lora_rank_unet_pix"), ["sem"] = LoraAlpha / Rank("lora_rank_unet_sem") };
            foreach (var (k, v) in sd)
            {
                if (k is not string key || v is not TorchTensor a || !key.Contains(".lora_A.", StringComparison.Ordinal)) continue;
                var module = key[..key.IndexOf(".lora_A.", StringComparison.Ordinal)];
                var adapter = key[(key.IndexOf(".lora_A.", StringComparison.Ordinal) + 8)..^".weight".Length];   // default_encoder_pix …
                var b = sd.TryGetValue(module + ".lora_B." + adapter + ".weight", out var bv) && bv is TorchTensor bt
                    ? bt : throw new InvalidDataException($"PiSA-SR: lora_B zu {key} fehlt.");
                var kind = adapter.EndsWith("_pix", StringComparison.Ordinal) ? "pix" : "sem";
                if (!_lora.TryGetValue(module + ".weight", out var list)) _lora[module + ".weight"] = list = [];
                list.Add((a, b, scales[kind]));
            }
        }

        /// <summary>Gets the number of weights a LoRA applies to (for verification).</summary>
        public int LoraTargets => _lora.Count;

        /// <inheritdoc/>
        public bool Has(string name) => _base.Contains(name);

        /// <inheritdoc/>
        /// <remarks>Weights with LoRA adapters are merged on every call.</remarks>
        /// <exception cref="InvalidDataException">A LoRA does not match the shape of its weight.</exception>
        public TorchTensor Get(string name)
        {
            var w = _base.Get(name);
            if (!_lora.TryGetValue(name, out var deltas)) return w;
            foreach (var (a, b, s) in deltas) AddDelta(w, a, b, s);
            return w;
        }

        /// <summary>W += s · B∘A (linear: B·A; convolution: B (1×1) after A (k×k), as PEFT <c>get_delta_weight</c>).</summary>
        private static void AddDelta(TorchTensor w, TorchTensor a, TorchTensor b, float s)
        {
            int outC = b.Shape[0], r = b.Shape[1];
            var inner = a.Count / r;   // linear: in; convolution: in · kh · kw
            if (w.Count != outC * inner) throw new InvalidDataException($"LoRA passt nicht zum Gewicht {w} (A {a}, B {b}).");
            Parallel.For(0, outC, ComputeLimits.Options, o =>
            {
                for (var j = 0; j < r; j++)
                {
                    var bj = b.Data[o * r + j] * s;
                    if (bj == 0) continue;
                    var row = o * inner;
                    var arow = j * inner;
                    for (var i = 0; i < inner; i++) w.Data[row + i] += bj * a.Data[arow + i];
                }
            });
        }
    }

    // ------------------------------------------------------------ Building blocks

    /// <summary>Building blocks of the networks on top of an <see cref="OnnxGraph"/>.</summary>
    /// <param name="half">
    /// Half precision (for the graphics card): weights and computation in float16, normalisations and softmax in float32
    /// (otherwise mean/variance and the exponential function overflow). Inputs and outputs stay float32.
    /// </param>
    private sealed class Net(OnnxGraph g, IWeights w, bool half)
    {
        public OnnxGraph G => g;
        public IWeights W => w;
        public bool Half => half;

        /// <summary>Data weight in the computation format of the graph.</summary>
        public string Const(float[] data, params long[] dims) => half ? g.WeightHalf(data, dims) : g.Weight(data, dims);

        public string Cast(string x, bool toHalf) => g.Node("Cast", [x], ("to", toHalf ? 10L : 1L));

        /// <summary>Converts an input (float32) to the computation format.</summary>
        public string In(string x) => half ? Cast(x, true) : x;

        /// <summary>Converts a result back to float32.</summary>
        public string Out(string x) => half ? Cast(x, false) : x;

        public string Conv(string x, string prefix, int stride = 1, int pad = 1, float[]? extraBias = null)
        {
            var wt = w.Get(prefix + ".weight");
            var bias = w.Get(prefix + ".bias").Data;
            if (extraBias is not null) for (var i = 0; i < bias.Length; i++) bias[i] += extraBias[i];
            var k = wt.Shape[2];
            return g.Node("Conv", [x, Const(wt.Data, wt.Shape.Select(d => (long)d).ToArray()), Const(bias, bias.Length)],
                ("kernel_shape", new long[] { k, k }), ("strides", new long[] { stride, stride }), ("pads", new long[] { pad, pad, pad, pad }));
        }

        /// <summary>Linear layer on the last axis: x · Wᵀ (+ b); <paramref name="scale"/> is folded into the weight.</summary>
        public string Linear(string x, string prefix, bool bias = true, float scale = 1)
        {
            var wt = w.Get(prefix + ".weight");
            int outF = wt.Shape[0], inF = wt.Count / outF;
            var t = new float[wt.Count];
            for (var o = 0; o < outF; o++)
            for (var i = 0; i < inF; i++) t[i * outF + o] = wt.Data[o * inF + i] * scale;
            var y = g.Node("MatMul", [x, Const(t, inF, outF)]);
            if (!bias || !w.Has(prefix + ".bias")) return y;
            var b = w.Get(prefix + ".bias").Data;
            if (scale != 1) for (var i = 0; i < b.Length; i++) b[i] *= scale;
            return g.Node("Add", [y, Const(b, b.Length)]);
        }

        public string Silu(string x) => g.Node("Mul", [x, g.Node("Sigmoid", [x])]);

        /// <summary>GroupNorm over NCHW: reshape into groups, InstanceNormalization, reshape back, affine per channel.</summary>
        public string GroupNorm(string x, string prefix, float eps, int groups = 32)
        {
            var gamma = w.Get(prefix + ".weight").Data;
            var beta = w.Get(prefix + ".bias").Data;
            var shape = g.Node("Shape", [x]);
            var grouped = g.Node("Reshape", [half ? Cast(x, false) : x, g.Ints(0, groups, -1)]);   // always normalise in float32
            var norm = g.Node("InstanceNormalization",
                [grouped, g.Weight(Enumerable.Repeat(1f, groups).ToArray(), groups), g.Weight(new float[groups], groups)], ("epsilon", eps));
            var back = g.Node("Reshape", [norm, shape]);
            if (half) back = Cast(back, true);
            return g.Node("Add", [g.Node("Mul", [back, Const(gamma, gamma.Length, 1, 1)]), Const(beta, beta.Length, 1, 1)]);
        }

        public string LayerNorm(string x, string prefix, float eps = 1e-5f)
        {
            var gamma = w.Get(prefix + ".weight").Data;
            var beta = w.Get(prefix + ".bias").Data;
            var y = g.Node("LayerNormalization", [half ? Cast(x, false) : x, g.Weight(gamma, gamma.Length), g.Weight(beta, beta.Length)],
                ("axis", -1L), ("epsilon", eps));
            return half ? Cast(y, true) : y;
        }

        /// <summary>ResnetBlock2D: GN, SiLU, convolution (+ time as bias), GN, SiLU, convolution; 1×1 shortcut if the channel count changes.</summary>
        public string Resnet(string x, string prefix, float eps, float[]? time = null)
        {
            float[]? timeBias = null;
            if (time is not null && w.Has(prefix + ".time_emb_proj.weight")) timeBias = Dense(Silu(time), prefix + ".time_emb_proj");
            var h = Conv(Silu(GroupNorm(x, prefix + ".norm1", eps)), prefix + ".conv1", extraBias: timeBias);
            h = Conv(Silu(GroupNorm(h, prefix + ".norm2", eps)), prefix + ".conv2");
            var skip = w.Has(prefix + ".conv_shortcut.weight") ? Conv(x, prefix + ".conv_shortcut", pad: 0) : x;
            return g.Node("Add", [skip, h]);
        }

        /// <summary>Linear layer on a fixed vector, computed while building.</summary>
        public float[] Dense(float[] v, string prefix)
        {
            var wt = w.Get(prefix + ".weight");
            var b = w.Get(prefix + ".bias").Data;
            int outF = wt.Shape[0], inF = wt.Shape[1];
            var y = new float[outF];
            for (var o = 0; o < outF; o++)
            {
                double s = b[o];
                for (var i = 0; i < inF; i++) s += wt.Data[o * inF + i] * v[i];
                y[o] = (float)s;
            }
            return y;
        }

        public static float[] Silu(float[] v) => v.Select(x => x / (1 + MathF.Exp(-x))).ToArray();

        /// <summary>Multi-head attention on [N, L, C] → [N, L, C]; keys and values from x itself or as constants.</summary>
        public string Attention(string x, string prefix, int heads, bool qkvBias, float[]? context = null, int contextLen = 0)
        {
            var wq = w.Get(prefix + ".to_q.weight");
            var c = wq.Shape[0];
            var d = c / heads;
            var scale = 1 / MathF.Sqrt(d);
            var q = g.Node("Transpose", [g.Node("Reshape", [Linear(x, prefix + ".to_q", qkvBias, scale), g.Ints(0, 0, heads, d)])],
                ("perm", new long[] { 0, 2, 1, 3 }));
            string kT, v;
            if (context is null)
            {
                kT = g.Node("Transpose", [g.Node("Reshape", [Linear(x, prefix + ".to_k", qkvBias), g.Ints(0, 0, heads, d)])], ("perm", new long[] { 0, 2, 3, 1 }));
                v = g.Node("Transpose", [g.Node("Reshape", [Linear(x, prefix + ".to_v", qkvBias), g.Ints(0, 0, heads, d)])], ("perm", new long[] { 0, 2, 1, 3 }));
            }
            else
            {
                // Fixed text: K = ctx · Wkᵀ and V = ctx · Wvᵀ computed now, as [1, h, d, L] and [1, h, L, d]
                var k = Project(context, contextLen, prefix + ".to_k");
                var vv = Project(context, contextLen, prefix + ".to_v");
                var kt = new float[heads * d * contextLen];
                var vt = new float[heads * contextLen * d];
                for (var t = 0; t < contextLen; t++)
                for (var hh = 0; hh < heads; hh++)
                for (var j = 0; j < d; j++)
                {
                    kt[(hh * d + j) * contextLen + t] = k[t * c + hh * d + j];
                    vt[(hh * contextLen + t) * d + j] = vv[t * c + hh * d + j];
                }
                kT = Const(kt, 1, heads, d, contextLen);
                v = Const(vt, 1, heads, contextLen, d);
            }
            var scores = g.Node("MatMul", [q, kT]);
            var probs = g.Node("Softmax", [half ? Cast(scores, false) : scores], ("axis", -1L));
            if (half) probs = Cast(probs, true);
            var o = g.Node("Transpose", [g.Node("MatMul", [probs, v])], ("perm", new long[] { 0, 2, 1, 3 }));
            return Linear(g.Node("Reshape", [o, g.Ints(0, 0, c)]), prefix + ".to_out.0");
        }

        /// <summary>ctx [L, in] · Wᵀ → [L, out], computed while building.</summary>
        private float[] Project(float[] ctx, int len, string prefix)
        {
            var wt = w.Get(prefix + ".weight");
            int outF = wt.Shape[0], inF = wt.Shape[1];
            var y = new float[len * outF];
            Parallel.For(0, len, ComputeLimits.Options, t =>
            {
                for (var o = 0; o < outF; o++)
                {
                    double s = 0;
                    for (var i = 0; i < inF; i++) s += ctx[t * inF + i] * wt.Data[o * inF + i];
                    y[t * outF + o] = (float)s;
                }
            });
            return y;
        }

        /// <summary>NCHW → [N, HW, C]; <see cref="FromTokens"/> converts back (using the shape of a template value).</summary>
        public string ToTokens(string x) =>
            g.Node("Transpose", [g.Node("Reshape", [x, g.Ints(0, 0, -1)])], ("perm", new long[] { 0, 2, 1 }));

        public string FromTokens(string t, string like) =>
            g.Node("Reshape", [g.Node("Transpose", [t], ("perm", new long[] { 0, 2, 1 })), g.Node("Shape", [like])]);

        /// <summary>Transformer2DModel (SD 2.x: linear projection, one BasicTransformerBlock).</summary>
        public string Transformer(string x, string prefix, float[] context, int contextLen)
        {
            var c = w.Get(prefix + ".proj_in.weight").Shape[0];
            var heads = c / 64;
            var h = Linear(ToTokens(GroupNorm(x, prefix + ".norm", 1e-6f)), prefix + ".proj_in");
            var b = prefix + ".transformer_blocks.0";
            h = g.Node("Add", [Attention(LayerNorm(h, b + ".norm1"), b + ".attn1", heads, false), h]);
            h = g.Node("Add", [Attention(LayerNorm(h, b + ".norm2"), b + ".attn2", heads, false, context, contextLen), h]);
            h = g.Node("Add", [FeedForward(LayerNorm(h, b + ".norm3"), b + ".ff"), h]);
            return g.Node("Add", [FromTokens(Linear(h, prefix + ".proj_out"), x), x]);
        }

        /// <summary>GEGLU: proj → (value, gate), value · GELU(gate), then linear back.</summary>
        private string FeedForward(string x, string prefix)
        {
            var proj = Linear(x, prefix + ".net.0.proj");
            var inner = w.Get(prefix + ".net.0.proj.bias").Count / 2;
            var value = g.Node("Slice", [proj, g.Ints(0), g.Ints(inner), g.Ints(-1)]);
            var gate = g.Node("Slice", [proj, g.Ints(inner), g.Ints(2 * inner), g.Ints(-1)]);
            var halfConst = Const([0.5f], 1);
            var gelu = g.Node("Mul", [g.Node("Mul", [gate, halfConst]),
                g.Node("Add", [g.Node("Erf", [g.Node("Mul", [gate, Const([1 / MathF.Sqrt(2)], 1)])]), Const([1f], 1)])]);
            return Linear(g.Node("Mul", [value, gelu]), prefix + ".net.2");
        }

        /// <summary>Nearest neighbour to the size of a template value (×2, also for odd sizes, like diffusers with output_size).</summary>
        public string UpsampleTo(string x, string like)
        {
            var sizes = g.Node("Concat", [
                g.Node("Slice", [g.Node("Shape", [x]), g.Ints(0), g.Ints(2), g.Ints(0)]),
                g.Node("Slice", [g.Node("Shape", [like]), g.Ints(2), g.Ints(4), g.Ints(0)])], ("axis", 0L));
            return g.Node("Resize", [x, "", "", sizes],
                ("mode", "nearest"), ("coordinate_transformation_mode", "asymmetric"), ("nearest_mode", "floor"));
        }

        public string Upsample2x(string x) =>
            g.Node("Resize", [x, "", g.Weight([1f, 1f, 2f, 2f], 4)],
                ("mode", "nearest"), ("coordinate_transformation_mode", "asymmetric"), ("nearest_mode", "floor"));
    }

    // ------------------------------------------------------------ UNet

    /// <summary>Time embedding for t (flip_sin_to_cos, freq_shift 0) → time_embedding (linear, SiLU, linear).</summary>
    private static float[] TimeEmbedding(Net n, float t)
    {
        const int dim = 320, half = dim / 2;
        var e = new float[dim];
        for (var i = 0; i < half; i++)
        {
            var arg = t * Math.Exp(-Math.Log(10000) * i / half);
            e[i] = (float)Math.Cos(arg);          // flip_sin_to_cos: cos first, then sin
            e[half + i] = (float)Math.Sin(arg);
        }
        return n.Dense(Net.Silu(n.Dense(e, "time_embedding.linear_1")), "time_embedding.linear_2");
    }

    /// <summary>
    /// Builds the UNet (input "latent" [1, 4, h, w] → "pred" [1, 4, h, w]) for t = 1 and the fixed prompt
    /// <paramref name="context"/> (77 × 1024).
    /// </summary>
    /// <param name="weights">UNet weights, usually <see cref="MergedUnet"/>.</param>
    /// <param name="context">Empty-prompt embedding, 77 × 1024 values.</param>
    /// <param name="onnxPath">Target file of the model.</param>
    /// <param name="progress">Receives the progress, 0…1.</param>
    /// <param name="dataPath">File for the external weights (over 2 GB); default <c>&lt;onnxPath&gt;.data</c>. It must be in the same folder as the model.</param>
    /// <param name="half">Half precision for the graphics card.</param>
    public static void BuildUnet(IWeights weights, float[] context, string onnxPath, IProgress<double>? progress = null, string? dataPath = null,
        bool half = false)
    {
        const int ctxLen = 77;
        using var g = new OnnxGraph(dataPath ?? onnxPath + ".data");
        var n = new Net(g, weights, half);
        var time = TimeEmbedding(n, 1);
        var x = n.In(g.Input("latent", 1, 4, "h", "w"));

        var h = n.Conv(x, "conv_in");
        var skips = new List<string> { h };
        for (var b = 0; b < 4; b++)
        {
            for (var r = 0; r < 2; r++)
            {
                h = n.Resnet(h, $"down_blocks.{b}.resnets.{r}", 1e-5f, time);
                if (weights.Has($"down_blocks.{b}.attentions.{r}.proj_in.weight"))
                    h = n.Transformer(h, $"down_blocks.{b}.attentions.{r}", context, ctxLen);
                skips.Add(h);
            }
            if (weights.Has($"down_blocks.{b}.downsamplers.0.conv.weight"))
            {
                h = n.Conv(h, $"down_blocks.{b}.downsamplers.0.conv", stride: 2);
                skips.Add(h);
            }
            progress?.Report(0.1 * (b + 1));
        }

        h = n.Resnet(h, "mid_block.resnets.0", 1e-5f, time);
        h = n.Transformer(h, "mid_block.attentions.0", context, ctxLen);
        h = n.Resnet(h, "mid_block.resnets.1", 1e-5f, time);
        progress?.Report(0.5);

        for (var b = 0; b < 4; b++)
        {
            for (var r = 0; r < 3; r++)
            {
                var skip = skips[^1];
                skips.RemoveAt(skips.Count - 1);
                h = g.Node("Concat", [h, skip], ("axis", 1L));
                h = n.Resnet(h, $"up_blocks.{b}.resnets.{r}", 1e-5f, time);
                if (weights.Has($"up_blocks.{b}.attentions.{r}.proj_in.weight"))
                    h = n.Transformer(h, $"up_blocks.{b}.attentions.{r}", context, ctxLen);
            }
            if (weights.Has($"up_blocks.{b}.upsamplers.0.conv.weight"))
                h = n.Conv(n.UpsampleTo(h, skips[^1]), $"up_blocks.{b}.upsamplers.0.conv");   // to the size of the next skip connection
            progress?.Report(0.5 + 0.1 * (b + 1));
        }

        h = n.Conv(n.Silu(n.GroupNorm(h, "conv_norm_out", 1e-5f)), "conv_out");
        g.Output(n.Out(h), "pred", 1, 4, "h", "w");
        g.Save(onnxPath, "ImageConverter PiSA-SR UNet");
        progress?.Report(1);
    }

    // ------------------------------------------------------------ VAE

    /// <summary>Attention in the VAE: one head over all pixels, with GroupNorm before it and a residual connection.</summary>
    private static string VaeAttention(Net n, string x, string prefix)
    {
        var t = n.ToTokens(n.GroupNorm(x, prefix + ".group_norm", 1e-6f));
        return n.G.Node("Add", [n.FromTokens(n.Attention(t, prefix, 1, true), x), x]);
    }

    /// <summary>Builds the VAE encoder: "image" [1, 3, H, W] (−1 … 1) → "latent" [1, 4, H/8, W/8] (mean · 0.18215).</summary>
    /// <param name="weights">VAE weights.</param>
    /// <param name="onnxPath">Target file of the model (weights embedded).</param>
    /// <param name="half">Half precision for the graphics card.</param>
    public static void BuildVaeEncoder(IWeights weights, string onnxPath, bool half = false)
    {
        using var g = new OnnxGraph();
        var n = new Net(g, weights, half);
        var x = n.In(g.Input("image", 1, 3, "H", "W"));
        var h = n.Conv(x, "encoder.conv_in");
        for (var b = 0; b < 4; b++)
        {
            for (var r = 0; r < 2; r++) h = n.Resnet(h, $"encoder.down_blocks.{b}.resnets.{r}", 1e-6f);
            if (weights.Has($"encoder.down_blocks.{b}.downsamplers.0.conv.weight"))
            {
                // diffusers: one row of zeros on the right and at the bottom, then convolution without padding, stride 2
                h = g.Node("Pad", [h, g.Ints(0, 0, 0, 0, 0, 0, 1, 1)]);
                h = n.Conv(h, $"encoder.down_blocks.{b}.downsamplers.0.conv", stride: 2, pad: 0);
            }
        }
        h = n.Resnet(h, "encoder.mid_block.resnets.0", 1e-6f);
        h = VaeAttention(n, h, "encoder.mid_block.attentions.0");
        h = n.Resnet(h, "encoder.mid_block.resnets.1", 1e-6f);
        h = n.Conv(n.Silu(n.GroupNorm(h, "encoder.conv_norm_out", 1e-6f)), "encoder.conv_out");
        h = n.Conv(h, "quant_conv", pad: 0);
        var mean = g.Node("Slice", [h, g.Ints(0), g.Ints(4), g.Ints(1)]);
        g.Output(n.Out(g.Node("Mul", [mean, n.Const([LatentScale], 1)])), "latent", 1, 4, "h", "w");
        g.Save(onnxPath, "ImageConverter PiSA-SR VAE-Encoder");
    }

    /// <summary>Builds the VAE decoder: "latent" [1, 4, h, w] (scaled) → "image" [1, 3, 8h, 8w] (−1 … 1, not clamped).</summary>
    /// <param name="weights">VAE weights.</param>
    /// <param name="onnxPath">Target file of the model (weights embedded).</param>
    /// <param name="half">Half precision for the graphics card.</param>
    public static void BuildVaeDecoder(IWeights weights, string onnxPath, bool half = false)
    {
        using var g = new OnnxGraph();
        var n = new Net(g, weights, half);
        var z = n.In(g.Input("latent", 1, 4, "h", "w"));
        var h = g.Node("Mul", [z, n.Const([1 / LatentScale], 1)]);
        h = n.Conv(h, "post_quant_conv", pad: 0);
        h = n.Conv(h, "decoder.conv_in");
        h = n.Resnet(h, "decoder.mid_block.resnets.0", 1e-6f);
        h = VaeAttention(n, h, "decoder.mid_block.attentions.0");
        h = n.Resnet(h, "decoder.mid_block.resnets.1", 1e-6f);
        for (var b = 0; b < 4; b++)
        {
            for (var r = 0; r < 3; r++) h = n.Resnet(h, $"decoder.up_blocks.{b}.resnets.{r}", 1e-6f);
            if (weights.Has($"decoder.up_blocks.{b}.upsamplers.0.conv.weight"))
                h = n.Conv(n.Upsample2x(h), $"decoder.up_blocks.{b}.upsamplers.0.conv");
        }
        h = n.Conv(n.Silu(n.GroupNorm(h, "decoder.conv_norm_out", 1e-6f)), "decoder.conv_out");
        g.Output(n.Out(h), "image", 1, 3, "H", "W");
        g.Save(onnxPath, "ImageConverter PiSA-SR VAE-Decoder");
    }

    // ------------------------------------------------------------ Everything together

    /// <summary>File name of the UNet model (full precision).</summary>
    public const string UnetFile = "pisasr-unet.onnx";

    /// <summary>File name of the VAE encoder model (full precision).</summary>
    public const string EncoderFile = "pisasr-vae-encoder.onnx";

    /// <summary>File name of the VAE decoder model (full precision).</summary>
    public const string DecoderFile = "pisasr-vae-decoder.onnx";

    /// <summary>Returns the file name for full or half precision ("pisasr-unet-fp16.onnx").</summary>
    /// <param name="file">File name for full precision.</param>
    /// <param name="half">Whether the half-precision variant is wanted.</param>
    /// <returns>The file name.</returns>
    public static string FileName(string file, bool half) => half ? file.Replace(".onnx", "-fp16.onnx", StringComparison.Ordinal) : file;

    /// <summary>
    /// Builds the three ONNX files in <paramref name="targetDir"/>.
    /// </summary>
    /// <remarks>
    /// The models are written under temporary names first and renamed only at the end, so cancellation leaves no half
    /// model. Cancellation is checked between the stages (VAE, UNet), not within them.
    /// </remarks>
    /// <param name="unetPath">SD 2.1 UNet weights (<c>.safetensors</c>).</param>
    /// <param name="vaePath">SD 2.1 VAE weights (<c>.safetensors</c>).</param>
    /// <param name="loraPath">PiSA-SR checkpoint with the LoRA.</param>
    /// <param name="context">Empty-prompt embedding, 77 × 1024 values.</param>
    /// <param name="targetDir">Target folder; it is created if necessary.</param>
    /// <param name="progress">Receives the progress, 0…1.</param>
    /// <param name="cancel">Cancels the build between its stages.</param>
    /// <param name="half">Half precision for the graphics card (files with "-fp16").</param>
    public static void BuildAll(string unetPath, string vaePath, string loraPath, float[] context, string targetDir,
        IProgress<double>? progress = null, CancellationToken cancel = default, bool half = false)
    {
        Directory.CreateDirectory(targetDir);
        string Name(string f) => FileName(f, half);
        string Tmp(string f) => Path.Combine(targetDir, Name(f) + ".bau");

        using (var vae = new SafeTensors(vaePath))
        {
            var w = new FileWeights(vae);
            BuildVaeEncoder(w, Tmp(EncoderFile), half);
            progress?.Report(0.03);
            BuildVaeDecoder(w, Tmp(DecoderFile), half);
            progress?.Report(0.06);
        }
        cancel.ThrowIfCancellationRequested();

        using (var unet = new SafeTensors(unetPath))
        {
            var merged = new MergedUnet(unet, TorchCheckpoint.Load(loraPath));
            // The model refers to the .data file by its final name, so it is written under that name directly
            BuildUnet(merged, context, Tmp(UnetFile), new Part(progress, 0.06, 0.94), Path.Combine(targetDir, Name(UnetFile) + ".data"), half);
        }
        cancel.ThrowIfCancellationRequested();
        foreach (var f in new[] { EncoderFile, DecoderFile, UnetFile })
            File.Move(Tmp(f), Path.Combine(targetDir, Name(f)), overwrite: true);
        progress?.Report(1);
    }

    /// <summary>
    /// Partial progress: 0 … 1 → from … from + span, passed on immediately. <see cref="Progress{T}"/> without a
    /// synchronisation context would report through the thread pool and therefore out of order.
    /// See docs/FEHLERGEDAECHTNIS.md, No. 61.
    /// </summary>
    internal sealed class Part(IProgress<double>? target, double from, double span) : IProgress<double>
    {
        public void Report(double value) => target?.Report(from + span * value);
    }
}
