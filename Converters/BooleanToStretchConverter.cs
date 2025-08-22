using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace N64RecompLauncher
{
    public class BooleanToStretchConverter : IValueConverter
    {
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
            if (value is Stretch stretch)
            {
                return stretch == Stretch.Fill;
            }
            return false;
        }
    }
}