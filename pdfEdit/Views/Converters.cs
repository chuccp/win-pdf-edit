using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using PdfEdit.ViewModels;

namespace PdfEdit.Views;

/// <summary>
/// Two-way binding between a radio/check box and an enum value, so the tool buttons can be
/// declared purely in XAML and still drive <see cref="MainViewModel.CurrentTool"/>.
/// </summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null && parameter is not null &&
           string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true || parameter is null) return Binding.DoNothing;

        var enumType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        return enumType.IsEnum && Enum.TryParse(enumType, parameter.ToString(), out var parsed)
            ? parsed
            : Binding.DoNothing;
    }
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;
}

/// <summary>Swatch colour for an annotation, so the toolbar shows what is picked.</summary>
public sealed class SpecToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Models.AnnotationSpec spec
            ? new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(spec.R, spec.G, spec.B))
            : System.Windows.Media.Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Picks the shape template matching an annotation's geometry.</summary>
public sealed class AnnotationTemplateSelector : DataTemplateSelector
{
    public DataTemplate? BoxTemplate { get; set; }
    public DataTemplate? EllipseTemplate { get; set; }
    public DataTemplate? InkTemplate { get; set; }
    public DataTemplate? NoteTemplate { get; set; }
    public DataTemplate? LineTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container) => item switch
    {
        AnnotationVisual { InkPoints: not null } => InkTemplate,
        AnnotationVisual { IsLine: true } => LineTemplate,
        AnnotationVisual { IsNote: true } => NoteTemplate,
        AnnotationVisual { IsEllipse: true } => EllipseTemplate,
        AnnotationVisual => BoxTemplate,
        _ => base.SelectTemplate(item, container),
    };
}
