using System.IO.Compression;
using System.Security.Cryptography;
using ImageConverter.Core.Platform;

namespace ImageConverter.Core.Ai;

/// <summary>
/// Location of the AI models on disk.
/// </summary>
/// <remarks>
/// By default the folder <c>KI-Modelle</c> next to the .exe or next to the AppImage file. If that folder is not writable
/// (for example under C:\Program Files), downloads go to the user's local application data folder instead; models are
/// looked up in both places. Each model lives in a subfolder named after <see cref="AiModel.Id"/>.
/// </remarks>
public sealed class AiModelStore
{
    /// <summary>Name of the model folder.</summary>
    public const string FolderName = "KI-Modelle";

    /// <summary>Creates a store with the given search locations.</summary>
    /// <param name="folders">Search locations in order of preference; the first writable one receives downloads. Must not be empty.</param>
    public AiModelStore(IReadOnlyList<string> folders) => Folders = folders;

    /// <summary>Gets the search locations in order of preference.</summary>
    public IReadOnlyList<string> Folders { get; }

    /// <summary>Creates the default store: next to the program, with the local application data folder as fallback.</summary>
    /// <returns>A store with these two search locations.</returns>
    public static AiModelStore CreateDefault()
    {
        // For an AppImage next to the .AppImage file, not inside the read-only image.
        var baseDir = AppLocations.ProgramFolder;

        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImageConverter", FolderName);

        return new AiModelStore([Path.Combine(baseDir, FolderName), fallback]);
    }

    /// <summary>
    /// Gets the download location: the first folder that is writable, otherwise the last one.
    /// </summary>
    /// <remarks>Each access probes the folders by creating them and writing and deleting a test file.</remarks>
    public string DownloadFolder => Folders.FirstOrDefault(IsWritable) ?? Folders[^1];

    /// <summary>Returns the path of the model's main file.</summary>
    /// <param name="model">Model to look up.</param>
    /// <returns>The path of <see cref="AiModel.MainFile"/> in the first search location that has it, or null if the model is not installed.</returns>
    public string? PathOf(AiModel model) =>
        Folders.Select(f => Path.Combine(f, model.Id, model.MainFile)).FirstOrDefault(File.Exists);

    /// <summary>Returns whether the model is installed in one of the search locations.</summary>
    /// <param name="model">Model to look up.</param>
    /// <returns>True if <see cref="PathOf"/> finds the main file.</returns>
    public bool IsInstalled(AiModel model) => PathOf(model) is not null;

    /// <summary>Deletes the model folder from every search location.</summary>
    /// <param name="model">Model to delete; models it requires are not deleted.</param>
    public void Delete(AiModel model)
    {
        foreach (var folder in Folders)
        {
            var dir = Path.Combine(folder, model.Id);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Downloads a model into <see cref="DownloadFolder"/>.
    /// </summary>
    /// <remarks>
    /// The data is written to a ".part" file first; it becomes the model only if the checksum matches. On cancellation
    /// or error the ".part" file is deleted and an empty model folder is removed, so no half-installed model remains.
    /// Models listed in <see cref="AiModel.Requires"/> are not downloaded by this method.
    /// </remarks>
    /// <param name="model">Model to download.</param>
    /// <param name="http">HTTP client used for the download; it stays owned by the caller.</param>
    /// <param name="progress">Receives the downloaded fraction, 0…1.</param>
    /// <param name="ct">Cancels the download.</param>
    /// <returns>A task that completes when the model is installed.</returns>
    /// <exception cref="InvalidDataException">The checksum of the downloaded file does not match <see cref="AiModel.Sha256"/>.</exception>
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
            // An empty folder after cancellation must not remain as a "half model"
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
    }

    /// <summary>
    /// Takes over a model file the user downloaded by hand (models without a download address, such as RMBG-2.0): copies
    /// it into <see cref="DownloadFolder"/> if size and checksum match.
    /// </summary>
    /// <remarks>Like <see cref="DownloadAsync"/>, the copy goes to a ".part" file first; the source stays where it is.</remarks>
    /// <param name="model">Model the file belongs to.</param>
    /// <param name="file">The downloaded file.</param>
    /// <param name="progress">Receives the copied fraction, 0…1.</param>
    /// <param name="ct">Cancels the copy.</param>
    /// <returns>A task that completes when the model is installed.</returns>
    /// <exception cref="InvalidDataException">Size or checksum do not match: a different or incomplete file.</exception>
    public async Task ImportAsync(AiModel model, string file, IProgress<double>? progress, CancellationToken ct)
    {
        // The size of a symbolic link is that of the link itself (a few bytes); the file behind it counts (No. 89)
        var info = new FileInfo(file);
        var total = (info.ResolveLinkTarget(returnFinalTarget: true) as FileInfo ?? info).Length;
        if (total != model.DownloadSize)
            throw new InvalidDataException(Tr.T("Das ist nicht die erwartete Datei: {0} statt {1} Bytes. Gebraucht wird „{2}“.",
                total, model.DownloadSize, model.MainFile));

        var dir = Path.Combine(DownloadFolder, model.Id);
        Directory.CreateDirectory(dir);
        var part = Path.Combine(dir, model.MainFile + ".part");
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
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
                    progress?.Report(done / (double)total);
                }
            }

            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!string.Equals(actual, model.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(Tr.T("Die Datei hat die richtige Größe, aber nicht die erwartete Prüfsumme — eine andere Fassung des Modells?"));
            File.Move(part, Path.Combine(dir, model.MainFile), overwrite: true);
        }
        finally
        {
            if (File.Exists(part)) File.Delete(part);
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
    }

    /// <summary>
    /// Extracts only files, without subfolders. Paths from the archive are discarded, so nothing can be written
    /// outside the model folder. The .onnx files come last, so that the main model counts as installed only when
    /// everything else is in place.
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
