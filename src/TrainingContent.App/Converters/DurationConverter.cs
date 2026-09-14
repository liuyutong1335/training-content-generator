using System.Globalization;
using System.Windows.Data;

namespace TrainingContent.App.Converters;

/// <summary>
/// ProjectSummary.DurationMs（long?）を HH:MM:SS 表示にする。
/// 録画前（null）は「—」。1 時間未満でも 00:01:24 の形に統一する。
/// </summary>
public sealed class DurationConverter : IValueConverter
{
    public const string NoValue = "—";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not long milliseconds || milliseconds < 0)
        {
            return NoValue;
        }

        var span = TimeSpan.FromMilliseconds(milliseconds);
        return $"{(int)span.TotalHours:D2}:{span.Minutes:D2}:{span.Seconds:D2}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
