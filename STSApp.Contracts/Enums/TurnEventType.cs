namespace STSApp.Contracts.Enums;

/// <summary>
/// turn_events に記録するイベントの種類です。
/// 同じ stage / event_type が複数回出ることは、リトライや再処理であり得ます。
/// </summary>
public enum TurnEventType
{
    Started,
    Completed,
    Failed,
    Info
}
