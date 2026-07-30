namespace STSApp.Backend.Domain.Entities;

/// <summary>
/// ナレッジベースに取り込んだ資料ファイル1件です。
/// ファイル本文そのものではなく、同期判定に必要なパスとハッシュを中心に持ちます。
/// </summary>
public sealed class KnowledgeDocumentEntity
{
    public long Id { get; init; }
    public string SourcePath { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string SourceHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; set; }
}
