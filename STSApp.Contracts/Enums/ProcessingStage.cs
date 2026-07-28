namespace STSApp.Contracts.Enums;

/// <summary>
/// 1ターン内で、現在どの処理段階にいるかを表します。
/// SignalR通知、turn_events、エラー記録で共通して使います。
/// </summary>
public enum ProcessingStage
{
    Upload,
    Stt,
    Gemini,
    Tts,
    Database
}
