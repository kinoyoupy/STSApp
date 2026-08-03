namespace STSApp.Backend.Services.Storage;

/// <summary>
/// 後から聞き返さない音声ファイルを整理するための窓口です。
/// 会話本文やRAG履歴ではなく、音声ファイルとaudio_filesの記録だけを扱います。
/// </summary>
public interface IAudioFileCleanupService
{
    Task CleanupConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken);

    Task CleanupOrphanedAudioAsync(CancellationToken cancellationToken);
}
