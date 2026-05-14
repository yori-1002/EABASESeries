using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace EA_DailyReport.Converters
{
    /// <summary>
    /// カラーコード文字列（#RRGGBB）→ SolidColorBrush 変換
    /// 無効値・空文字の場合は Transparent を返す
    /// </summary>
    public class StringToColorBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string color_str && !string.IsNullOrWhiteSpace(color_str))
            {
                try
                {
                    var color = (Color)ColorConverter.ConvertFromString(color_str);
                    return new SolidColorBrush(color);
                }
                catch
                {
                    return Brushes.Transparent;
                }
            }
            return Brushes.Transparent;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
