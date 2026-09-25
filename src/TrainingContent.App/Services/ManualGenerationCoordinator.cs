using System.Diagnostics;
using System.IO;
using System.Text;
using TrainingContent.App.State;
using TrainingContent.Core.Models;
using TrainingContent.Manual;
using TrainingContent.Storage;

namespace TrainingContent.App.Services;

/// <summary>Manual 生成の結果分類。</summary>
public enum ManualGenerationStatus
{
    /// <summary>Markdown / HTML の pair と metadata の commit まで完了した。</summary>
    Generated,

    ProjectNotFound,

    /// <summary>Steps が無い（Manual の input authority は reviewed TrainingProject.Steps）。</summary>
    StepsMissing,

    /// <summary>Manual Core が Error を返した（canonical / metadata は一切変更しない）。</summary>
    GenerationRejected,

    /// <summary>生成中に Project が更新された（canonical / metadata は生成前のまま）。</summary>
    SourceChanged,

    Failed,

    /// <summary>ユーザー操作で cancel された（failure ではない）。</summary>
    Cancelled,
}

/// <summary>
/// <see cref="ManualGenerationCoordinator.GenerateAsync"/> の結果。
/// </summary>
/// <param name="Message">ユーザー向け message（raw exception / stack trace を含めない）。</param>
/// <param name="Errors"><see cref="ManualGenerationStatus.GenerationRejected"/> の safe な Manual Errors（detail 表示用）。</param>
/// <param name="RecoveryDirectory">
/// rollback に失敗し backup を保持している場合のみ、その transaction directory を示す。
/// </param>
public sealed record ManualGenerationOutcome(
    ManualGenerationStatus Status,
    string Message,
    IReadOnlyList<string> Errors,
    string? MarkdownPath = null,
    string? ErrorMessage = null,
    Exception? Error = null,
    string? RecoveryDirectory = null)
{
    public bool Succeeded => Status == ManualGenerationStatus.Generated;
}

/// <summary>
/// Manual 生成の backend orchestration（D-owned）。View から filesystem / Project metadata を触らせない窓口。
///
/// <para>
/// 流れ: Project load → precondition → <see cref="ManualGenerator.Generate"/>（in-memory）→ 固定 canonical path の確認 →
/// staging へ UTF-8（BOM なし）で書き込み → <see cref="ManualArtifactTransaction.CommitAsync"/>
/// （Revision gate → pair の backup / 置換 / metadata 更新）→ Current Project 更新。
/// </para>
/// <para>
/// Manual Core は file I/O も Outputs 更新も行わない契約のため、persistence はすべてここ（+ Storage）が持つ。
/// Manual 生成では <c>Project.Revision</c> を増やさない（output generation は teaching content の編集ではない）。
/// </para>
/// </summary>
public sealed class ManualGenerationCoordinator
{
    private const string GeneratedMessage = "マニュアルを生成しました。";
    private const string ProjectNotFoundMessage = "プロジェクトが見つかりません。";
    private const string StepsMissingMessage = "手順がありません。マニュアルを生成するには手順の確定が必要です。";
    private const string GenerationRejectedMessage = "マニュアルを生成できませんでした。";
    private const string SourceChangedMessage = "生成中に内容が変更されました。最新の内容で再度生成してください。";
    private const string FailedMessage = "マニュアルの生成に失敗しました。";
    private const string RecoveryMessage = "マニュアルの保存に失敗しました。復旧用バックアップを保持しています。";

    private readonly ProjectStore _projectStore;
    private readonly CurrentProjectContext _currentProject;
    private readonly ManualArtifactTransaction _transaction;
    private readonly Func<TrainingProject, ManualGenerationResult> _generator;

    /// <param name="generator">
    /// <see cref="ManualGenerator.Generate"/> は static のため、失敗経路（path 不一致など）を
    /// test が注入できるよう delegate 経由で呼ぶ。Composition Root が本物を渡す。
    /// </param>
    public ManualGenerationCoordinator(
        ProjectStore projectStore,
        CurrentProjectContext currentProject,
        ManualArtifactTransaction transaction,
        Func<TrainingProject, ManualGenerationResult> generator)
    {
        ArgumentNullException.ThrowIfNull(projectStore);
        ArgumentNullException.ThrowIfNull(currentProject);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(generator);

        _projectStore = projectStore;
        _currentProject = currentProject;
        _transaction = transaction;
        _generator = generator;
    }

    /// <summary>
    /// 指定 Project の Manual（Markdown + HTML）を生成し、成功したら canonical pair と metadata を置換する。
    /// 失敗しても既存の canonical / metadata は変更しない。
    ///
    /// <para>
    /// <c>CurrentProject.SetCurrent</c> は UI が観測する state なので、caller の context（UI thread）へ
    /// 戻ってから行う（<c>ConfigureAwait(true)</c> を維持し、transaction 内部だけが context を離れてよい）。
    /// </para>
    /// </summary>
    public async Task<ManualGenerationOutcome> GenerateAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        TrainingProject? project;
        try
        {
            project = await _projectStore.LoadProjectAsync(projectId, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // staging も canonical も触っていない。cancel は failure ではない。
            return Cancelled();
        }

        if (project is null)
        {
            return new ManualGenerationOutcome(ManualGenerationStatus.ProjectNotFound, ProjectNotFoundMessage, []);
        }

        // Manual の input authority は reviewed TrainingProject.Steps（Recording / Duration は不要）。
        if (project.Steps.Count == 0)
        {
            return new ManualGenerationOutcome(ManualGenerationStatus.StepsMissing, StepsMissingMessage, []);
        }

        var sourceRevision = project.Revision;

        var generated = _generator(project);
        if (generated.HasErrors)
        {
            // Manual Core の Error は safe な検証結果。canonical / metadata は一切触らない。
            Trace.TraceWarning("ManualGenerationCoordinator: Manual 生成が拒否されました — {0}", generated.Errors.Count);
            return new ManualGenerationOutcome(
                ManualGenerationStatus.GenerationRejected, GenerationRejectedMessage, generated.Errors);
        }

        if (generated.Markdown is null || generated.Html is null)
        {
            Trace.TraceError("ManualGenerationCoordinator: Markdown / HTML の pair が揃っていません。");
            return new ManualGenerationOutcome(ManualGenerationStatus.Failed, FailedMessage, []);
        }

        // 固定 canonical path 以外は fail closed（arbitrary な output path を filesystem path に変換しない）。
        if (generated.Markdown.Path != ManualArtifactTransaction.MarkdownRelativePath
            || generated.Html.Path != ManualArtifactTransaction.HtmlRelativePath)
        {
            Trace.TraceError("ManualGenerationCoordinator: Manual Core が固定 canonical path 以外を返しました。");
            return new ManualGenerationOutcome(ManualGenerationStatus.Failed, FailedMessage, []);
        }

        ManualArtifactStaging? staging = null;

        try
        {
            staging = _transaction.BeginStaging(projectId);

            // pair を staging へ完全に書いてから commit する（片方だけ canonical へ進めない）。
            await File.WriteAllTextAsync(
                    staging.StagingMarkdownPath,
                    generated.Markdown.Content,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken)
                .ConfigureAwait(true);
            await File.WriteAllTextAsync(
                    staging.StagingHtmlPath,
                    generated.Html.Content,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken)
                .ConfigureAwait(true);

            var commit = await _transaction
                .CommitAsync(staging, sourceRevision, cancellationToken)
                .ConfigureAwait(true);

            if (commit.Status != ManualArtifactCommitStatus.Committed)
            {
                // Storage 側の DiscardStaging は recovery backup があれば削除を拒否する。
                _transaction.DiscardStaging(staging);

                if (commit.RecoveryRequired)
                {
                    Trace.TraceWarning(
                        "ManualGenerationCoordinator: 手動 recovery が必要です。backup を {0} に残しました。",
                        commit.RecoveryDirectory);
                    return new ManualGenerationOutcome(
                        ManualGenerationStatus.Failed,
                        RecoveryMessage,
                        [],
                        ErrorMessage: commit.ErrorMessage,
                        RecoveryDirectory: commit.RecoveryDirectory);
                }

                // commit が rollback 済みで OCE を報告した場合だけ cancel 扱いにする。
                if (commit.Error is OperationCanceledException)
                {
                    return Cancelled();
                }

                return MapCommitFailure(commit);
            }

            // 生成対象が Current Project のときだけ差し替える（別 Project の生成で巻き戻さない）。
            // UI が観測する state なので caller の context（UI thread）で publish する。
            if (commit.Project is { } updated && _currentProject.IsCurrent(projectId))
            {
                _currentProject.SetCurrent(updated);
            }

            return new ManualGenerationOutcome(
                ManualGenerationStatus.Generated,
                GeneratedMessage,
                [],
                MarkdownPath: ManualArtifactTransaction.MarkdownRelativePath);
        }
        catch (OperationCanceledException)
        {
            if (staging is not null)
            {
                _transaction.DiscardStaging(staging);
            }

            Trace.TraceInformation("ManualGenerationCoordinator: Manual 生成をキャンセルしました。");
            return Cancelled();
        }
        catch (Exception ex)
        {
            // staging へ書く前 / 途中の failure。canonical と project.json は未変更。
            if (staging is not null)
            {
                _transaction.DiscardStaging(staging);
            }

            Trace.TraceError("ManualGenerationCoordinator: Manual 生成に失敗しました — {0}", ex);
            return new ManualGenerationOutcome(
                ManualGenerationStatus.Failed, FailedMessage, [], ErrorMessage: ex.Message, Error: ex);
        }
    }

    private static ManualGenerationOutcome MapCommitFailure(ManualArtifactCommitResult commit) =>
        commit.Status switch
        {
            ManualArtifactCommitStatus.ProjectNotFound =>
                new ManualGenerationOutcome(ManualGenerationStatus.ProjectNotFound, ProjectNotFoundMessage, []),
            ManualArtifactCommitStatus.SourceChanged =>
                new ManualGenerationOutcome(
                    ManualGenerationStatus.SourceChanged, SourceChangedMessage, [],
                    ErrorMessage: commit.ErrorMessage),
            _ => new ManualGenerationOutcome(
                ManualGenerationStatus.Failed, FailedMessage, [], ErrorMessage: commit.ErrorMessage),
        };

    private static ManualGenerationOutcome Cancelled() =>
        new(ManualGenerationStatus.Cancelled, "マニュアルの生成をキャンセルしました。", []);
}
