using System;
using System.Globalization;
using System.Windows.Data;

namespace EA_DailyReport.Converters
{
    /// <summary>
    /// int値とConverterParameterが等しければtrue、違えばfalseを返すConverter
    /// RadioButtonのIsCheckedとintプロパティをバインドするために使用
    /// 使用例：IsChecked="{Binding tab_scroll_mode, Converter={StaticResource IntEqual}, ConverterParameter=1}"
    /// </summary>
    public class IntEqualConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int intVal && parameter is string paramStr
                && int.TryParse(paramStr, out int paramInt))
                return intVal == paramInt;
            return false;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // チェックされたときだけ値を返す（外れたときはDependencyProperty.UnsetValueで無視）
            if (value is bool b && b && parameter is string paramStr
                && int.TryParse(paramStr, out int paramInt))
                return paramInt;
            return System.Windows.DependencyProperty.UnsetValue;
        }
    }
}