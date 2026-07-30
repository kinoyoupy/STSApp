namespace STSApp.Backend.Services.Storage;

/// <summary>
/// 音声ファイル本体を保存するための窓口です。
/// DBにはファイル本体ではなく、この保存結果のパスを記録します。
/// </summary>
public interface IAudioFileStorage
{
    Task<StoredAudioFile> SaveInputAudioAsync(
        Guid turnId,
        IFormFile audioFile,
        CancellationToken cancellationToken);

    Task<StoredAudioFile> SaveOutputAudioAsync(
        Guid turnId,
        Stream audioStream,
        string mimeType,
        string fileExtension,
        CancellationToken cancellationToken);

    Task DeleteAsync(
        string filePath,
        CancellationToken cancellationToken);

    Task<Stream?> OpenReadAsync(
        string filePath,
        CancellationToken cancellationToken);
}

/// <summary>
/// 保存済み音声ファイルの情報です。
/// audio_files テーブルへ保存する値のもとになります。
/// </summary>
public sealed record StoredAudioFile(
    string FilePath,
    string MimeType,
    long FileSizeBytes);
