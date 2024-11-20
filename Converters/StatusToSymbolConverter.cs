using System;
using DecryptStation3.Models;
using Microsoft.UI.Xaml.Data;

namespace DecryptStation3.Converters
{
    public class StatusToSymbolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is ProcessingStatus status)
            {
                return status switch
                {
                    ProcessingStatus.Pending => "Clock24",
                    ProcessingStatus.CalculatingHash => "Calculator24",
                    ProcessingStatus.HashCalculated => "Checkmark24",
                    ProcessingStatus.Decrypting => "LockOpen24",
                    ProcessingStatus.Extracting => "ArrowExportLtr24",
                    ProcessingStatus.Completed => "CheckmarkCircle24",
                    ProcessingStatus.Error => "ErrorCircle24",
                    _ => "QuestionCircle24"
                };
            }

            return "QuestionCircle24";
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}