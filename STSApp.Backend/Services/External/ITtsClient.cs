namespace STSApp.Backend.Services.External;

/// <summary>
/// 既存TTS API呼び出しの差し替え口です。
/// 返答テキストを渡して、生成された音声データを受け取ります。
/// </summary>
public interface ITtsClient
{
    Task<GeneratedSpeech> SynthesizeAsync(
        string text,
        CancellationToken cancellationToken);
}

/// <summary>
/// TTS APIから返る音声データです。
/// MimeType は audio/wav などを想定します。
/// </summary>
public sealed record GeneratedSpeech(
    Stream AudioStream,
    string MimeType,
    string FileExtension);
