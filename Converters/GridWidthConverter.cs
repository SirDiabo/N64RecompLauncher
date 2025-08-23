using System.Globalization;
using System.Windows.Data;

namespace N64RecompLauncher
{
    public class GridWidthConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double slotScale)
            {
                return Math.Max(120, slotScale * 1.33);
            }
            return 120;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}