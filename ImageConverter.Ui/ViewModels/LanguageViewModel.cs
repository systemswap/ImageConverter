using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageConverter.Core.Localization;
using ImageConverter.Core.Platform;
using ImageConverter.Services;

namespace ImageConverter.ViewModels;

/// <summary>
/// Tab „Sprache“ in den Einstellungen: eingebaute Sprachen und JSON-Dateien aus dem
/// Ordner languages. Die Wahl gilt sofort und wird gespeichert.
/// </summary>
public partial class LanguageViewModel : ObservableObject
{
    public ObservableCollection<Language> Available { get; } = [];

    [ObservableProperty] public partial Language? Selected { get; set; }

    /// <summary>Fehlerhafte Sprachdateien, je Zeile eine; null = keine.</summary>
    [ObservableProperty] public partial string? Problems { get; set; }

    [ObservableProperty] public partial string? Status { get; set; }

    public string Folder => Languages.Folder;

    public LanguageViewModel() => Rescan();

    /// <summary>Ordner neu einlesen — z. B. nachdem eine Datei hineinkopiert wurde.</summary>
    public void Rescan()
    {
        var all = Languages.All(out var problems);
        Available.Clear();
        foreach (var l in all) Available.Add(l);
        Problems = problems.Count > 0 ? string.Join("\n", problems) : null;

        // Aktuelle Sprache wiederfinden (nach Neueinlesen ist es ein neues Objekt)
        _syncing = true;
        Selected = all.FirstOrDefault(l => l.Code == Tr.Current.Code) ?? all[0];
        _syncing = false;
        if (!ReferenceEquals(Selected, Tr.Current)) Tr.Use(Selected);
    }

    private bool _syncing;

    partial void OnSelectedChanged(Language? value)
    {
        if (_syncing || value is null) return;
        Tr.Use(value);
        AppSettings.Update(s => s with { Language = value.Code });
        Status = null;
    }

    /// <summary>
    /// Sprache beim Programmstart: gespeicherte Wahl, sonst Systemsprache.
    /// </summary>
    public static void ApplyAtStartup()
    {
        var all = Languages.All(out _);
        var saved = AppSettings.Load().Language;
        Tr.Use(all.FirstOrDefault(l => l.Code == saved) ?? Languages.ForSystem(all));
    }

    [RelayCommand]
    private void OpenFolder()
    {
        Directory.CreateDirectory(Folder);
        if (!FolderOpener.TryOpen(Folder))
            Status = Tr.T("Kein Dateimanager gefunden. Ordner: {0}", Folder);
    }

    [RelayCommand]
    private void WriteTemplate()
    {
        try
        {
            var path = Languages.WriteTemplate();
            Status = Tr.T("Vorlage gespeichert: {0}", path);
            FolderOpener.TryOpen(Path.GetDirectoryName(path)!);
        }
        catch (IOException ex)
        {
            Status = Tr.T("Vorlage ließ sich nicht speichern: {0}", ex.Message);
        }
        catch (System.UnauthorizedAccessException)
        {
            Status = Tr.T("Kein Schreibrecht im Ordner {0}", Folder);
        }
    }
}
