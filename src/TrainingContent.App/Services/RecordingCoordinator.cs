using System.Diagnostics;
using TrainingContent.App.State;
using TrainingContent.Capture;
using TrainingContent.Core.Models;
using TrainingContent.Storage;

namespace TrainingContent.App.Services;

/// <summary>
/// 担当A の <see cref="IRecordingEngine"/> を App へ接続する薄い Integration Layer。
///
/// <para>
/// 責任: Engine の呼出、Recording session state、Project ID の固定、RecordingOptions の構築、
/// RecordingResult → RecordingInfo の mapping、ProjectStore への保存、CurrentProjectContext の更新。
/// </para>
/// <para>
/// 意図的にやらないこと: ScreenRecorderLib の直接操作、canonical timeline の補正、
/// EventCapture（担当B）の呼出、Step 生成、AI。D5-A では B とは接続しない。
/// </para>
/// <para>
/// 録画 session 中は Project が切り替わっても保存先がぶれないよう、開始時の Project ID と
/// 選択 device を session として固定する（<see cref="SessionProjectId"/>）。
/// </para>
/// </summary>
public sealed class RecordingCoordinator
{
    private readonly IRecordingEngine _engine;
    private readonly ProjectStore _projectStore;
    private readonly CurrentProjectContext _currentProject;

    /// <summary>
    /// 録画停止後に project.json の保存へ失敗したときの message。
    /// Project directory が失われている場合など MP4 の存在を断定できない状況があるため、
    /// 「作成されました」とは言い切らず、確認を促す表現にする。rollback / recovery は D8 の範囲。
    /// </summary>
    private const string SaveFailedMessage =
        "録画停止後、プロジェクト情報の保存に失敗しました。録画ファイルの状態を確認してください。";

    private Guid? _sessionProjectId;
    private RecordingOptions? _sessionOptions;
    private bool _isCommandRunning;

    // IRecordingEngine は State を公開していない（具象 Engine のみ）。境界は interface に保つため、
    // StateChanged から受け取った状態をここで保持する。Engine を動かすのは本 Coordinator だけなので
    // この mirror が唯一の状態源になる。
    private RecordingState _state = RecordingState.Idle;

    public RecordingCoordinator(
        IRecordingEngine engine,
        ProjectStore projectStore,
        CurrentProjectContext currentProject)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(projectStore);
        ArgumentNullException.ThrowIfNull(currentProject);

        _engine = engine;
        _projectStore = projectStore;
        _currentProject = currentProject;

        // Engine の StateChanged は UI thread から来る保証がない。ここでは中継するだけにして、
        // Dispatcher への marshal は UI 側（RecordingView / MainWindow）の責任にする。
        _engine.StateChanged += (_, e) =>
        {
            _state = e.State;
            ActivityChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>Engine の状態変化、command の開始/終了を UI へ通知する。</summary>
    public event EventHandler? ActivityChanged;

    /// <summary>Engine の現在状態（StateChanged から保持した mirror）。</summary>
    public RecordingState State => _state;

    /// <summary>Start/Pause/Resume/Stop のいずれかが実行中か（UI の二重操作防止）。</summary>
    public bool IsCommandRunning => _isCommandRunning;

    /// <summary>
    /// 録画 session が進行中か。Navigation lock（§26）と Window close 拒否（§27）の判定に使う。
    /// </summary>
    public bool IsSessionActive =>
        _state is RecordingState.Recording or RecordingState.Paused or RecordingState.Stopping;

    /// <summary>session 開始時に固定した Project ID（録画中以外は null）。</summary>
    public Guid? SessionProjectId => _sessionProjectId;

    // ---------------------------------------------------------------------
    // Device enumeration（Engine へそのまま委譲する）
    // ---------------------------------------------------------------------

    public IReadOnlyList<DisplayDevice> GetDisplays() => _engine.GetDisplays();

    public IReadOnlyList<AudioDevice> GetMicrophones() => _engine.GetMicrophones();

    public IReadOnlyList<AudioDevice> GetSystemAudioDevices() => _engine.GetSystemAudioDevices();

    // ---------------------------------------------------------------------
    // Start / Pause / Resume
    // ---------------------------------------------------------------------

    /// <summary>
    /// 録画を開始する。出力先は ProjectStore が解決し、選択 device は Engine の返した
    /// instance をそのまま渡す（FriendlyName からの再構築はしない）。
    ///
    /// <para>
    /// 注意: Engine の StartAsync は「実際の撮影開始」より前に戻る（WGC 初期化に ~2 秒）。
    /// ここでは UI state を Recording にするだけで、timeline の基準には使わない。
    /// </para>
    /// </summary>
    public async Task<RecordingCommandResult> StartAsync(
        DisplayDevice? display,
        AudioDevice? systemAudioDevice,
        AudioDevice? microphoneDevice)
    {
        if (IsSessionActive)
        {
            return RecordingCommandResult.Failure("録画は既に開始されています。");
        }

        var project = _currentProject.CurrentProject;
        if (project is null)
        {
            return RecordingCommandResult.Failure("録画を開始するには、先にプロジェクトを作成または開いてください。");
        }

        _isCommandRunning = true;
        ActivityChanged?.Invoke(this, EventArgs.Empty);

        try
        {
            var options = new RecordingOptions
            {
                OutputFilePath = _projectStore.GetRecordingOutputPath(project.Id),
                Display = display,
                SystemAudioDevice = systemAudioDevice,
                MicrophoneDevice = microphoneDevice,
            };

            _sessionProjectId = project.Id;
            _sessionOptions = options;

            await _engine.StartAsync(options).ConfigureAwait(true);
            return RecordingCommandResult.Success();
        }
        catch (Exception ex)
        {
            Trace.TraceError("RecordingCoordinator: 録画開始に失敗しました — {0}", ex);

            _sessionProjectId = null;
            _sessionOptions = null;
            return RecordingCommandResult.Failure("録画を開始できませんでした。");
        }
        finally
        {
            _isCommandRunning = false;
            ActivityChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task<RecordingCommandResult> PauseAsync()
    {
        _isCommandRunning = true;
        ActivityChanged?.Invoke(this, EventArgs.Empty);

        try
        {
            await _engine.PauseAsync().ConfigureAwait(true);
            return RecordingCommandResult.Success();
        }
        catch (Exception ex)
        {
            Trace.TraceError("RecordingCoordinator: 一時停止に失敗しました — {0}", ex);
            return RecordingCommandResult.Failure("一時停止できませんでした。");
        }
        finally
        {
            _isCommandRunning = false;
            ActivityChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task<RecordingCommandResult> ResumeAsync()
    {
        _isCommandRunning = true;
        ActivityChanged?.Invoke(this, EventArgs.Empty);

        try
        {
            await _engine.ResumeAsync().ConfigureAwait(true);
            return RecordingCommandResult.Success();
        }
        catch (Exception ex)
        {
            Trace.TraceError("RecordingCoordinator: 再開に失敗しました — {0}", ex);
            return RecordingCommandResult.Failure("再開できませんでした。");
        }
        finally
        {
            _isCommandRunning = false;
            ActivityChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // ---------------------------------------------------------------------
    // Stop → RecordingInfo → project.json
    // ---------------------------------------------------------------------

    /// <summary>
    /// 録画を停止し、成功したら Current Project の Recording を更新して保存する。
    ///
    /// <para>
    /// 保存は session 固定の Project ID で project.json を読み直してから行う
    /// （UI が保持している古い object をそのまま書き戻さない）。
    /// Revision / UpdatedAtUtc を進めるのはこの 1 箇所だけ。
    /// </para>
    /// <para>
    /// MP4 の確定に成功しても project.json の保存に失敗する可能性がある。その場合も
    /// MP4 は削除せず、Current Project を「保存できたように」更新しない。
    /// </para>
    /// </summary>
    public async Task<RecordingStopOutcome> StopAsync()
    {
        if (!IsSessionActive)
        {
            return RecordingStopOutcome.Failure(RecordingStopStatus.NotRecording, "録画中ではありません。");
        }

        _isCommandRunning = true;
        ActivityChanged?.Invoke(this, EventArgs.Empty);

        try
        {
            RecordingResult result;
            try
            {
                result = await _engine.StopAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                // Engine 側の失敗（Failed state / 予期しない例外）。独自 recovery は行わない。
                Trace.TraceError("RecordingCoordinator: 録画停止に失敗しました — {0}", ex);
                return RecordingStopOutcome.Failure(
                    RecordingStopStatus.EngineFailed,
                    "録画エンジンでエラーが発生しました。アプリを再起動して再試行してください。");
            }

            var projectId = _sessionProjectId;
            var options = _sessionOptions;
            if (projectId is null || options is null)
            {
                Trace.TraceError("RecordingCoordinator: session 情報が失われているため保存できません。");
                return RecordingStopOutcome.Failure(
                    RecordingStopStatus.SaveFailed,
                    SaveFailedMessage,
                    result.FilePath);
            }

            try
            {
                var project = await _projectStore.LoadProjectAsync(projectId.Value).ConfigureAwait(true);
                if (project is null)
                {
                    Trace.TraceError("RecordingCoordinator: Project が見つかりません — {0}", projectId.Value);
                    return RecordingStopOutcome.Failure(
                        RecordingStopStatus.SaveFailed,
                        SaveFailedMessage,
                        result.FilePath);
                }

                project.Recording = MapToRecordingInfo(result, options);
                project.Revision++;
                project.UpdatedAtUtc = DateTimeOffset.UtcNow;

                await _projectStore.SaveProjectAsync(project).ConfigureAwait(true);
                _currentProject.SetCurrent(project);

                return RecordingStopOutcome.Success(result.FilePath);
            }
            catch (Exception ex)
            {
                Trace.TraceError("RecordingCoordinator: 録画結果の保存に失敗しました — {0}", ex);
                return RecordingStopOutcome.Failure(
                    RecordingStopStatus.SaveFailed,
                    SaveFailedMessage,
                    result.FilePath);
            }
        }
        finally
        {
            _sessionProjectId = null;
            _sessionOptions = null;
            _isCommandRunning = false;
            ActivityChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // ---------------------------------------------------------------------
    // Mapping（純関数 — UI / IO に依存しない）
    // ---------------------------------------------------------------------

    /// <summary>
    /// <see cref="RecordingResult"/> と録画時に渡した <see cref="RecordingOptions"/> から
    /// 契約 §7 の <see cref="RecordingInfo"/> を作る。
    ///
    /// <para>
    /// MediaPath は ProjectStore が定義する canonical な相対 path を使う（絶対パスは保存禁止）。
    /// HasSystemAudio / HasMicrophone と device metadata は Result に含まれないため、
    /// 「何を録音するよう Engine へ要求したか」= RecordingOptions から導出する。
    /// </para>
    /// </summary>
    public static RecordingInfo MapToRecordingInfo(RecordingResult result, RecordingOptions options)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(options);

        return new RecordingInfo
        {
            MediaPath = ProjectStore.RecordingMediaPath,
            StartedAtUtc = result.StartedAtUtc,
            DurationMs = (long)result.Duration.TotalMilliseconds,
            HasSystemAudio = options.SystemAudioDevice is not null,
            HasMicrophone = options.MicrophoneDevice is not null,
            DisplayId = options.Display?.DeviceId,
            DisplayName = options.Display?.DeviceName,
            SystemAudioDeviceId = options.SystemAudioDevice?.DeviceId,
            SystemAudioDeviceName = options.SystemAudioDevice?.DeviceName,
            MicrophoneDeviceId = options.MicrophoneDevice?.DeviceId,
            MicrophoneDeviceName = options.MicrophoneDevice?.DeviceName,
        };
    }
}

/// <summary>Start / Pause / Resume の結果。失敗しても例外は投げず、UI が message を表示できる形にする。</summary>
public sealed record RecordingCommandResult(bool Succeeded, string? ErrorMessage)
{
    public static RecordingCommandResult Success() => new(true, null);

    public static RecordingCommandResult Failure(string message) => new(false, message);
}

/// <summary>Stop の結果分類。</summary>
public enum RecordingStopStatus
{
    /// <summary>MP4 確定 + project.json 保存 + Current Project 更新まで完了。</summary>
    Saved,

    /// <summary>MP4 は確定したが project.json の保存に失敗（MP4 は削除しない）。</summary>
    SaveFailed,

    /// <summary>Engine 側で失敗（Failed state / 例外）。</summary>
    EngineFailed,

    /// <summary>そもそも録画中ではなかった。</summary>
    NotRecording,
}

/// <summary>Stop の結果。UI はこの Status で表示 message を切り替える。</summary>
public sealed record RecordingStopOutcome(RecordingStopStatus Status, string? ErrorMessage, string? AbsoluteFilePath)
{
    public static RecordingStopOutcome Success(string absoluteFilePath) =>
        new(RecordingStopStatus.Saved, null, absoluteFilePath);

    public static RecordingStopOutcome Failure(RecordingStopStatus status, string message, string? absoluteFilePath = null) =>
        new(status, message, absoluteFilePath);
}
