namespace STSApp.Backend.Services.External;

/// <summary>
/// 既存STT API呼び出しの差し替え口です。
/// 音声ファイルを渡して、認識されたテキストを受け取ります。
/// </summary>
public interface ISttClient
{
    Task<string> TranscribeAsync(
        Stream audioStream,
        string fileName,
        string contentType,
        CancellationToken cancellationToken);
}
