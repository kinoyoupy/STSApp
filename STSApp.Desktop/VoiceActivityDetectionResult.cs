namespace STSApp.Desktop;

/// <summary>
/// 1回の録音中にWebRTC VADが判定した結果をまとめたものです。
///
/// VADは20msごとに「発話あり・なし」を返します。
/// このクラスは、それらを録音単位で確認できるようにするための結果です。
/// </summary>
public sealed record VoiceActivityDetectionResult(
    int TotalFrameCount,
    int SpeechFrameCount,
    string? ErrorMessage)
{
    /// <summary>
    /// 録音中に一度でも発話ありと判定されたかを表します。
    /// 次の自動終話の段階では、このような1回だけの結果ではなく、
    /// フレームの連続性も使って発話開始・終話を判断します。
    /// </summary>
    public bool SpeechDetected => SpeechFrameCount > 0;
}
