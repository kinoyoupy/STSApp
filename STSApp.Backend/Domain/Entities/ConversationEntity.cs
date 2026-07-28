namespace STSApp.Backend.Domain.Entities;

/// <summary>
/// conversations テーブルに対応するエンティティです。
/// ひとまとまりの会話セッションを表します。
/// </summary>
public sealed class ConversationEntity
{
    // 会話セッションを一意に識別するUUIDです。
    public Guid Id { get; init; }
    // 画面の会話一覧などで表示する名前です。
    public string Title { get; set; } = string.Empty;
    // セッションを作成した時刻です。DBにはUTCで保存します。
    public DateTime CreatedAt { get; init; }
    // 会話が更新された時刻です。一覧の並び替えにも使います。
    public DateTime UpdatedAt { get; set; }
}
