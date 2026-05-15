using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace VideoFixPro;

// Value converter for status colour in XAML bindings
public class StatusColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string hex)
            try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
            catch { }
        return Brushes.Gray;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotImplementedException();
}
