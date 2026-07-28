namespace STSApp.Backend.Services.External;

/// <summary>
/// TTS API詳細が未設定の間に使うプレースホルダーです。
/// 実装前チェックリストの情報が揃ったら、HTTP実装へ差し替えます。
/// </summary>
public sealed class NotConfiguredTtsClient : ITtsClient
{
    public Task<GeneratedSpeech> SynthesizeAsync(
        string text,
        CancellationToken cancellationToken)
    {
        throw new NotImplementedException("TTS API settings are not configured yet.");
    }
}
