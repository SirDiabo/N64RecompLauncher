using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace N64RecompLauncher
{
    public class SlotScaleToGridConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double slotScale && parameter is string mode)
            {
                bool isGridMode = slotScale > 120;

                if (mode == "Grid")
                    return isGridMode ? Visibility.Visible : Visibility.Collapsed;
                else
                    return isGridMode ? Visibility.Collapsed : Visibility.Visible;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}