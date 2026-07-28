namespace STSApp.Contracts.Enums;

/// <summary>
/// 会話ターン全体の現在状態です。
/// 詳細な処理履歴は turn_events に残し、この値は一覧表示向けの現在状態として使います。
/// </summary>
public enum TurnStatus
{
    // 処理途中の状態を持つのは、音声を受け付けた後も画面で進行中と表示できるようにするためです。
    Processing,

    // 完了を別に持つのは、AI文字だけでなく返答音声の保存まで終わったか判断するためです。
    Completed,

    // 失敗を別に持つのは、処理が止まったことと原因を履歴から確認できるようにするためです。
    Failed
}
