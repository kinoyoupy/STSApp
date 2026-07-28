namespace STSApp.Contracts.Enums;

/// <summary>
/// 会話ターン全体の現在状態です。
/// 詳細な処理履歴は turn_events に残し、この値は一覧表示向けの現在状態として使います。
/// </summary>
public enum TurnStatus
{
    Processing,
    Completed,
    Failed
}
