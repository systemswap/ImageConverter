using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using ImageConverter.Core.Localization;

namespace ImageConverter.Localization;

/// <summary>
/// Translated text in XAML: <c>Text="{l:T 'Bilder hierher ziehen'}"</c>. The German text is the
/// key (<see cref="Tr"/>). Bound to <see cref="Tr.Version"/>, so the text changes immediately when
/// the language changes.
/// </summary>
/// <remarks>
/// Write texts containing curly braces (e.g. "{name}") as an element:
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
/// Translates a bound German text, for list entries whose value is the German text:
/// <c>&lt;TextBlock Text="{l:TItem}" /&gt;</c> (the entry itself) or
/// <c>{l:TItem Path=Description}</c>. Also changes immediately with the language.
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
