using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace EA_DailyReport.Converters
{
    /// <summary>
    /// bool → Visibility 変換
    /// </summary>
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b)
            {
                if (parameter?.ToString() == "Invert")
                    return b ? Visibility.Collapsed : Visibility.Visible;
                return b ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}