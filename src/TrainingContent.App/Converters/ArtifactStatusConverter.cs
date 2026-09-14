using System.Globalization;
using System.Windows.Data;

namespace TrainingContent.App.Converters;

/// <summary>
/// ProjectSummary.HasManual / HasVideo（bool）を「生成済み」「未生成」表示にする。
/// 判定そのものは Storage 側 ProjectSummary が持つ（metadata と実ファイルの両方を確認済み）。
/// </summary>
public sealed class ArtifactStatusConverter : IValueConverter
{
    public const string Generated = "生成済み";
    public const string NotGenerated = "未生成";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Generated : NotGenerated;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
