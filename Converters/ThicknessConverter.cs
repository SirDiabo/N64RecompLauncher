using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia;

namespace N64RecompLauncher
{
    public class ThicknessConverter : IValueConverter
    {
        public static readonly ThicknessConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double doubleValue)
            {
                return new Thickness(doubleValue, 0, 0, 0);
            }
            if (value is int intValue)
            {
                return new Thickness(intValue, 0, 0, 0);
            }
            return new Thickness(0);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}