using STSApp.Contracts.Enums;

namespace STSApp.Backend.Domain.Entities;

/// <summary>
/// audio_files テーブルに対応するエンティティです。
/// 音声ファイル本体ではなく、保存場所とメタ情報だけを持ちます。
/// </summary>
public sealed class AudioFileEntity
{
    // 音声参照レコード自体を識別するUUIDです。
    public Guid Id { get; init; }
    // どの会話ターンに属する音声かを示します。
    public Guid ConversationTurnId { get; init; }
    // inputならユーザー音声、outputならTTS音声です。
    public AudioFileKind Kind { get; init; }
    // 実ファイルの相対パスです。音声本体はここには入りません。
    public string FilePath { get; init; } = string.Empty;
    // audio/wavなど、再生時に形式を判断する値です。
    public string MimeType { get; init; } = string.Empty;
    public long? FileSizeBytes { get; init; }
    public DateTime CreatedAt { get; init; }
}
