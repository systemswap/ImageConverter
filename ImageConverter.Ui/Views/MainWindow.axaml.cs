using System.Linq;
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

        // The version comes from Directory.Build.props, e.g. "ImageConverter 0.4.1", including a suffix
        // ("0.4.41-beta" → "0.4.41 beta"); the part after "+" is the commit and is dropped.
        var info = typeof(MainWindow).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
        var v = typeof(MainWindow).Assembly.GetName().Version;
        var text = info is not null ? info.Split('+')[0].Replace('-', ' ')
            : v is not null ? $"{v.Major}.{v.Minor}.{v.Build}" : null;
        if (text is not null)
            Title = $"ImageConverter {text}";
    }

    /// <summary>
    /// Window-wide undo/redo (and Ctrl+P for printing). Deliberately handled in OnKeyDown instead of
    /// KeyBindings: the labelled key is evaluated (important for QWERTZ layouts with Y and Z swapped),
    /// and text boxes that already handled their own Ctrl+Z never reach this point.
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
