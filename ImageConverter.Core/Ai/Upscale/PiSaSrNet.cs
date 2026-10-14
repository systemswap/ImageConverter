using ImageConverter.Core.Ai.Onnx;

namespace ImageConverter.Core.Ai.Upscale;

/// <summary>
/// Baut die drei ONNX-Teile von PiSA-SR (Modus „Standard“, ein Schritt) aus den Gewichten von Stable Diffusion 2.1-base und
/// der LoRA von PiSA-SR — in C#, ohne Python. Nachbau von diffusers (<c>UNet2DConditionModel</c>, <c>AutoencoderKL</c>) mit
/// den Einstellungen von SD 2.1 (lineare Projektion im Transformer, Kopfbreite 64). Weil Zeitschritt (t = 1) und Text
/// (leere Beschreibung) fest sind, werden Zeit-Einbettung und Schlüssel/Werte der Kreuz-Aufmerksamkeit schon beim Bauen
/// gerechnet: die Zeit landet als Bias in den Faltungen, der Text als Konstanten.
/// </summary>
public static class PiSaSrNet
{
    /// <summary>Skalierung des Latentraums von SD 2.1 (<c>vae.config.scaling_factor</c>).</summary>
    public const float LatentScale = 0.18215f;

    /// <summary>LoRA-Rang und -Alpha (PEFT-Vorgabe 8) → Faktor alpha / rang.</summary>
    private const float LoraAlpha = 8;

    /// <summary>Gewichte nach Namen (diffusers-Schema).</summary>
    public interface IWeights
    {
        TorchTensor Get(string name);
        bool Has(string name);
    }

    /// <summary>Safetensors-Datei als Gewichtsquelle.</summary>
    public sealed class FileWeights(SafeTensors file) : IWeights
    {
        public TorchTensor Get(string name) => file.Get(name);
        public bool Has(string name) => file.Contains(name);
    }

    /// <summary>
    /// UNet von SD 2.1 mit eingerechneter PiSA-LoRA: W' = W + Σ (alpha/rang) · B·A über die Pixel- und die
    /// semantischen Adapter (im Modus „Standard“ sind beide aktiv — wie <c>PiSASR_eval</c> mit <c>--default</c>).
    /// </summary>
    public sealed class MergedUnet : IWeights
    {
        private readonly SafeTensors _base;
        private readonly Dictionary<string, List<(TorchTensor A, TorchTensor B, float Scale)>> _lora = new(StringComparer.Ordinal);

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

        /// <summary>Zahl der Gewichte, auf die eine LoRA wirkt (Prüfung).</summary>
        public int LoraTargets => _lora.Count;

        public bool Has(string name) => _base.Contains(name);

        public TorchTensor Get(string name)
        {
            var w = _base.Get(name);
            if (!_lora.TryGetValue(name, out var deltas)) return w;
            foreach (var (a, b, s) in deltas) AddDelta(w, a, b, s);
            return w;
        }

        /// <summary>W += s · B∘A (Linear: B·A; Faltung: B (1×1) nach A (k×k) — wie PEFT <c>get_delta_weight</c>).</summary>
        private static void AddDelta(TorchTensor w, TorchTensor a, TorchTensor b, float s)
        {
            int outC = b.Shape[0], r = b.Shape[1];
            var inner = a.Count / r;   // Linear: in; Faltung: in · kh · kw
            if (w.Count != outC * inner) throw new InvalidDataException($"LoRA passt nicht zum Gewicht {w} (A {a}, B {b}).");
            Parallel.For(0, outC, o =>
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

    // ------------------------------------------------------------ Bausteine

    private sealed class Net(OnnxGraph g, IWeights w)
    {
        public OnnxGraph G => g;
        public IWeights W => w;

        public string Const(float[] data, params long[] dims) => g.Weight(data, dims);

        public string Conv(string x, string prefix, int stride = 1, int pad = 1, float[]? extraBias = null)
        {
            var wt = w.Get(prefix + ".weight");
            var bias = w.Get(prefix + ".bias").Data;
            if (extraBias is not null) for (var i = 0; i < bias.Length; i++) bias[i] += extraBias[i];
            var k = wt.Shape[2];
            return g.Node("Conv", [x, g.Weight(wt.Data, wt.Shape.Select(d => (long)d).ToArray()), g.Weight(bias, bias.Length)],
                ("kernel_shape", new long[] { k, k }), ("strides", new long[] { stride, stride }), ("pads", new long[] { pad, pad, pad, pad }));
        }

        /// <summary>Linear auf der letzten Achse: x · Wᵀ (+ b); <paramref name="scale"/> wird ins Gewicht eingerechnet.</summary>
        public string Linear(string x, string prefix, bool bias = true, float scale = 1)
        {
            var wt = w.Get(prefix + ".weight");
            int outF = wt.Shape[0], inF = wt.Count / outF;
            var t = new float[wt.Count];
            for (var o = 0; o < outF; o++)
            for (var i = 0; i < inF; i++) t[i * outF + o] = wt.Data[o * inF + i] * scale;
            var y = g.Node("MatMul", [x, g.Weight(t, inF, outF)]);
            if (!bias || !w.Has(prefix + ".bias")) return y;
            var b = w.Get(prefix + ".bias").Data;
            if (scale != 1) for (var i = 0; i < b.Length; i++) b[i] *= scale;
            return g.Node("Add", [y, g.Weight(b, b.Length)]);
        }

        public string Silu(string x) => g.Node("Mul", [x, g.Node("Sigmoid", [x])]);

        /// <summary>GroupNorm über NCHW: in Gruppen umformen, InstanceNormalization, zurück, Affin je Kanal.</summary>
        public string GroupNorm(string x, string prefix, float eps, int groups = 32)
        {
            var gamma = w.Get(prefix + ".weight").Data;
            var beta = w.Get(prefix + ".bias").Data;
            var shape = g.Node("Shape", [x]);
            var grouped = g.Node("Reshape", [x, g.Ints(0, groups, -1)]);
            var norm = g.Node("InstanceNormalization",
                [grouped, g.Weight(Enumerable.Repeat(1f, groups).ToArray(), groups), g.Weight(new float[groups], groups)], ("epsilon", eps));
            var back = g.Node("Reshape", [norm, shape]);
            return g.Node("Add", [g.Node("Mul", [back, g.Weight(gamma, gamma.Length, 1, 1)]), g.Weight(beta, beta.Length, 1, 1)]);
        }

        public string LayerNorm(string x, string prefix, float eps = 1e-5f)
        {
            var gamma = w.Get(prefix + ".weight").Data;
            var beta = w.Get(prefix + ".bias").Data;
            return g.Node("LayerNormalization", [x, g.Weight(gamma, gamma.Length), g.Weight(beta, beta.Length)], ("axis", -1L), ("epsilon", eps));
        }

        /// <summary>ResnetBlock2D: GN, SiLU, Faltung (+ Zeit als Bias), GN, SiLU, Faltung; Abkürzung mit 1×1 bei anderer Kanalzahl.</summary>
        public string Resnet(string x, string prefix, float eps, float[]? time = null)
        {
            float[]? timeBias = null;
            if (time is not null && w.Has(prefix + ".time_emb_proj.weight")) timeBias = Dense(Silu(time), prefix + ".time_emb_proj");
            var h = Conv(Silu(GroupNorm(x, prefix + ".norm1", eps)), prefix + ".conv1", extraBias: timeBias);
            h = Conv(Silu(GroupNorm(h, prefix + ".norm2", eps)), prefix + ".conv2");
            var skip = w.Has(prefix + ".conv_shortcut.weight") ? Conv(x, prefix + ".conv_shortcut", pad: 0) : x;
            return g.Node("Add", [skip, h]);
        }

        /// <summary>Lineare Schicht auf einem festen Vektor, schon beim Bauen gerechnet.</summary>
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

        /// <summary>Mehrkopf-Aufmerksamkeit auf [N, L, C] → [N, L, C]; Schlüssel/Werte aus x selbst oder als Konstanten.</summary>
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
                // Text fest: K = ctx · Wkᵀ, V = ctx · Wvᵀ jetzt schon, als [1, h, d, L] und [1, h, L, d]
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
                kT = g.Weight(kt, 1, heads, d, contextLen);
                v = g.Weight(vt, 1, heads, contextLen, d);
            }
            var probs = g.Node("Softmax", [g.Node("MatMul", [q, kT])], ("axis", -1L));
            var o = g.Node("Transpose", [g.Node("MatMul", [probs, v])], ("perm", new long[] { 0, 2, 1, 3 }));
            return Linear(g.Node("Reshape", [o, g.Ints(0, 0, c)]), prefix + ".to_out.0");
        }

        /// <summary>ctx [L, in] · Wᵀ → [L, out], beim Bauen gerechnet.</summary>
        private float[] Project(float[] ctx, int len, string prefix)
        {
            var wt = w.Get(prefix + ".weight");
            int outF = wt.Shape[0], inF = wt.Shape[1];
            var y = new float[len * outF];
            Parallel.For(0, len, t =>
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

        /// <summary>NCHW → [N, HW, C] und zurück (mit der Form eines Vorbilds).</summary>
        public string ToTokens(string x) =>
            g.Node("Transpose", [g.Node("Reshape", [x, g.Ints(0, 0, -1)])], ("perm", new long[] { 0, 2, 1 }));

        public string FromTokens(string t, string like) =>
            g.Node("Reshape", [g.Node("Transpose", [t], ("perm", new long[] { 0, 2, 1 })), g.Node("Shape", [like])]);

        /// <summary>Transformer2DModel (SD 2.x: lineare Projektion, ein BasicTransformerBlock).</summary>
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

        /// <summary>GEGLU: proj → (Wert, Tor), Wert · GELU(Tor), dann Linear zurück.</summary>
        private string FeedForward(string x, string prefix)
        {
            var proj = Linear(x, prefix + ".net.0.proj");
            var inner = w.Get(prefix + ".net.0.proj.bias").Count / 2;
            var value = g.Node("Slice", [proj, g.Ints(0), g.Ints(inner), g.Ints(-1)]);
            var gate = g.Node("Slice", [proj, g.Ints(inner), g.Ints(2 * inner), g.Ints(-1)]);
            var half = g.Weight([0.5f], 1);
            var gelu = g.Node("Mul", [g.Node("Mul", [gate, half]),
                g.Node("Add", [g.Node("Erf", [g.Node("Mul", [gate, g.Weight([1 / MathF.Sqrt(2)], 1)])]), g.Weight([1f], 1)])]);
            return Linear(g.Node("Mul", [value, gelu]), prefix + ".net.2");
        }

        /// <summary>Nächster Nachbar auf die Größe eines Vorbilds (×2, auch bei ungeraden Größen wie diffusers mit output_size).</summary>
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

    /// <summary>Zeit-Einbettung für t (flip_sin_to_cos, freq_shift 0) → time_embedding (Linear, SiLU, Linear).</summary>
    private static float[] TimeEmbedding(Net n, float t)
    {
        const int dim = 320, half = dim / 2;
        var e = new float[dim];
        for (var i = 0; i < half; i++)
        {
            var arg = t * Math.Exp(-Math.Log(10000) * i / half);
            e[i] = (float)Math.Cos(arg);          // flip_sin_to_cos: erst cos, dann sin
            e[half + i] = (float)Math.Sin(arg);
        }
        return n.Dense(Net.Silu(n.Dense(e, "time_embedding.linear_1")), "time_embedding.linear_2");
    }

    /// <summary>
    /// UNet (Eingang „latent“ [1, 4, h, w] → „pred“ [1, 4, h, w]) für t = 1 und die feste Beschreibung
    /// <paramref name="context"/> (77 × 1024). Gewichte ausgelagert in <c>&lt;onnxPath&gt;.data</c> (über 2 GB).
    /// </summary>
    public static void BuildUnet(IWeights weights, float[] context, string onnxPath, IProgress<double>? progress = null, string? dataPath = null)
    {
        const int ctxLen = 77;
        using var g = new OnnxGraph(dataPath ?? onnxPath + ".data");
        var n = new Net(g, weights);
        var time = TimeEmbedding(n, 1);
        var x = g.Input("latent", 1, 4, "h", "w");

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
                h = n.Conv(n.UpsampleTo(h, skips[^1]), $"up_blocks.{b}.upsamplers.0.conv");   // auf die Größe der nächsten Abkürzung
            progress?.Report(0.5 + 0.1 * (b + 1));
        }

        h = n.Conv(n.Silu(n.GroupNorm(h, "conv_norm_out", 1e-5f)), "conv_out");
        g.Output(h, "pred", 1, 4, "h", "w");
        g.Save(onnxPath, "ImageConverter PiSA-SR UNet");
        progress?.Report(1);
    }

    // ------------------------------------------------------------ VAE

    /// <summary>Aufmerksamkeit im VAE: ein Kopf über alle Pixel, mit GroupNorm davor und Rest-Verbindung.</summary>
    private static string VaeAttention(Net n, string x, string prefix)
    {
        var t = n.ToTokens(n.GroupNorm(x, prefix + ".group_norm", 1e-6f));
        return n.G.Node("Add", [n.FromTokens(n.Attention(t, prefix, 1, true), x), x]);
    }

    /// <summary>VAE-Encoder: „image“ [1, 3, H, W] (−1 … 1) → „latent“ [1, 4, H/8, W/8] (Mittelwert · 0,18215).</summary>
    public static void BuildVaeEncoder(IWeights weights, string onnxPath)
    {
        using var g = new OnnxGraph();
        var n = new Net(g, weights);
        var x = g.Input("image", 1, 3, "H", "W");
        var h = n.Conv(x, "encoder.conv_in");
        for (var b = 0; b < 4; b++)
        {
            for (var r = 0; r < 2; r++) h = n.Resnet(h, $"encoder.down_blocks.{b}.resnets.{r}", 1e-6f);
            if (weights.Has($"encoder.down_blocks.{b}.downsamplers.0.conv.weight"))
            {
                // diffusers: rechts und unten eine Null-Reihe, dann Faltung ohne Polster mit Schritt 2
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
        g.Output(g.Node("Mul", [mean, g.Weight([LatentScale], 1)]), "latent", 1, 4, "h", "w");
        g.Save(onnxPath, "ImageConverter PiSA-SR VAE-Encoder");
    }

    /// <summary>VAE-Decoder: „latent“ [1, 4, h, w] (skaliert) → „image“ [1, 3, 8h, 8w] (−1 … 1, ungeklammert).</summary>
    public static void BuildVaeDecoder(IWeights weights, string onnxPath)
    {
        using var g = new OnnxGraph();
        var n = new Net(g, weights);
        var z = g.Input("latent", 1, 4, "h", "w");
        var h = g.Node("Mul", [z, g.Weight([1 / LatentScale], 1)]);
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
        g.Output(h, "image", 1, 3, "H", "W");
        g.Save(onnxPath, "ImageConverter PiSA-SR VAE-Decoder");
    }

    // ------------------------------------------------------------ alles zusammen

    public const string UnetFile = "pisasr-unet.onnx";
    public const string EncoderFile = "pisasr-vae-encoder.onnx";
    public const string DecoderFile = "pisasr-vae-decoder.onnx";

    /// <summary>
    /// Baut die drei ONNX-Dateien nach <paramref name="targetDir"/> (zuerst unter Hilfsnamen, erst am Ende umbenannt — ein
    /// Abbruch hinterlässt kein halbes Modell). <paramref name="context"/>: leere Beschreibung, 77 × 1024.
    /// </summary>
    public static void BuildAll(string unetPath, string vaePath, string loraPath, float[] context, string targetDir,
        IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        Directory.CreateDirectory(targetDir);
        string Tmp(string f) => Path.Combine(targetDir, f + ".bau");

        using (var vae = new SafeTensors(vaePath))
        {
            var w = new FileWeights(vae);
            BuildVaeEncoder(w, Tmp(EncoderFile));
            progress?.Report(0.03);
            BuildVaeDecoder(w, Tmp(DecoderFile));
            progress?.Report(0.06);
        }
        cancel.ThrowIfCancellationRequested();

        using (var unet = new SafeTensors(unetPath))
        {
            var merged = new MergedUnet(unet, TorchCheckpoint.Load(loraPath));
            // Die .data-Datei heißt im Modell so, wie sie am Ende liegt — sie wird unter ihrem endgültigen Namen geschrieben
            BuildUnet(merged, context, Tmp(UnetFile), new Part(progress, 0.06, 0.94), Path.Combine(targetDir, UnetFile + ".data"));
        }
        cancel.ThrowIfCancellationRequested();
        foreach (var f in new[] { EncoderFile, DecoderFile, UnetFile })
            File.Move(Tmp(f), Path.Combine(targetDir, f), overwrite: true);
        progress?.Report(1);
    }

    /// <summary>Teilfortschritt: 0 … 1 → from … from + span, sofort weitergereicht (kein Umweg über einen Kontext).</summary>
    internal sealed class Part(IProgress<double>? target, double from, double span) : IProgress<double>
    {
        public void Report(double value) => target?.Report(from + span * value);
    }
}
