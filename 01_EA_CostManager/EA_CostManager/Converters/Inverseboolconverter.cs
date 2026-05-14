using System;
using System.Globalization;
using System.Windows.Data;

namespace EA_CostManager.Converters
{
    /// <summary>
    /// bool を反転するコンバーター
    /// IsEnabled="{Binding IsChecked, Converter={StaticResource InverseBool}}" 等で使用
    /// </summary>
    public class InverseBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b ? !b : true;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b ? !b : false;
    }
}