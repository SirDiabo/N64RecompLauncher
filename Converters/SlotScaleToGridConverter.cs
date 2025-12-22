using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace N64RecompLauncher
{
    public class SlotScaleToGridConverter : IValueConverter
    {
        public static readonly SlotScaleToGridConverter Instance = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is double slotSize && parameter is string mode)
            {
                bool showGrid = slotSize >= 200;

                if (mode == "Grid")
                    return showGrid;
                else if (mode == "List")
                    return !showGrid;
            }
            return parameter?.ToString() == "List";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}