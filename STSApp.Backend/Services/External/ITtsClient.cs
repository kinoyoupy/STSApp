namespace STSApp.Backend.Services.External;

/// <summary>
/// 既存TTS API呼び出しの差し替え口です。
/// 返答テキストを渡して、生成された音声データを受け取ります。
/// </summary>
public interface ITtsClient
{
    // テキストを音声データへ変換します。
    // 戻り値には保存に必要な形式情報も含めます。
    Task<GeneratedSpeech> SynthesizeAsync(
        string text,
        CancellationToken cancellationToken);
}

/// <summary>
/// TTS APIから返る音声データです。
/// MimeType は audio/wav などを想定します。
/// </summary>
public sealed record GeneratedSpeech(
    // TTS APIのレスポンスを読み込むストリームです。
    Stream AudioStream,
    // audio/wavなど、保存後に再生形式を判断するための値です。
    string MimeType,
    // outputファイル名に付ける拡張子です。
    string FileExtension);
