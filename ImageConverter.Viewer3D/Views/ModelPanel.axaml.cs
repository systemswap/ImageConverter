using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace ImageConverter.Viewer3D.Views;

/// <summary>Column beside the model: its figures, how it is drawn, and the measurements.</summary>
public partial class ModelPanel : UserControl
{
    public ModelPanel() => AvaloniaXamlLoader.Load(this);

    /// <summary>Double click on the brightness value: back to 100 %.</summary>
    private void OnBrightnessReset(object? sender, TappedEventArgs e) => (DataContext as ModelViewModel)?.ResetBrightnessCommand.Execute(null);
}
