using System;
using WebRtcVadDetector = global::WebRtcVad.NET.WebRtcVad;
using WebRtcVadFrameLength = global::WebRtcVad.NET.FrameLength;
using WebRtcVadOperatingMode = global::WebRtcVad.NET.OperatingMode;
using WebRtcVadSampleRate = global::WebRtcVad.NET.SampleRate;

namespace STSApp.Desktop;

/// <summary>
/// WebRTC VADライブラリへ音声フレームを渡す窓口です。
///
/// 録音処理がライブラリを直接知らないように分けています。
/// こうしておくと、将来VADライブラリを変更する場合も、録音や画面へ影響を広げずに済みます。
/// </summary>
public sealed class WebRtcVoiceActivityDetector : IDisposable
{
    private readonly object _syncRoot = new();
    // ライブラリの名前空間と型名の両方にWebRtcVadが含まれます。
    // C#が名前空間と型を取り違えないよう、usingの別名を使います。
    private WebRtcVadDetector? _vad;

    private bool _isRecording;
    private int _totalFrameCount;
    private int _speechFrameCount;
    private string? _errorMessage;

    /// <summary>
    /// WebRTC VADが1フレームを判定するたびに通知します。
    /// このイベントは音声処理用のスレッドから呼ばれるため、購読側は短時間で処理を終えます。
    /// </summary>
    public event Action<bool>? FrameClassified;

    /// <summary>
    /// 新しい録音に対する集計を開始します。
    /// </summary>
    public void BeginRecording()
    {
        lock (_syncRoot)
        {
            // WebRTC VADは直前のフレームを使って判定を安定させる内部状態を持ちます。
            // 採用ライブラリにはリセット操作がないため、録音ごとに作り直して前回の状態を持ち越しません。
            _vad?.Dispose();
            _vad = CreateVad();
            _isRecording = true;
            _totalFrameCount = 0;
            _speechFrameCount = 0;
            _errorMessage = null;
        }
    }

    /// <summary>
    /// 20msのPCM音声フレームを判定します。
    ///
    /// このメソッドは録音処理用のスレッドから呼ばれます。
    /// 画面更新や通信を行わず、VAD判定と数の集計だけを短時間で終える必要があります。
    /// </summary>
    public void ProcessFrame(ReadOnlyMemory<short> samples)
    {
        Action<bool>? frameClassified = null;
        var hasSpeech = false;

        lock (_syncRoot)
        {
            if (!_isRecording || _errorMessage is not null)
            {
                return;
            }

            _totalFrameCount++;

            try
            {
                if (_vad is null)
                {
                    _errorMessage = "WebRTC VADを初期化できませんでした。";
                    return;
                }

                hasSpeech = _vad.HasSpeech(samples.Span);
                if (hasSpeech)
                {
                    _speechFrameCount++;
                }

                // 外部クラスの処理をlockの中で呼ぶと、将来の拡張時に待ち合わせが起きやすくなります。
                // 呼び出す対象だけ退避し、lockを抜けてから通知します。
                frameClassified = FrameClassified;
            }
            catch (Exception ex)
            {
                // 録音用のスレッドで例外を外へ出すと、マイク処理そのものが停止する恐れがあります。
                // そのため例外はここで保持し、録音終了後に画面へ一度だけ表示します。
                _errorMessage = ex.Message;
            }
        }

        frameClassified?.Invoke(hasSpeech);
    }

    /// <summary>
    /// 録音を終了し、今回の判定結果を返します。
    /// </summary>
    public VoiceActivityDetectionResult EndRecording()
    {
        lock (_syncRoot)
        {
            _isRecording = false;

            return new VoiceActivityDetectionResult(
                _totalFrameCount,
                _speechFrameCount,
                _errorMessage);
        }
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            _isRecording = false;
            _vad?.Dispose();
            _vad = null;
        }
    }

    private static WebRtcVadDetector CreateVad()
    {
        // ライブラリの名前空間と型名の両方にWebRtcVadが含まれます。
        // C#が名前空間と型を取り違えないよう、usingの別名を使います。
        return new WebRtcVadDetector
        {
            // ContinuousAudioRecorderが16kHzへ統一しているため、ここも同じ値に固定します。
            SampleRate = WebRtcVadSampleRate.Rate16kHz,

            // ContinuousAudioRecorderは20ms = 320サンプルでフレームを通知します。
            // VAD側の設定も一致させないと、正しく判定できません。
            FrameLength = WebRtcVadFrameLength.Length20ms,

            // 最初は雑音を発話と誤認しにくい設定から試します。
            // 小さい声を検知しにくい場合は、実機確認後にQualityまたはLowBitrateへ調整します。
            OperatingMode = WebRtcVadOperatingMode.Aggressive
        };
    }
}
