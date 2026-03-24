using System;
using System.Globalization;
using System.Windows.Data;

namespace VisionAICam.Pages
{
    public class ClassIdConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null) return string.Empty;
            try
            {
                var pi = value.GetType().GetProperty("ClassId");
                if (pi != null)
                    return pi.GetValue(value)?.ToString() ?? string.Empty;

                var namePi = value.GetType().GetProperty("ClassName");
                return namePi?.GetValue(value)?.ToString() ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotImplementedException();
    }
}