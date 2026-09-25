namespace TrainingContent.App.Services;

/// <summary>
/// <see cref="Views.ContentsView"/> の video 生成中 state（button / progress / cancel の可否）を決める純粋 helper。
///
/// <para>
/// cancel を要求しても <b>lock は解除しない</b>（<see cref="IsContentMutationEnabled"/> は false のまま）。
/// 実際に <c>GenerateAsync</c> が戻って <c>isGenerating</c> が false になった時点で解除される。
/// </para>
/// </summary>
public readonly record struct ContentsGenerationUiState(
    bool IsContentMutationEnabled,
    bool IsGridEnabled,
    bool IsProgressVisible,
    bool IsCancelVisible,
    bool IsCancelEnabled)
{
    /// <summary>idle（読み込み中でも生成中でもない）ときだけ新しい生成を開始できる。</summary>
    public bool CanStartGeneration => IsContentMutationEnabled;

    /// <summary>Generate button の enabled。precondition（選択行の Step / Duration）は呼出側が判定する。</summary>
    public bool IsGenerateEnabled(bool preconditionMet) => CanStartGeneration && preconditionMet;
}

/// <inheritdoc cref="ContentsGenerationUiState"/>
public static class ContentsGenerationUiStateResolver
{
    public static ContentsGenerationUiState Resolve(
        bool isLoading,
        bool isGenerating,
        bool isCancelRequested)
    {
        if (!isGenerating)
        {
            // idle: 内容操作は自由。progress / cancel は出さない。
            return new ContentsGenerationUiState(
                IsContentMutationEnabled: !isLoading,
                IsGridEnabled: true,
                IsProgressVisible: false,
                IsCancelVisible: false,
                IsCancelEnabled: false);
        }

        return new ContentsGenerationUiState(
            // loading / generating のどちらでも内容操作はさせない。
            IsContentMutationEnabled: false,
            // 生成中は一覧そのものを止める（selection 変更・行ダブルクリックも含めて）。
            IsGridEnabled: false,
            IsProgressVisible: true,
            IsCancelVisible: true,
            // cancel 済みなら再度押させない（連打は無視する）。lock は維持。
            IsCancelEnabled: !isCancelRequested);
    }
}
