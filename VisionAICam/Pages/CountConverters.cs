using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace VisionAICam.Pages
{
    // Converts numeric count to a neon green brush when >= 10, otherwise default dark color.
    public class CountToBrushConverter : IValueConverter
    {
        private static readonly SolidColorBrush NeonGreen = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#39FF14"));
        private static readonly SolidColorBrush DefaultBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#222222"));

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int count && count >= 10)
                return NeonGreen;
            return DefaultBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotImplementedException();
    }

    // Converts numeric count to Visible when >= 10, otherwise Collapsed.
    public class CountToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int count && count >= 10)
                return Visibility.Visible;
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotImplementedException();
    }
}