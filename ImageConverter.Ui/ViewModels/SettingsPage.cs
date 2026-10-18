namespace ImageConverter.ViewModels;

/// <summary>
/// Seiten des Einstellungsfensters in der Reihenfolge der linken Leiste: Leistung, Allgemein, KI und darunter eingerückt
/// je Anwendung eine Seite mit ihren Modellen.
/// </summary>
public enum SettingsPage
{
    Performance,
    General,
    Ai,
    Improve,
    Faces,
    Remove,
    Dewarp,
    Upscale,
}
