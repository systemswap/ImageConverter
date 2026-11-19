namespace ImageConverter.ViewModels;

/// <summary>
/// Pages of the settings window in the order of the left-hand sidebar: General (opened first), Performance, AI and,
/// indented below it, one page per AI application with its models.
/// </summary>
public enum SettingsPage
{
    General,
    Performance,
    Ai,
    Improve,
    Faces,
    Remove,
    Background,
    Dewarp,
    Upscale,
}
