using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ImageConverter.ViewModels;

namespace ImageConverter.Views;

/// <summary>
/// Small prompt with several answers stacked vertically (e.g. "Whole image (≈ 12 min)", "Selection only", "Cancel").
/// Result: index of the chosen answer, −1 if the window is closed without an answer.
/// </summary>
public partial class ChoiceWindow : Window
{
    private int _result = -1;

    // For the XAML designer.
    public ChoiceWindow()
    {
        InitializeComponent();
    }

    public ChoiceWindow(ChoiceRequest request) : this()
    {
        Title = request.Title;
        Heading.Text = request.Title;
        Message.Text = request.Message;
        Message.IsVisible = !string.IsNullOrEmpty(request.Message);
        for (var i = 0; i < request.Choices.Count; i++)
        {
            var index = i;
            var button = new Button
            {
                // Wrap long answers (duration, time of day) instead of truncating them.
                Content = new TextBlock { Text = request.Choices[i], TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            if (i == request.Default) button.Classes.Add("primary");
            button.Click += (_, _) =>
            {
                _result = index;
                Close();
            };
            Buttons.Children.Add(button);
        }
    }

    public int Result => _result;
}
