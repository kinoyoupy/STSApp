namespace STSApp.Contracts.Models;

/// <summary>
/// 会話セッションの表示用データです。
/// </summary>
public sealed record ConversationDto(
    Guid Id,
    string Title,
    DateTime CreatedAt,
    DateTime UpdatedAt);
