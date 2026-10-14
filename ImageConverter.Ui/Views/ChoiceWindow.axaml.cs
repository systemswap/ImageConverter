using Avalonia.Controls;
using Avalonia.Layout;
using ImageConverter.ViewModels;

namespace ImageConverter.Views;

/// <summary>
/// Kleine Rückfrage mit mehreren Antworten untereinander (z. B. „Ganzes Bild (≈ 12 min)“, „Nur Ausschnitt“, „Abbrechen“).
/// Ergebnis: Nummer der Antwort, −1 beim Schließen über das Fenster.
/// </summary>
public partial class ChoiceWindow : Window
{
    private int _result = -1;

    // Für den XAML-Designer
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
                Content = request.Choices[i],
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
