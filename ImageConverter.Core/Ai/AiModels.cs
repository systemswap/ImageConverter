namespace ImageConverter.Core.Ai;

/// <summary>Category of an AI model; determines where it is offered and how it is run.</summary>
public enum AiModelKind
{
    /// <summary>Whole image in tiles: noise, artefacts, blur, enlargement.</summary>
    Enhance,

    /// <summary>Faces only: detect, align, restore, paste back.</summary>
    Face,

    /// <summary>Needed by other models (face detection, face parsing); not selectable on its own.</summary>
    Helper,

    /// <summary>Repaints marked areas: removes watermarks, text and distracting objects (the "Remove" tab).</summary>
    Inpaint,

    /// <summary>Straightens a photographed sheet ("AI straighten" in the preview).</summary>
    Dewarp,

    /// <summary>Enlargement with AI (the "Upscaling" group under "Size").</summary>
    Upscale,

    /// <summary>Weights from which the app builds a model (Stable Diffusion for PiSA-SR); downloaded only together with the main model.</summary>
    Part,

    /// <summary>Separates the subject from the background (the "Hintergrund" tab): one mask for the whole image.</summary>
    Background,
}

/// <summary>
/// A downloadable AI model.
/// </summary>
/// <remarks>
/// After the download it lives in its own subfolder <c>KI-Modelle/&lt;Id&gt;/</c> (see <see cref="AiModelStore"/>);
/// it counts as installed as soon as <see cref="MainFile"/> is present there.
/// </remarks>
/// <param name="Id">Stable identifier; also the name of the model's subfolder.</param>
/// <param name="Name">Display name.</param>
/// <param name="Purpose">Short description of what the model is good for.</param>
/// <param name="Description">Longer description for the model list.</param>
/// <param name="Kind">Category of the model.</param>
/// <param name="Url">Download address: a single file (.onnx, checkpoint or weights) or a .zip archive (<see cref="IsZip"/>).</param>
/// <param name="DownloadSize">Size of the download in bytes; used for progress if the server sends no length.</param>
/// <param name="Sha256">SHA-256 checksum of the download (hexadecimal); if it does not match, nothing is saved.</param>
/// <param name="IsZip">Whether <paramref name="Url"/> points to a .zip archive that is extracted after the download.</param>
/// <param name="MainFile">File name inside the model folder that is opened and marks the model as installed.</param>
/// <param name="License">Licence identifier shown to the user.</param>
/// <param name="NonCommercial">Whether the licence permits only non-commercial use.</param>
/// <param name="Origin">Authors and source of the model (and of the ONNX conversion).</param>
/// <param name="Scale">Magnification of the model (Enhance, Upscale), otherwise 1.</param>
/// <param name="TileSize">Tile size (Enhance, Upscale), face crop size (Face) or input size (Inpaint, face detection) in pixels.</param>
/// <param name="FixedTile">Whether the model accepts only exactly this tile size.</param>
/// <param name="Requires">IDs of further models that are downloaded together with this one.</param>
/// <param name="Gpu">
/// Whether the model may run on the graphics card. Only models that have been tested there: an error in the graphics
/// driver or in DirectML is a memory fault that the app cannot catch. See docs/FEHLERGEDAECHTNIS.md, No. 67 and No. 69.
/// </param>
/// <param name="GpuWithoutOptimization">
/// Whether the model must be opened on the graphics card without graph optimisation: with it, DirectML fails to load
/// BiRefNet ("Split … Cannot parse data from external tensors"). See docs/KI-HINTERGRUND.md.
/// </param>
/// <param name="ManualPage">
/// For models without a download address (<paramref name="Url"/> empty): page where the user downloads the file
/// after agreeing to the terms (RMBG-2.0); the model list then shows instructions instead of "Herunterladen".
/// </param>
public sealed record AiModel(
    string Id,
    string Name,
    string Purpose,
    string Description,
    AiModelKind Kind,
    string Url,
    long DownloadSize,
    string Sha256,
    bool IsZip,
    string MainFile,
    string License,
    bool NonCommercial,
    string Origin,
    int Scale = 1,
    int TileSize = 512,
    bool FixedTile = true,
    string[]? Requires = null,
    bool Gpu = false,
    bool GpuWithoutOptimization = false,
    string? ManualPage = null)
{
    /// <summary>Gets the IDs of further models that are downloaded together with this one; never null.</summary>
    public string[] Requires { get; init; } = Requires ?? [];

    /// <summary>Gets the download size as text in KB or MB.</summary>
    public string SizeText => DownloadSize >= 1024 * 1024
        ? $"{DownloadSize / (1024.0 * 1024):0.#} MB"
        : $"{DownloadSize / 1024.0:0} KB";

    /// <summary>Returns the display name.</summary>
    /// <returns><see cref="Name"/>.</returns>
    public override string ToString() => Name;
}

/// <summary>
/// The models offered for download.
/// </summary>
/// <remarks>
/// Only models with a known licence are included. Sources are fixed files: versioned releases or pinned commits
/// where the host offers them. The checksums protect against corrupted or replaced files.
/// </remarks>
public static class AiModelCatalog
{
    private const string FaceFusion = "https://github.com/facefusion/facefusion-assets/releases/download/";

    /// <summary>Gets the small all-round enhancement model (Real-ESRGAN general x4v3).</summary>
    public static AiModel Allround { get; } = new(
        "allround",
        "Allround (klein)",
        "Rauschen, JPEG-Artefakte, leichte Unschärfe",
        "Real-ESRGAN „general x4v3“: kleines, schnelles Modell für Fotos aus Handy und Netz. " +
        "Entfernt Rauschen und Kompressionsblöcke und macht Kanten klarer. Kann vergrößern (bis 4×).",
        AiModelKind.Enhance,
        "https://qaihub-public-assets.s3.us-west-2.amazonaws.com/qai-hub-models/models/real_esrgan_general_x4v3/releases/v0.64.0/real_esrgan_general_x4v3-onnx-float.zip",
        4_540_895,
        "fc094329b24a00c936ebc1cc4c51bc59633e7dc36f4a553344342171cbd3c7cf",
        IsZip: true,
        MainFile: "real_esrgan_general_x4v3.onnx",
        License: "BSD-3-Clause",
        NonCommercial: false,
        Origin: "Xintao Wang (Real-ESRGAN), ONNX von Qualcomm AI Hub",
        Scale: 4,
        TileSize: 128,
        FixedTile: true,
        Gpu: true);

    /// <summary>Gets the face detector (YuNet), a helper model loaded with every face model.</summary>
    public static AiModel FaceDetector { get; } = new(
        "yunet",
        "Gesichtserkennung",
        "Findet Gesichter für die Gesichtsmodelle",
        "YuNet (OpenCV): sehr kleines Modell, das Gesichter und Augen, Nase, Mund findet. " +
        "Wird mit jedem Gesichtsmodell automatisch geladen.",
        AiModelKind.Helper,
        FaceFusion + "models-3.4.0/yunet_2023_mar.onnx",
        232_475,
        "490d79bc15c0e958d91c999c566c6a338b310ead96e0274dd004e59dd6abecb9",
        IsZip: false,
        MainFile: "yunet_2023_mar.onnx",
        License: "MIT",
        NonCommercial: false,
        Origin: "Shiqi Yu u. a. (OpenCV Zoo), ONNX von FaceFusion",
        TileSize: 640);

    /// <summary>Gets the face parsing model (BiSeNet), a helper model loaded with every face model.</summary>
    public static AiModel FaceParser { get; } = new(
        "faceparser",
        "Gesichtsbereiche",
        "Erkennt Haut, Augen, Mund … für die Detailregler",
        "BiSeNet (Face Parsing): teilt jedes Gesicht in Bereiche — Haut, Nase, Augen, Brauen, Lippen, Zähne, Haare. " +
        "Damit kommen Falten und Korn nur auf die Haut, Augen und Zähne bleiben vom Modell. " +
        "Wird mit jedem Gesichtsmodell automatisch geladen.",
        AiModelKind.Helper,
        FaceFusion + "models-3.1.0/bisenet_resnet_18.onnx",
        53_205_356,
        "2218b6183c26ca5c83303232d682a536c670c13ea9695f716c777d1f244eefe9",
        IsZip: false,
        MainFile: "bisenet_resnet_18.onnx",
        License: "MIT",
        NonCommercial: false,
        Origin: "zllrunning (face-parsing.PyTorch, BiSeNet), ONNX von FaceFusion",
        TileSize: 512);

    /// <summary>Gets the GFPGAN 1.4 face model.</summary>
    public static AiModel Gfpgan { get; } = new(
        "gfpgan",
        "Gesichter – GFPGAN 1.4",
        "Augen, Mund, Haut bei unscharfen Gesichtern",
        "GFPGAN 1.4: stellt unscharfe oder kleine Gesichter natürlich wieder her. " +
        "Nur die Gesichter werden verändert, der Rest des Bildes bleibt, wie er ist.",
        AiModelKind.Face,
        FaceFusion + "models-3.0.0/gfpgan_1.4.onnx",
        340_299_087,
        "accc4757b26bdb89b32b4d3500d4f79c9dff97c1dd7c7104bf9dcb95e3311385",
        IsZip: false,
        MainFile: "gfpgan_1.4.onnx",
        License: "Apache-2.0",
        NonCommercial: false,
        Origin: "TencentARC (GFPGAN), ONNX von FaceFusion",
        Requires: ["yunet", "faceparser"]);

    /// <summary>Gets the GFPGAN 1.3 face model.</summary>
    public static AiModel Gfpgan13 { get; } = new(
        "gfpgan13",
        "Gesichter – GFPGAN 1.3",
        "Natürlicher, etwas weicher als 1.4",
        "GFPGAN 1.3: ältere Fassung von GFPGAN, laut den Entwicklern natürlicher und mit weniger künstlichen " +
        "Details als 1.4. Nur die Gesichter werden verändert, der Rest des Bildes bleibt, wie er ist.",
        AiModelKind.Face,
        FaceFusion + "models-3.0.0/gfpgan_1.3.onnx",
        340_299_087,
        "d0dbdefc8974a4dd01161d99bd311967c18e63d6628fe40dba2bc60874271e47",
        IsZip: false,
        MainFile: "gfpgan_1.3.onnx",
        License: "Apache-2.0",
        NonCommercial: false,
        Origin: "TencentARC (GFPGAN), ONNX von FaceFusion",
        Requires: ["yunet", "faceparser"]);

    /// <summary>Gets the RestoreFormer++ face model.</summary>
    public static AiModel RestoreFormer { get; } = new(
        "restoreformer",
        "Gesichter – RestoreFormer++",
        "Echte, unscharfe Fotos, hält die Person gut",
        "RestoreFormer++: neueres Modell für echte, unscharfe oder verrauschte Fotos. Bleibt meist nah an der " +
        "Person und erfindet weniger Details. Nur die Gesichter werden verändert.",
        AiModelKind.Face,
        FaceFusion + "models-3.0.0/restoreformer_plus_plus.onnx",
        294_264_232,
        "164818b70d1745da4108a45c9263b05452236d14ac0ffab2202af0ac4a438195",
        IsZip: false,
        MainFile: "restoreformer_plus_plus.onnx",
        License: "Apache-2.0",
        NonCommercial: false,
        Origin: "Zhouxia Wang u. a. (RestoreFormer++), ONNX von FaceFusion",
        Requires: ["yunet", "faceparser"]);

    /// <summary>Gets the CodeFormer face model (non-commercial licence).</summary>
    public static AiModel CodeFormer { get; } = new(
        "codeformer",
        "Gesichter – CodeFormer",
        "Stark beschädigte Gesichter, alte Fotos",
        "CodeFormer: kommt auch mit sehr schlechten Gesichtern zurecht (alte Fotos, starke Kompression). " +
        "Nur die Gesichter werden verändert. Lizenz erlaubt nur nicht-kommerzielle Nutzung.",
        AiModelKind.Face,
        FaceFusion + "models-3.0.0/codeformer.onnx",
        376_951_650,
        "21710e7ab61c82683576c428e9c1b6fe1ed419586b7b39e394c3449c294b550f",
        IsZip: false,
        MainFile: "codeformer.onnx",
        License: "S-Lab-1.0",
        NonCommercial: true,
        Origin: "Shangchen Zhou (CodeFormer), ONNX von FaceFusion",
        Requires: ["yunet", "faceparser"]);

    // Fixed revision of the Hugging Face repository (commit), not "main"; otherwise the file could change
    private const string LamaRepo = "https://huggingface.co/Carve/LaMa-ONNX/resolve/c3c0c9e468934d62e79c329e35d82dd09ff8c444/";
    private const string MiganRepo = "https://huggingface.co/andraniksargsyan/migan/resolve/406830d0fa60666da0071c342ad2fbc8f30c5c64/";

    /// <summary>Gets the LaMa inpainting model (thorough).</summary>
    public static AiModel Lama { get; } = new(
        "lama",
        "LaMa (gründlich)",
        "Wasserzeichen, Schrift, Personen – beste Füllung",
        "LaMa (big-lama): malt markierte Stellen passend zur Umgebung neu, auch größere Flächen und Muster wie " +
        "Mauern, Gras oder Himmel. Etwas langsamer.",
        AiModelKind.Inpaint,
        LamaRepo + "lama_fp32.onnx",
        208_044_816,
        "1faef5301d78db7dda502fe59966957ec4b79dd64e16f03ed96913c7a4eb68d6",
        IsZip: false,
        MainFile: "lama_fp32.onnx",
        License: "Apache-2.0",
        NonCommercial: false,
        Origin: "Roman Suvorov u. a. (LaMa, Samsung AI Center), ONNX von Carve",
        TileSize: 512);

    /// <summary>Gets the MI-GAN inpainting model (fast).</summary>
    public static AiModel Migan { get; } = new(
        "migan",
        "MI-GAN (schnell)",
        "Kleine Stellen – schnell und klein",
        "MI-GAN: kleines, schnelles Modell zum Entfernen. Gut für kleine Stellen wie Schriftzüge oder Flecken; " +
        "bei großen Flächen ist LaMa meist besser.",
        AiModelKind.Inpaint,
        MiganRepo + "migan_pipeline_v2.onnx",
        28_079_181,
        "6f1f3530a1a2324b19752018ce756088b07973cda8d7d890034ace5c8a48c40b",
        IsZip: false,
        MainFile: "migan_pipeline_v2.onnx",
        License: "MIT",
        NonCommercial: false,
        Origin: "Andranik Sargsyan u. a. (MI-GAN, Picsart AI Research)",
        TileSize: 512);

    /// <summary>
    /// Gets the SyntheticDoc dewarping model (successor of UVDoc), which predicts the backward mapping of a photographed
    /// sheet as a 121 × 177 grid.
    /// </summary>
    /// <remarks>
    /// The authors' original checkpoint (PyTorch, ETH Zurich) is downloaded; the app builds the ONNX file from it on first
    /// use (<c>SyntheticDoc.SyntheticDocModel</c>, without Python). A self-trained checkpoint with the same name replaces it.
    /// See docs/KI-BEGRADIGEN.md.
    /// </remarks>
    public static AiModel SyntheticDoc { get; } = new(
        "syntheticdoc",
        "SyntheticDoc",
        "Gewölbtes Blatt mit KI begradigen – Bons, Bücher, zerknitterte Blätter",
        "SyntheticDoc: begradigt ein fotografiertes Blatt mit einem Klick, ohne Linien. Stark bei Knittern, Bons in der " +
        "Hand und Büchern; bei glatten Arbeitsblättern ist „Gewölbtes Blatt“ mit Linien genauer. Wird beim ersten Gebrauch " +
        "für die App umgewandelt.",
        AiModelKind.Dewarp,
        "https://igl.ethz.ch/projects/SyntheticDoc/highres_121x177.ckpt",
        101_733_025,
        "a9b69148fb3a8bf309757106f489df17a339a03f298f59d882f5ff51ba65a91d",
        IsZip: false,
        MainFile: "highres_121x177.ckpt",
        License: "MIT",
        NonCommercial: false,
        Origin: "Daniel Woortmann, Tanguy Magne, Olga Sorkine-Hornung (SyntheticDoc, ECCV 2026, ETH Zürich)");

    // Fixed revisions (commits), not "main"
    private const string UltraSharpRepo = "https://huggingface.co/Kim2091/UltraSharpV2/resolve/2d1db39ff692c111da92112d49f833e4ad7035ae/";
    // Stable Diffusion 2.1-base: Stability AI has withdrawn the original repository; this mirror holds the same files
    private const string Sd21Repo = "https://huggingface.co/Manojb/stable-diffusion-2-1-base/resolve/0094d483a120f3f33dafbd187ea4aa60d10de75c/";
    private const string Sd21License = "CreativeML-OpenRAIL++-M";
    private const string Sd21Origin = "Stability AI (Stable Diffusion 2.1-base), Spiegel „Manojb/stable-diffusion-2-1-base“ auf Hugging Face";

    /// <summary>Gets the medium-speed upscaling model 4x-UltraSharpV2 (DAT architecture), ONNX export by its author.</summary>
    public static AiModel UltraSharpV2 { get; } = new(
        "ultrasharpv2",
        "4x-UltraSharpV2",
        "Upscaling mittel – scharf und treu, etwa eine Minute je Megapixel",
        "UltraSharpV2 (Kim2091, 2025): vergrößert ×4, schärft Kanten und feine Strukturen, ohne viel dazuzuerfinden. " +
        "Deutlich langsamer als Real-ESRGAN x4v3. Lizenz erlaubt nur nicht-kommerzielle Nutzung.",
        AiModelKind.Upscale,
        UltraSharpRepo + "4x-UltraSharpV2_fp32_op17.onnx",
        51_800_517,
        "6c0201e3403745f39a9aa5273c50ed084cf6c4af5a71a4654c30252cf19bf0a5",
        IsZip: false,
        MainFile: "4x-UltraSharpV2_fp32_op17.onnx",
        License: "CC-BY-NC-SA-4.0",
        NonCommercial: true,
        Origin: "Kim2091 (UltraSharpV2, OpenModelDB)",
        Scale: 4,
        TileSize: 128,
        FixedTile: false,
        Gpu: true);

    /// <summary>
    /// Gets the slow upscaling model PiSA-SR (CVPR 2025, generative, invents fine detail): the authors' LoRA on Stable Diffusion 2.1.
    /// </summary>
    /// <remarks>
    /// The weights are downloaded (LoRA from Google Drive, SD 2.1 from the mirror); the app builds the ONNX files itself on
    /// first use (<c>Upscale.PiSaSrModel</c>, without Python).
    /// </remarks>
    public static AiModel PiSaSr { get; } = new(
        "pisasr",
        "PiSA-SR",
        "Upscaling langsam – erfindet feine Details (Nadeln, Blätter, Haut)",
        "PiSA-SR (Lingchen Sun u. a., CVPR 2025): Ein-Schritt-Diffusion auf Stable Diffusion 2.1. Ergänzt glaubhafte feine " +
        "Strukturen, wo das Original nur Matsch hat; die Treue-Sicherung hält Formen und Farben am Original. Sehr langsam auf " +
        "der CPU (Minuten je Megapixel). Lädt Stable Diffusion 2.1 (2,6 GB) mit und wird beim ersten Gebrauch umgewandelt.",
        AiModelKind.Upscale,
        "https://drive.usercontent.google.com/download?id=1PWFM5zUd6lNMOyKX64anvUXhKEOCrf8L&export=download&confirm=t",
        32_865_445,
        "13502f7bde5e4e585f84f24c3d904d9c848e068bf89f984046fe87da81a391f6",
        IsZip: false,
        MainFile: "pisa_sr.pkl",
        License: "Apache-2.0 (Code); SD 2.1: CreativeML-OpenRAIL++-M",
        NonCommercial: false,
        Origin: "Lingchen Sun, Rongyuan Wu, Zhiyuan Ma, Shuaizheng Liu, Qiaosi Yi, Lei Zhang (PiSA-SR, PolyU / OPPO)",
        Scale: 4,
        Requires: ["sd21-unet", "sd21-vae", "sd21-text"],
        Gpu: true);

    /// <summary>Gets the Stable Diffusion 2.1 UNet weights, part of PiSA-SR.</summary>
    public static AiModel Sd21Unet { get; } = new(
        "sd21-unet", "Stable Diffusion 2.1 – UNet", "Teil von PiSA-SR",
        "Das Herz von Stable Diffusion 2.1 (halbe Genauigkeit, reicht als Quelle — die App rechnet in voller). Wird mit PiSA-SR geladen.",
        AiModelKind.Part, Sd21Repo + "unet/diffusion_pytorch_model.fp16.safetensors", 1_731_904_736,
        "28ec9cf3b239c0751c201b1f6fb46b551df5862731b30a37aa1360101cb3fbab", IsZip: false,
        MainFile: "diffusion_pytorch_model.fp16.safetensors", License: Sd21License, NonCommercial: false, Origin: Sd21Origin);

    /// <summary>Gets the Stable Diffusion 2.1 VAE weights, part of PiSA-SR.</summary>
    public static AiModel Sd21Vae { get; } = new(
        "sd21-vae", "Stable Diffusion 2.1 – Bildkodierer (VAE)", "Teil von PiSA-SR",
        "Übersetzt zwischen Bild und dem inneren Raum von Stable Diffusion. Wird mit PiSA-SR geladen.",
        AiModelKind.Part, Sd21Repo + "vae/diffusion_pytorch_model.fp16.safetensors", 167_335_342,
        "3e4c08995484ee61270175e9e7a072b66a6e4eeb5f0c266667fe1f45b90daf9a", IsZip: false,
        MainFile: "diffusion_pytorch_model.fp16.safetensors", License: Sd21License, NonCommercial: false, Origin: Sd21Origin);

    /// <summary>Gets the Stable Diffusion 2.1 text encoder weights, part of PiSA-SR (needed only while converting).</summary>
    public static AiModel Sd21Text { get; } = new(
        "sd21-text", "Stable Diffusion 2.1 – Text-Encoder", "Teil von PiSA-SR",
        "Wird nur einmal gebraucht: PiSA-SR rechnet mit leerer Beschreibung, die die App beim Umwandeln daraus berechnet. " +
        "Wird mit PiSA-SR geladen.",
        AiModelKind.Part, Sd21Repo + "text_encoder/model.fp16.safetensors", 680_821_096,
        "681c555376658c81dc273f2d737a2aeb23ddb6d1d8e5b3a7064636d359a22668", IsZip: false,
        MainFile: "model.fp16.safetensors", License: Sd21License, NonCommercial: false, Origin: Sd21Origin);

    // Background removal (docs/KI-HINTERGRUND.md): BiRefNet from the author's release, the others from fixed commits
    private const string BiRefNetRelease = "https://github.com/ZhengPeng7/BiRefNet/releases/download/v1/";
    private const string BiRefNetOnnxRepo = "https://huggingface.co/onnx-community/BiRefNet-ONNX/resolve/534d3c82d3bb8b2f0867db6dfbc3a525b8e42f67/";
    private const string Ben2Repo = "https://huggingface.co/PramaLLC/BEN2/resolve/e48a20765fb421d19dcdb0bf3cc61e802ca5ec8f/";

    /// <summary>Gets BiRefNet general (1024 px), the default model for background removal.</summary>
    public static AiModel BiRefNetGeneral { get; } = new(
        "birefnet-general",
        "BiRefNet",
        "Hintergrund entfernen – sauberstes Motiv, Standard",
        "BiRefNet (2024): erkennt das Motiv und stellt es mit feinen Kanten frei, auch Haare und Durchsichtiges. " +
        "Lässt Personen und Dinge im Hintergrund am zuverlässigsten weg. Rund 25 s je Bild auf der CPU.",
        AiModelKind.Background,
        BiRefNetRelease + "BiRefNet-general-epoch_244.onnx",
        972_666_916,
        "58f621f00f5d756097615970a88a791584600dcf7c45b18a0a6267535a1ebd3c",
        IsZip: false,
        MainFile: "BiRefNet-general-epoch_244.onnx",
        License: "MIT",
        NonCommercial: false,
        Origin: "Peng Zheng u. a. (BiRefNet, CAAI AIR 2024), ONNX vom Autor",
        TileSize: 1024);

    /// <summary>
    /// Gets BiRefNet general in half precision for the graphics card: DirectML cannot run the full-precision file on an
    /// integrated graphics card. Optional; used instead of <see cref="BiRefNetGeneral"/> when the AI runs on the graphics card.
    /// </summary>
    public static AiModel BiRefNetGeneralGpu { get; } = new(
        "birefnet-general-gpu",
        "BiRefNet · Zusatz für die Grafikkarte",
        "Nur nötig, wenn die KI auf der Grafikkarte rechnet – etwa doppelt so schnell",
        "Dieselbe KI in halber Genauigkeit. Die App nimmt sie statt der vollen, wenn die Grafikkarte rechnet; das " +
        "Ergebnis ist fast gleich (im Mittel unter 0,1 Stufen Unterschied). Ohne Grafikkarte überflüssig.",
        AiModelKind.Background,
        BiRefNetOnnxRepo + "onnx/model_fp16.onnx",
        489_666_272,
        "3654c741eb80bd926ada8fed1713b506ccf8d30eb1f6487e87eb9f234f33df09",
        IsZip: false,
        MainFile: "model_fp16.onnx",
        License: "MIT",
        NonCommercial: false,
        Origin: "Peng Zheng u. a. (BiRefNet), ONNX in halber Genauigkeit von onnx-community",
        TileSize: 1024,
        Gpu: true,
        GpuWithoutOptimization: true);

    /// <summary>Gets BiRefNet lite (Swin tiny, 1024 px): about twice as fast, leaves more remnants.</summary>
    public static AiModel BiRefNetLite { get; } = new(
        "birefnet-lite",
        "BiRefNet lite",
        "Hintergrund entfernen – schnell",
        "Kleine Fassung von BiRefNet: etwa doppelt so schnell, lässt aber öfter Reste vom Hintergrund stehen.",
        AiModelKind.Background,
        BiRefNetRelease + "BiRefNet-general-bb_swin_v1_tiny-epoch_232.onnx",
        224_005_088,
        "5600024376f572a557870a5eb0afb1e5961636bef4e1e22132025467d0f03333",
        IsZip: false,
        MainFile: "BiRefNet-general-bb_swin_v1_tiny-epoch_232.onnx",
        License: "MIT",
        NonCommercial: false,
        Origin: "Peng Zheng u. a. (BiRefNet lite), ONNX vom Autor",
        TileSize: 1024,
        Gpu: true,
        GpuWithoutOptimization: true);

    /// <summary>Gets BEN2 base: finest hair, but keeps people in the background more often. CPU only (differs on DirectML).</summary>
    public static AiModel Ben2 { get; } = new(
        "ben2",
        "BEN2",
        "Hintergrund entfernen – feine Haare",
        "BEN2 (2025): rechnet unsichere Randpixel ein zweites Mal nach – besonders gut bei abstehenden Haaren und Fell. " +
        "Behält dafür öfter Personen im Hintergrund; dann mit dem roten Pinsel nachhelfen.",
        AiModelKind.Background,
        Ben2Repo + "BEN2_Base.onnx",
        222_932_053,
        "22cea62108ff53b7ccc20f7a008bf30494228d84b1687f29ecbe76936a998101",
        IsZip: false,
        MainFile: "BEN2_Base.onnx",
        License: "MIT",
        NonCommercial: false,
        Origin: "Maxwell Meyer, Jack Spruyt (BEN2, Prama LLC)",
        TileSize: 1024);

    /// <summary>
    /// Gets RMBG-2.0 by Bria (BiRefNet architecture, trained on licensed data). Non-commercial only and downloadable only after
    /// agreeing to Bria's terms, so it has no download address: the user places the file by hand (<see cref="AiModel.ManualPage"/>).
    /// </summary>
    public static AiModel Rmbg2 { get; } = new(
        "rmbg-2.0",
        "RMBG-2.0",
        "Hintergrund entfernen – etwa wie BiRefNet, nur nicht-kommerziell",
        "RMBG-2.0 von Bria: gleiches Netz wie BiRefNet, mit lizenzierten Bildern trainiert. Bria gibt die Datei nur nach " +
        "Zustimmung frei: 1. Bei Hugging Face anmelden und auf der Seite des Modells das Formular ausfüllen (Name, E-Mail, " +
        "„Non commercial use“). 2. Im selben Browser „onnx/model.onnx“ herunterladen (1,02 GB). 3. Die Datei in den Ordner " +
        "legen, den „Ordner öffnen“ zeigt – die App erkennt sie dann von selbst.",
        AiModelKind.Background,
        "",
        1_024_331_469,
        "5b486f08200f513f460da46dd701db5fbb47d79b4be4b708a19444bcd4e79958",
        IsZip: false,
        MainFile: "model.onnx",
        License: "CC-BY-NC-4.0",
        NonCommercial: true,
        Origin: "Bria AI (RMBG-2.0), Datei von Hugging Face „briaai/RMBG-2.0“ nach Zustimmung",
        TileSize: 1024,
        Gpu: true,
        ManualPage: "https://huggingface.co/briaai/RMBG-2.0");

    /// <summary>Gets all models of the catalogue.</summary>
    public static IReadOnlyList<AiModel> All { get; } =
        [Allround, Gfpgan, Gfpgan13, RestoreFormer, CodeFormer, Lama, Migan, SyntheticDoc, UltraSharpV2, PiSaSr, Sd21Unet, Sd21Vae, Sd21Text,
         BiRefNetGeneral, BiRefNetGeneralGpu, BiRefNetLite, Ben2, Rmbg2, FaceDetector, FaceParser];

    /// <summary>Looks up a model by its ID.</summary>
    /// <param name="id">Model ID (<see cref="AiModel.Id"/>), compared case-sensitively.</param>
    /// <returns>The model, or null if the catalogue has no model with this ID.</returns>
    public static AiModel? Find(string id) => All.FirstOrDefault(m => m.Id == id);
}
