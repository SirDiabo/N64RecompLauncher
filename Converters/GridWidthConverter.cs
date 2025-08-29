using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace N64RecompLauncher
{
    public class GridWidthConverter : IValueConverter
    {
        public static readonly GridWidthConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double slotSize)
            {
                return slotSize * 1.2;
            }
            return 120.0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}