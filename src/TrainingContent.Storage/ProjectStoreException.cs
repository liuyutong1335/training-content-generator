namespace TrainingContent.Storage;

/// <summary>
/// ProjectStore が「Project が存在しない」以外の理由で失敗したことを示す。
/// 破損 JSON / 未対応 schema / Contract validation 違反を、欠損（Load の null）と
/// 呼出側が判別できるようにするための型。
///
/// <para>
/// 基底は <see cref="IOException"/>。<see cref="InvalidDataException"/> は .NET 8 で sealed のため
/// 継承できないが、それ自身も IOException 派生なので、呼出側の捕捉可能性は同じになる
/// （<c>catch (ProjectStoreException)</c> でも <c>catch (IOException)</c> でも拾える）。
/// </para>
/// </summary>
public sealed class ProjectStoreException : IOException
{
    public ProjectStoreException(string message)
        : base(message)
    {
    }

    public ProjectStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
