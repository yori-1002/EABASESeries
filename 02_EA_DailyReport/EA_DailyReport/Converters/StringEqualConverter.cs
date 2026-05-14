using System;
using System.Globalization;
using System.Windows.Data;

namespace EA_DailyReport.Converters
{
    /// <summary>
    /// string値とConverterParameterが等しければtrue、違えばfalseを返すConverter
    /// RadioButtonのIsCheckedとstringプロパティをバインドするために使用
    /// 使用例：IsChecked="{Binding theme, Converter={StaticResource StrEqual}, ConverterParameter=cyan}"
    /// </summary>
    public class StringEqualConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value?.ToString() == parameter?.ToString();
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b && b)
                return parameter?.ToString() ?? string.Empty;
            return System.Windows.DependencyProperty.UnsetValue;
        }
    }
}