namespace STSApp.Backend.Services.External;

/// <summary>
/// STT API詳細が未設定の間に使うプレースホルダーです。
/// 実装前チェックリストの情報が揃ったら、HTTP実装へ差し替えます。
/// </summary>
public sealed class NotConfiguredSttClient : ISttClient
{
    public Task<string> TranscribeAsync(
        Stream audioStream,
        string fileName,
        string contentType,
        CancellationToken cancellationToken)
    {
        throw new NotImplementedException("STT API settings are not configured yet.");
    }
}
