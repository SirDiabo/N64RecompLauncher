using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace N64RecompLauncher
{
    public class BoolInverterConverter : IValueConverter
    {
        public static readonly BoolInverterConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return !boolValue;
            }
            return true;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return !boolValue;
            }
            return false;
        }
    }
}