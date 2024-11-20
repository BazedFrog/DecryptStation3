using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml;
using System;

namespace DecryptStation3
{
    public class ProgressToWidthConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is double progress)
            {
                // Convert progress to pixels directly
                return new GridLength(progress, GridUnitType.Pixel);
            }
            return new GridLength(0, GridUnitType.Pixel);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}