using STSApp.Contracts.Enums;

namespace STSApp.Contracts.Models;

/// <summary>
/// 音声ファイルの参照情報です。
/// 音声ファイル本体はDBに入れず、Backend側の保存場所に置きます。
/// </summary>
public sealed record AudioFileDto(
    Guid Id,
    Guid ConversationTurnId,
    AudioFileKind Kind,
    string FilePath,
    string MimeType,
    long? FileSizeBytes,
    DateTime CreatedAt);
