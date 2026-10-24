namespace ImageConverter.ViewModels;

/// <summary>
/// Pages of the settings window in the order of the left-hand sidebar: Performance, General, AI and, indented
/// below it, one page per AI application with its models.
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
