using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace N64RecompLauncher
{
    public class GridWidthMultiConverter : IMultiValueConverter
    {
        public static readonly GridWidthMultiConverter Instance = new();

        public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values.Count >= 2 &&
                values[0] is double slotSize &&
                values[1] is bool isPortrait)
            {
                if (isPortrait)
                {
                    return slotSize * 0.73;
                }
                return slotSize * 1.43;
            }
            return 120.0;
        }

        public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}