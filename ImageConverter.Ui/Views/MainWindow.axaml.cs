using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using ImageConverter.ViewModels;

namespace ImageConverter.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Version kommt aus Directory.Build.props, z. B. "ImageConverter 0.4.1"
        var v = typeof(MainWindow).Assembly.GetName().Version;
        if (v is not null)
            Title = $"ImageConverter {v.Major}.{v.Minor}.{v.Build}";
    }

    /// <summary>
    /// Rückgängig/Wiederholen fensterweit. Bewusst per OnKeyDown statt KeyBindings:
    /// ausgewertet wird die beschriftete Taste (wichtig für QWERTZ mit vertauschtem
    /// Y/Z), und Textfelder, die ihr eigenes Strg+Z schon behandelt haben, kommen
    /// hier gar nicht erst an.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || DataContext is not MainViewModel vm) return;
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;

        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        ICommand? command = e.Key switch
        {
            Key.Z when !shift => vm.UndoCommand,
            Key.Z when shift => vm.RedoCommand,
            Key.Y => vm.RedoCommand,
            Key.P => vm.PrintCommand,
            _ => null,
        };

        if (command?.CanExecute(null) == true)
        {
            command.Execute(null);
            e.Handled = true;
        }
    }
}
