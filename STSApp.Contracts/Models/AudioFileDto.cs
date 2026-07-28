using STSApp.Contracts.Enums;

namespace STSApp.Contracts.Models;

/// <summary>
/// 音声ファイルの参照情報です。
/// 音声ファイル本体はDBに入れず、Backend側の保存場所に置きます。
/// </summary>
public sealed record AudioFileDto(
    // 音声参照レコードのUUIDです。
    Guid Id,
    // 所属する会話ターンのUUIDです。
    Guid ConversationTurnId,
    // input/outputの用途です。
    AudioFileKind Kind,
    string FilePath,
    string MimeType,
    int? DurationMs,
    long? FileSizeBytes,
    DateTime CreatedAt);
