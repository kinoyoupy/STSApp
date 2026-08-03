using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace STSApp.Desktop;

/// <summary>
/// マイク監視、VAD、発話開始・終話、WAV確定を一つの音声入力セッションとして管理します。
/// MainWindowが録音ライブラリの細かな呼び出し順を持たず、画面表示とBackend通信へ集中するためのクラスです。
/// </summary>
public sealed class VoiceInputSessionController : IDisposable
{
    private readonly ContinuousAudioRecorder _audioRecorder = new();
    private readonly WebRtcVoiceActivityDetector _voiceActivityDetector = new();
    private readonly VoiceEndpointDetector _voiceEndpointDetector = new();
    private static readonly TimeSpan FirstAudioFrameTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FirstAudioFrameRetryDelay = TimeSpan.FromMilliseconds(200);
    private const int MaxFirstAudioFrameRetries = 1;
    private bool _isCompletingUtterance;
    private CancellationTokenSource? _frameWatchdogCancellation;

    public VoiceInputSessionController()
    {
        // 録音、1フレームの発話判定、発話区間の判定を順番に接続します。
        // MainWindowからこの配線を隠し、音声入力の内部構成を変更しても画面への影響を抑えます。
        _audioRecorder.FrameCaptured += _voiceActivityDetector.ProcessFrame;
        _voiceActivityDetector.FrameClassified += _voiceEndpointDetector.ProcessVoiceActivity;
        _voiceEndpointDetector.StateChanged += VoiceEndpointDetector_StateChanged;
    }

    public VoiceInputState State { get; private set; } = VoiceInputState.Ready;

    /// <summary>
    /// 利用者が音声入力を開始してから、停止するかエラーになるまでtrueです。
    /// 1回の返答再生後もtrueなら、次の発話待機へ自動的に戻します。
    /// </summary>
    public bool SessionEnabled { get; private set; }

    public event Action<VoiceInputState>? StateChanged;
    public event Action<VoiceInputSessionActivity>? ActivityChanged;
    public event Action<RecordedAudio>? AudioReady;
    public event Action<VoiceInputSessionError>? ErrorOccurred;

    public void StartSession()
    {
        SessionEnabled = true;
        StartListeningCore();
    }

    public void StopSessionAndDiscard()
    {
        SessionEnabled = false;
        _isCompletingUtterance = false;
        CancelFrameWatchdog();

        try
        {
            _audioRecorder.StopAndDiscard();
        }
        catch (Exception exception)
        {
            // 停止時の後片付けエラーは通知しますが、利用者の停止操作自体は完了させます。
            NotifySafely(() => ErrorOccurred?.Invoke(new VoiceInputSessionError(
                VoiceInputSessionErrorKind.Stop,
                exception.Message)));
        }

        _voiceActivityDetector.EndRecording();
        _voiceEndpointDetector.EndRecording();
        SetState(VoiceInputState.Ready);
        NotifySafely(() => ActivityChanged?.Invoke(VoiceInputSessionActivity.ListeningStopped));
    }

    public void ResumeAfterResponse()
    {
        if (SessionEnabled)
        {
            StartListeningCore();
            return;
        }

        SetState(VoiceInputState.Ready);
    }

    public void SetExternalFailure()
    {
        SessionEnabled = false;
        _isCompletingUtterance = false;
        CancelFrameWatchdog();

        // 通常はBackend処理中でマイク停止済みですが、遅れて届いた失敗通知でも
        // マイクを開いたままにしないよう、待機・録音中なら明示的に後片付けします。
        if (State is VoiceInputState.Listening or VoiceInputState.Recording)
        {
            TryStopAndDiscard();
            _voiceActivityDetector.EndRecording();
            _voiceEndpointDetector.EndRecording();
        }

        SetState(VoiceInputState.Error);
    }

    public void Dispose()
    {
        TryCleanup(CancelFrameWatchdog);
        TryCleanup(() => _audioRecorder.FrameCaptured -= _voiceActivityDetector.ProcessFrame);
        TryCleanup(() => _voiceActivityDetector.FrameClassified -= _voiceEndpointDetector.ProcessVoiceActivity);
        TryCleanup(() => _voiceEndpointDetector.StateChanged -= VoiceEndpointDetector_StateChanged);
        TryCleanup(_audioRecorder.Dispose);
        TryCleanup(_voiceActivityDetector.Dispose);
    }

    private void StartListeningCore(int startupAttempt = 0)
    {
        CancelFrameWatchdog();

        try
        {
            _isCompletingUtterance = false;
            _voiceActivityDetector.BeginRecording();
            _voiceEndpointDetector.BeginRecording();
            _audioRecorder.StartListening();
            SetState(VoiceInputState.Listening);
            NotifySafely(() => ActivityChanged?.Invoke(VoiceInputSessionActivity.ListeningStarted));

            var watchdogCancellation = new CancellationTokenSource();
            _frameWatchdogCancellation = watchdogCancellation;
            _ = MonitorFirstAudioFrameAsync(watchdogCancellation, startupAttempt);
        }
        catch (Exception exception)
        {
            SessionEnabled = false;
            _voiceActivityDetector.EndRecording();
            _voiceEndpointDetector.EndRecording();
            TryStopAndDiscard();
            SetState(VoiceInputState.Error);
            NotifySafely(() => ErrorOccurred?.Invoke(new VoiceInputSessionError(
                VoiceInputSessionErrorKind.Start,
                exception.Message)));
        }
    }

    private void VoiceEndpointDetector_StateChanged(VoiceEndpointState state)
    {
        // 音声判定はマイク用スレッドから届きます。
        // 録音の開始・停止と画面側イベントはUIスレッドへ順番に戻し、同時実行を避けます。
        Dispatcher.UIThread.Post(() => NotifySafely(() => HandleVoiceEndpointStateChanged(state)));
    }

    private void HandleVoiceEndpointStateChanged(VoiceEndpointState state)
    {
        if (!SessionEnabled)
        {
            return;
        }

        if (state == VoiceEndpointState.SpeechInProgress && State == VoiceInputState.Listening)
        {
            StartCapturingDetectedSpeech();
            return;
        }

        if (state == VoiceEndpointState.SpeechEnded && State == VoiceInputState.Recording)
        {
            CompleteDetectedUtterance();
        }
    }

    private void StartCapturingDetectedSpeech()
    {
        try
        {
            CancelFrameWatchdog();
            _audioRecorder.BeginAudioCapture();
            SetState(VoiceInputState.Recording);
            NotifySafely(() => ActivityChanged?.Invoke(VoiceInputSessionActivity.SpeechStarted));
        }
        catch (Exception exception)
        {
            SessionEnabled = false;
            // 録音開始の失敗を利用者へ伝える処理を、後片付け側の失敗で中断させないため、
            // ここでも例外を外へ出さない共通の停止処理を使います。
            TryStopAndDiscard();
            _voiceActivityDetector.EndRecording();
            _voiceEndpointDetector.EndRecording();
            SetState(VoiceInputState.Error);
            NotifySafely(() => ErrorOccurred?.Invoke(new VoiceInputSessionError(
                VoiceInputSessionErrorKind.CaptureStart,
                exception.Message)));
        }
    }

    private void CompleteDetectedUtterance()
    {
        if (_isCompletingUtterance)
        {
            return;
        }

        _isCompletingUtterance = true;
        CancelFrameWatchdog();
        SetState(VoiceInputState.Processing);
        NotifySafely(() => ActivityChanged?.Invoke(VoiceInputSessionActivity.SpeechEnded));

        try
        {
            // WAVを確定してから通知することで、MainWindowは完成音声だけをBackendへ送れます。
            var audio = _audioRecorder.StopAndGetAudio();
            _voiceActivityDetector.EndRecording();
            _voiceEndpointDetector.EndRecording();
            NotifySafely(() => AudioReady?.Invoke(audio));
        }
        catch (Exception exception)
        {
            SessionEnabled = false;
            _voiceActivityDetector.EndRecording();
            _voiceEndpointDetector.EndRecording();
            SetState(VoiceInputState.Error);
            NotifySafely(() => ErrorOccurred?.Invoke(new VoiceInputSessionError(
                VoiceInputSessionErrorKind.Finalize,
                exception.Message)));
        }
        finally
        {
            _isCompletingUtterance = false;
        }
    }

    private void SetState(VoiceInputState state)
    {
        State = state;
        NotifySafely(() => StateChanged?.Invoke(state));
    }

    private void TryStopAndDiscard()
    {
        try
        {
            _audioRecorder.StopAndDiscard();
        }
        catch
        {
            // 開始処理の元例外を利用者へ伝えることを優先します。
            // 後片付けの例外で原因を上書きしないため、ここでは追加送出しません。
        }
    }

    private async Task MonitorFirstAudioFrameAsync(
        CancellationTokenSource watchdogCancellation,
        int startupAttempt)
    {
        try
        {
            await Task.Delay(FirstAudioFrameTimeout, watchdogCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // タイマーの完了後に停止・発話開始が行われている可能性があるため、
        // UIスレッドへ戻ってから状態とフレーム数をもう一度確認します。
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_frameWatchdogCancellation, watchdogCancellation)
                || !SessionEnabled
                || State != VoiceInputState.Listening
                || _audioRecorder.CapturedFrameCount > 0)
            {
                watchdogCancellation.Dispose();
                return;
            }

            _frameWatchdogCancellation = null;
            watchdogCancellation.Dispose();

            if (startupAttempt < MaxFirstAudioFrameRetries)
            {
                // macOSの初回だけ入力デバイスの準備に時間がかかることがあります。
                // ここで即座にエラーにすると、権限が許可されていても利用者に誤った案内を出すため、
                // 録音エンジンを一度作り直してから、同じ入力開始を1回だけ自動で再試行します。
                _voiceActivityDetector.EndRecording();
                _voiceEndpointDetector.EndRecording();
                TryStopAndDiscard();
                _ = RetryListeningAfterStartupDelayAsync(startupAttempt + 1);
                return;
            }

            SessionEnabled = false;
            _isCompletingUtterance = false;
            _voiceActivityDetector.EndRecording();
            _voiceEndpointDetector.EndRecording();
            TryStopAndDiscard();
            SetState(VoiceInputState.Error);
            NotifySafely(() => ErrorOccurred?.Invoke(new VoiceInputSessionError(
                VoiceInputSessionErrorKind.NoAudioFrame,
                "マイク入力を開始できませんでした。入力デバイスを確認して、もう一度お試しください。")));
        });
    }

    private async Task RetryListeningAfterStartupDelayAsync(int startupAttempt)
    {
        await Task.Delay(FirstAudioFrameRetryDelay);

        // 待機中に利用者が停止した場合は、再試行して録音を勝手に再開しません。
        if (SessionEnabled && State == VoiceInputState.Listening)
        {
            StartListeningCore(startupAttempt);
        }
    }

    private void CancelFrameWatchdog()
    {
        var cancellation = Interlocked.Exchange(ref _frameWatchdogCancellation, null);
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        cancellation.Dispose();
    }

    private static void NotifySafely(Action notification)
    {
        try
        {
            notification();
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"Voice input notification failed: {exception.GetType().Name}");
        }
    }

    private static void TryCleanup(Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"Voice input cleanup failed: {exception.GetType().Name}");
        }
    }
}

public enum VoiceInputSessionActivity
{
    ListeningStarted,
    SpeechStarted,
    SpeechEnded,
    ListeningStopped
}

public enum VoiceInputSessionErrorKind
{
    Start,
    NoAudioFrame,
    CaptureStart,
    Finalize,
    Stop
}

public sealed record VoiceInputSessionError(
    VoiceInputSessionErrorKind Kind,
    string Message);
