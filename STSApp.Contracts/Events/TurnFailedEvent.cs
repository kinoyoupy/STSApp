using STSApp.Contracts.Enums;

namespace STSApp.Contracts.Events;

/// <summary>
/// ターン処理中に失敗した時に、BackendからAvaloniaへ通知するイベントです。
/// どの段階で失敗したかを stage で表します。
/// </summary>
public sealed record TurnFailedEvent(
    Guid ConversationId,
    Guid TurnId,
    ProcessingStage Stage,
    string Message);
