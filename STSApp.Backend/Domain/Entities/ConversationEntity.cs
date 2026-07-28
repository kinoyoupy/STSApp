namespace STSApp.Backend.Domain.Entities;

/// <summary>
/// conversations テーブルに対応するエンティティです。
/// ひとまとまりの会話セッションを表します。
/// </summary>
public sealed class ConversationEntity
{
    public Guid Id { get; init; }
    public string Title { get; set; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; set; }
}
