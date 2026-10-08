using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using ImageConverter.Core.Localization;

namespace ImageConverter.Localization;

/// <summary>
/// Übersetzter Text in XAML: <c>Text="{l:T 'Bilder hierher ziehen'}"</c>. Der deutsche Text
/// ist der Schlüssel (<see cref="Tr"/>). Gebunden an <see cref="Tr.Version"/> — beim
/// Sprachwechsel wechselt der Text sofort.
/// </summary>
/// <remarks>
/// Texte mit geschweiften Klammern (z. B. „{name}“) als Element schreiben:
/// <c>&lt;ToolTip.Tip&gt;&lt;l:T Text="Platzhalter: {name} …" /&gt;&lt;/ToolTip.Tip&gt;</c>.
/// </remarks>
public sealed class T : MarkupExtension
{
    public T() { }
    public T(string text) => Text = text;

    public string Text { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding(nameof(Tr.Version))
        {
            Source = Tr.Instance,
            Mode = BindingMode.OneWay,
            Converter = new FixedText(Text),
        };

    private sealed class FixedText(string german) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Tr.T(german);
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}

/// <summary>
/// Übersetzt einen gebundenen deutschen Text — für Listeneinträge, deren Wert der deutsche
/// Text ist: <c>&lt;TextBlock Text="{l:TItem}" /&gt;</c> (der Eintrag selbst) oder
/// <c>{l:TItem Path=Description}</c>. Wechselt ebenfalls sofort mit der Sprache.
/// </summary>
public sealed class TItem : MarkupExtension
{
    public string Path { get; set; } = ".";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new MultiBinding
        {
            Bindings =
            {
                new Binding(Path),
                new Binding(nameof(Tr.Version)) { Source = Tr.Instance },
            },
            Converter = Translate.Instance,
        };

    private sealed class Translate : IMultiValueConverter
    {
        public static readonly Translate Instance = new();

        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
            values.Count > 0 && values[0] is string german ? Tr.T(german) : values.Count > 0 ? values[0] : null;
    }
}
