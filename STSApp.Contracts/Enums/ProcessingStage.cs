namespace STSApp.Contracts.Enums;

/// <summary>
/// 1ターン内で、現在どの処理段階にいるかを表します。
/// SignalR通知、turn_events、エラー記録で共通して使います。
/// </summary>
public enum ProcessingStage
{
    // uploadを分けるのは、外部APIへ送る前のファイル受け取りで失敗した場合も区別するためです。
    Upload,

    // sttを分けるのは、録音は成功したが文字起こしで失敗したケースを区別するためです。
    Stt,

    // geminiを分けるのは、文字起こし後のAI応答で失敗したケースを区別するためです。
    Gemini,

    // ttsを分けるのは、返答文はできたが音声化で失敗したケースを区別するためです。
    Tts,

    // databaseを分けるのは、外部APIではなくDB保存で失敗した場合も区別するためです。
    Database
}
