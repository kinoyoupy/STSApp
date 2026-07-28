namespace STSApp.Contracts.Models;

/// <summary>
/// 会話セッションの表示用データです。
/// </summary>
public sealed record ConversationDto(
    // 会話セッションのUUIDです。
    Guid Id,
    // 画面に表示する会話名です。
    string Title,
    DateTime CreatedAt,
    DateTime UpdatedAt);
