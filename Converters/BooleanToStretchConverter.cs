using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace N64RecompLauncher
{
    public class BooleanToStretchConverter : IValueConverter
    {
        public static readonly BooleanToStretchConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return boolValue ? Stretch.Fill : Stretch.Uniform;
            }
            return Stretch.Uniform;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}