using System;
using System.Globalization;
using System.Windows.Data;

namespace N64RecompLauncher
{
    public class BoolInverterConverter : IValueConverter
    {
        public static readonly BoolInverterConverter Instance = new BoolInverterConverter();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return !(bool)value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return !(bool)value;
        }
    }
}