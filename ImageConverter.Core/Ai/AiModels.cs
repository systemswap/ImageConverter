namespace ImageConverter.Core.Ai;

public enum AiModelKind
{
    /// <summary>Ganzes Bild in Kacheln: Rauschen, Artefakte, Unschärfe, Vergrößern.</summary>
    Enhance,

    /// <summary>Nur Gesichter: erkennen, ausrichten, verbessern, zurücksetzen.</summary>
    Face,

    /// <summary>Wird von anderen Modellen gebraucht (Gesichtserkennung), nicht selbst wählbar.</summary>
    Helper,
}

/// <summary>
/// Ein herunterladbares Modell. Liegt nach dem Download in einem eigenen Unterordner
/// <c>KI-Modelle/&lt;Id&gt;/</c>; vorhanden ist es, sobald <see cref="MainFile"/> dort liegt.
/// </summary>
/// <param name="Url">Download-Adresse — eine .onnx-Datei oder ein .zip (<see cref="IsZip"/>).</param>
/// <param name="Sha256">Prüfsumme des Downloads; stimmt sie nicht, wird nichts gespeichert.</param>
/// <param name="Scale">Vergrößerung des Modells (Enhance), sonst 1.</param>
/// <param name="TileSize">Kachelgröße (Enhance) bzw. Gesichtsgröße (Face) in Pixeln.</param>
/// <param name="FixedTile">Modell nimmt nur genau diese Kachelgröße an.</param>
/// <param name="Requires">Ids weiterer Modelle, die mit heruntergeladen werden.</param>
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
    string[]? Requires = null)
{
    public string[] Requires { get; init; } = Requires ?? [];

    public string SizeText => DownloadSize >= 1024 * 1024
        ? $"{DownloadSize / (1024.0 * 1024):0.#} MB"
        : $"{DownloadSize / 1024.0:0} KB";

    public override string ToString() => Name;
}

/// <summary>
/// Die angebotenen Modelle. Bewusst nur Modelle mit bekannter Lizenz; Quellen sind
/// feste, versionierte Releases. Die Prüfsummen schützen vor kaputten oder
/// ausgetauschten Dateien.
/// </summary>
public static class AiModelCatalog
{
    private const string FaceFusion = "https://github.com/facefusion/facefusion-assets/releases/download/";

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
        FixedTile: true);

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
        Requires: ["yunet"]);

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
        Requires: ["yunet"]);

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
        Requires: ["yunet"]);

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
        Requires: ["yunet"]);

    public static IReadOnlyList<AiModel> All { get; } = [Allround, Gfpgan, Gfpgan13, RestoreFormer, CodeFormer, FaceDetector];

    public static AiModel? Find(string id) => All.FirstOrDefault(m => m.Id == id);
}
