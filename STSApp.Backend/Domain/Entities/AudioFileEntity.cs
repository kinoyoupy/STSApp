using STSApp.Contracts.Enums;

namespace STSApp.Backend.Domain.Entities;

/// <summary>
/// audio_files テーブルに対応するエンティティです。
/// 音声ファイル本体ではなく、保存場所とメタ情報だけを持ちます。
/// </summary>
public sealed class AudioFileEntity
{
    public Guid Id { get; init; }
    public Guid ConversationTurnId { get; init; }
    public AudioFileKind Kind { get; init; }
    public string FilePath { get; init; } = string.Empty;
    public string MimeType { get; init; } = string.Empty;
    public int? DurationMs { get; init; }
    public long? FileSizeBytes { get; init; }
    public DateTime CreatedAt { get; init; }
}
