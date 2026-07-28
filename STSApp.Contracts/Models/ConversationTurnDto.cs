using STSApp.Contracts.Enums;

namespace STSApp.Contracts.Models;

/// <summary>
/// 1回のユーザー発話とAI返答を表す表示用データです。
/// 詳細な処理履歴は TurnEventDto 側に分けます。
/// </summary>
public sealed record ConversationTurnDto(
    // 1回のPushToTalk送信を識別するIDです。会話IDとは別の値です。
    Guid Id,
    // このターンが所属する会話セッションのIDです。
    Guid ConversationId,
    // STT完了後に入るユーザー発話です。処理中はnullの場合があります。
    string? UserText,
    // Gemini完了後に入るAI返答です。処理中はnullの場合があります。
    string? AssistantText,
    // 一覧画面で現在の状態を判断するための値です。
    TurnStatus Status,
    // Failedの場合に、どの段階で失敗したかを示します。
    ProcessingStage? ErrorStage,
    // Failedの場合に、ユーザーや調査者へ伝えるエラー内容を示します。
    string? ErrorMessage,
    DateTime CreatedAt,
    DateTime UpdatedAt);
