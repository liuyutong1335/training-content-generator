using System.Globalization;
using System.Windows.Data;
using TrainingContent.Storage;

namespace TrainingContent.App.Converters;

/// <summary>
/// 生成 artifact の状態を一覧の表示文字列にする。
///
/// <para>
/// Video は <see cref="ArtifactGenerationState"/>（Missing / Current / Stale）で判定する。
/// Manual は C integration 待ちのため <see cref="ProjectSummary.HasManual"/>（bool）のままで、
/// 従来どおり「生成済み」「未生成」を返す。
/// </para>
/// <para>
/// 判定そのものは Storage 側 <see cref="ProjectSummary"/> が持つ（metadata・実ファイル・
/// <c>SourceRevision</c> を確認済み）。ここは表示語に置き換えるだけ。
/// </para>
/// </summary>
public sealed class ArtifactStatusConverter : IValueConverter
{
    public const string Generated = "生成済み";
    public const string NotGenerated = "未生成";
    public const string Current = "最新";
    public const string Stale = "要再生成";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            ArtifactGenerationState.Missing => NotGenerated,
            ArtifactGenerationState.Current => Current,
            ArtifactGenerationState.Stale => Stale,
            // bool 互換（Manual 列）。
            true => Generated,
            _ => NotGenerated,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
