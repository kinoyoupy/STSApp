namespace STSApp.Contracts.Enums;

/// <summary>
/// turn_events に記録するイベントの種類です。
/// 同じ stage / event_type が複数回出ることは、リトライや再処理であり得ます。
/// </summary>
public enum TurnEventType
{
    // その段階の処理を開始したことを表します。
    Started,

    // その段階の処理が正常に終わったことを表します。
    Completed,

    // その段階でエラーが発生したことを表します。
    Failed,

    // 進捗説明など、開始・完了・失敗以外の情報を表します。
    Info
}
