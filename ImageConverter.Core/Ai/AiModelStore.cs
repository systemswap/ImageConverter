using System.IO.Compression;
using System.Security.Cryptography;
using ImageConverter.Core.Platform;

namespace ImageConverter.Core.Ai;

/// <summary>
/// Ort der Modelle auf der Platte: Ordner „KI-Modelle“ neben der .exe bzw. neben der
/// AppImage-Datei. Ist dort kein Schreiben möglich (z. B. unter C:\Programme), weicht
/// der Download ins Benutzerverzeichnis aus; gesucht wird an beiden Stellen.
/// </summary>
public sealed class AiModelStore
{
    public const string FolderName = "KI-Modelle";

    /// <param name="folders">Suchorte, der erste beschreibbare nimmt Downloads auf.</param>
    public AiModelStore(IReadOnlyList<string> folders) => Folders = folders;

    public IReadOnlyList<string> Folders { get; }

    /// <summary>Standard: neben dem Programm, ersatzweise im Benutzerverzeichnis.</summary>
    public static AiModelStore CreateDefault()
    {
        // Bei einer AppImage neben der .AppImage-Datei, nicht im schreibgeschützten Inneren.
        var baseDir = AppLocations.ProgramFolder;

        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImageConverter", FolderName);

        return new AiModelStore([Path.Combine(baseDir, FolderName), fallback]);
    }

    /// <summary>Wohin heruntergeladen wird: der erste Ordner, in den sich schreiben lässt.</summary>
    public string DownloadFolder => Folders.FirstOrDefault(IsWritable) ?? Folders[^1];

    /// <summary>Wo das Modell liegt — oder null, wenn es (noch) nicht da ist.</summary>
    public string? PathOf(AiModel model) =>
        Folders.Select(f => Path.Combine(f, model.Id, model.MainFile)).FirstOrDefault(File.Exists);

    public bool IsInstalled(AiModel model) => PathOf(model) is not null;

    /// <summary>Entfernt das Modell überall, wo es liegt.</summary>
    public void Delete(AiModel model)
    {
        foreach (var folder in Folders)
        {
            var dir = Path.Combine(folder, model.Id);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Lädt ein Modell herunter. Geschrieben wird erst in eine „.part“-Datei; nur bei
    /// passender Prüfsumme wird sie zum Modell. Abbruch oder Fehler hinterlassen nichts.
    /// </summary>
    /// <param name="progress">Anteil 0…1.</param>
    public async Task DownloadAsync(AiModel model, HttpClient http, IProgress<double>? progress, CancellationToken ct)
    {
        var dir = Path.Combine(DownloadFolder, model.Id);
        Directory.CreateDirectory(dir);
        var part = Path.Combine(dir, (model.IsZip ? "download.zip" : model.MainFile) + ".part");

        try
        {
            using var response = await http.GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? model.DownloadSize;

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    hash.AppendData(buffer, 0, read);
                    done += read;
                    progress?.Report(total > 0 ? Math.Min(1, done / (double)total) : 0);
                }
            }

            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!string.Equals(actual, model.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    Tr.T("Die heruntergeladene Datei ist beschädigt oder wurde verändert (Prüfsumme stimmt nicht)."));

            if (model.IsZip)
                ExtractFlat(part, dir);
            else
                File.Move(part, Path.Combine(dir, model.MainFile), overwrite: true);
        }
        finally
        {
            if (File.Exists(part)) File.Delete(part);
            // Leerer Ordner nach Abbruch soll nicht als „halbes Modell“ liegen bleiben
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
    }

    /// <summary>
    /// Entpackt nur Dateien, ohne Unterordner — Pfade aus dem Archiv werden nicht
    /// übernommen (kein Schreiben außerhalb des Modellordners). Das Hauptmodell
    /// kommt zuletzt, damit es erst „vorhanden“ ist, wenn alles da ist.
    /// </summary>
    private static void ExtractFlat(string zipPath, string dir)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries
                     .Where(e => !string.IsNullOrEmpty(e.Name))
                     .OrderBy(e => e.Name.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)))
        {
            entry.ExtractToFile(Path.Combine(dir, Path.GetFileName(entry.Name)), overwrite: true);
        }
    }

    private static bool IsWritable(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, $".schreibtest-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
