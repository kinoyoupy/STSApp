using System;

namespace STSApp.Desktop;

/// <summary>
/// VADの「発話あり・なし」を並べて見て、発話開始と終話を判断します。
///
/// WebRTC VAD単体は短い20ms区間の判定しか返しません。
/// 実際の会話で必要な「話し始めた」「話し終えた」は、このクラスが連続した判定から決めます。
/// </summary>
public sealed class VoiceEndpointDetector
{
    // 20msフレームを3回連続で発話と判定した場合、約60ms続いた発話として扱います。
    // 単発の小さな雑音だけで発話開始しないための最小限の条件です。
    private const int SpeechStartFrameCount = 3;

    // 20msフレームを50回連続で非発話と判定した場合、約1秒の無音として扱います。
    // 会話中の短い間や息継ぎで終話しないよう、開始判定より長く取ります。
    private const int EndSilenceFrameCount = 50;

    private readonly object _syncRoot = new();
    private bool _isRecording;
    private VoiceEndpointState _state;
    private int _consecutiveSpeechFrames;
    private int _consecutiveSilenceFrames;
    private int _voiceActivityFrameCount;

    /// <summary>
    /// 発話開始・終話を確定した時だけ通知します。
    /// 音声処理用のスレッドから呼ばれるため、購読側は画面更新をUIスレッドへ渡します。
    /// </summary>
    public event Action<VoiceEndpointState>? StateChanged;

    /// <summary>
    /// 新しい録音に対する発話・終話判定を開始します。
    /// </summary>
    public void BeginRecording()
    {
        lock (_syncRoot)
        {
            _isRecording = true;
            _state = VoiceEndpointState.WaitingForSpeech;
            _consecutiveSpeechFrames = 0;
            _consecutiveSilenceFrames = 0;
            _voiceActivityFrameCount = 0;
        }
    }

    /// <summary>
    /// WebRTC VADが返した1フレームぶんの発話判定を受け取ります。
    /// </summary>
    public void ProcessVoiceActivity(bool hasSpeech)
    {
        VoiceEndpointState? changedState = null;

        lock (_syncRoot)
        {
            if (!_isRecording || _state == VoiceEndpointState.SpeechEnded)
            {
                return;
            }

            _voiceActivityFrameCount++;

            if (_state == VoiceEndpointState.WaitingForSpeech)
            {
                _consecutiveSpeechFrames = hasSpeech ? _consecutiveSpeechFrames + 1 : 0;

                if (_consecutiveSpeechFrames >= SpeechStartFrameCount)
                {
                    _state = VoiceEndpointState.SpeechInProgress;
                    _consecutiveSilenceFrames = 0;
                    changedState = _state;
                }
            }
            else
            {
                // 発話開始後は、発話を検知するたびに無音の連続回数をリセットします。
                // そのため、会話中の短い間では終話になりません。
                _consecutiveSilenceFrames = hasSpeech ? 0 : _consecutiveSilenceFrames + 1;

                if (_consecutiveSilenceFrames >= EndSilenceFrameCount)
                {
                    _state = VoiceEndpointState.SpeechEnded;
                    changedState = _state;
                }
            }
        }

        // 外部の画面処理をlockの中で実行すると、音声フレームの処理が待たされます。
        // 状態だけを退避して、内部状態の更新が終わった後に通知します。
        if (changedState is not null)
        {
            StateChanged?.Invoke(changedState.Value);
        }
    }

    /// <summary>
    /// 音声入力を終了し、今回の判定結果を返します。
    /// </summary>
    public VoiceEndpointDetectionResult EndRecording()
    {
        lock (_syncRoot)
        {
            _isRecording = false;

            return new VoiceEndpointDetectionResult(
                _voiceActivityFrameCount,
                _state is VoiceEndpointState.SpeechInProgress or VoiceEndpointState.SpeechEnded,
                _state == VoiceEndpointState.SpeechEnded,
                SpeechStartFrameCount,
                EndSilenceFrameCount);
        }
    }
}

/// <summary>
/// 録音単位の発話開始・終話検知結果です。
/// </summary>
public sealed record VoiceEndpointDetectionResult(
    int ProcessedFrameCount,
    bool SpeechStarted,
    bool SpeechEnded,
    int SpeechStartThresholdFrames,
    int EndSilenceThresholdFrames);

/// <summary>
/// 発話の区切りを判断する途中状態です。
/// </summary>
public enum VoiceEndpointState
{
    WaitingForSpeech,
    SpeechInProgress,
    SpeechEnded
}
