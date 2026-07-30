using System;
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
            ErrorOccurred?.Invoke(new VoiceInputSessionError(
                VoiceInputSessionErrorKind.Stop,
                exception.Message));
        }

        _voiceActivityDetector.EndRecording();
        _voiceEndpointDetector.EndRecording();
        SetState(VoiceInputState.Ready);
        ActivityChanged?.Invoke(VoiceInputSessionActivity.ListeningStopped);
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
        CancelFrameWatchdog();
        _audioRecorder.FrameCaptured -= _voiceActivityDetector.ProcessFrame;
        _voiceActivityDetector.FrameClassified -= _voiceEndpointDetector.ProcessVoiceActivity;
        _voiceEndpointDetector.StateChanged -= VoiceEndpointDetector_StateChanged;
        _audioRecorder.Dispose();
        _voiceActivityDetector.Dispose();
    }

    private void StartListeningCore()
    {
        CancelFrameWatchdog();

        try
        {
            _isCompletingUtterance = false;
            _voiceActivityDetector.BeginRecording();
            _voiceEndpointDetector.BeginRecording();
            _audioRecorder.StartListening();
            SetState(VoiceInputState.Listening);
            ActivityChanged?.Invoke(VoiceInputSessionActivity.ListeningStarted);

            var watchdogCancellation = new CancellationTokenSource();
            _frameWatchdogCancellation = watchdogCancellation;
            _ = MonitorFirstAudioFrameAsync(watchdogCancellation);
        }
        catch (Exception exception)
        {
            SessionEnabled = false;
            _voiceActivityDetector.EndRecording();
            _voiceEndpointDetector.EndRecording();
            TryStopAndDiscard();
            SetState(VoiceInputState.Error);
            ErrorOccurred?.Invoke(new VoiceInputSessionError(
                VoiceInputSessionErrorKind.Start,
                exception.Message));
        }
    }

    private void VoiceEndpointDetector_StateChanged(VoiceEndpointState state)
    {
        // 音声判定はマイク用スレッドから届きます。
        // 録音の開始・停止と画面側イベントはUIスレッドへ順番に戻し、同時実行を避けます。
        Dispatcher.UIThread.Post(() => HandleVoiceEndpointStateChanged(state));
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
            ActivityChanged?.Invoke(VoiceInputSessionActivity.SpeechStarted);
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
            ErrorOccurred?.Invoke(new VoiceInputSessionError(
                VoiceInputSessionErrorKind.CaptureStart,
                exception.Message));
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
        ActivityChanged?.Invoke(VoiceInputSessionActivity.SpeechEnded);

        try
        {
            // WAVを確定してから通知することで、MainWindowは完成音声だけをBackendへ送れます。
            var audio = _audioRecorder.StopAndGetAudio();
            _voiceActivityDetector.EndRecording();
            _voiceEndpointDetector.EndRecording();
            AudioReady?.Invoke(audio);
        }
        catch (Exception exception)
        {
            SessionEnabled = false;
            _voiceActivityDetector.EndRecording();
            _voiceEndpointDetector.EndRecording();
            SetState(VoiceInputState.Error);
            ErrorOccurred?.Invoke(new VoiceInputSessionError(
                VoiceInputSessionErrorKind.Finalize,
                exception.Message));
        }
        finally
        {
            _isCompletingUtterance = false;
        }
    }

    private void SetState(VoiceInputState state)
    {
        State = state;
        StateChanged?.Invoke(state);
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

    private async Task MonitorFirstAudioFrameAsync(CancellationTokenSource watchdogCancellation)
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
            SessionEnabled = false;
            _isCompletingUtterance = false;
            _voiceActivityDetector.EndRecording();
            _voiceEndpointDetector.EndRecording();
            TryStopAndDiscard();
            SetState(VoiceInputState.Error);
            ErrorOccurred?.Invoke(new VoiceInputSessionError(
                VoiceInputSessionErrorKind.Start,
                "音声フレームを受信できませんでした。macOSのマイク権限、入力デバイスの接続状態を確認し、Desktopアプリを再起動してください。"));
        });
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
    CaptureStart,
    Finalize,
    Stop
}

public sealed record VoiceInputSessionError(
    VoiceInputSessionErrorKind Kind,
    string Message);
