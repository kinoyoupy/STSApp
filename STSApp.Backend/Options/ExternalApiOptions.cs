namespace STSApp.Backend.Options;

/// <summary>
/// STT/TTS/Gemini など外部APIの設定です。
/// 実URLやAPIキーは設計書に直接書かず、設定ファイルや環境変数から渡します。
/// </summary>
public sealed class ExternalApiOptions
{
    public const string SectionName = "ExternalApis";

    /// <summary>
    /// 実APIをまだ設定しない段階で、BackendからDesktopまでの成功ルートを確認するための開発用設定です。
    /// true にすると、STT/Gemini/TTS の実HTTP APIではなく、Backend内の疑似実装を使います。
    /// RAGは資料検索の正しさを確認する必要があるため、モック化せず明確な未設定エラーにします。
    /// </summary>
    public bool UseDevelopmentMocks { get; init; }

    public SttOptions Stt { get; init; } = new();
    public TtsOptions Tts { get; init; } = new();
    public GeminiOptions Gemini { get; init; } = new();
}

public sealed class SttOptions
{
    public string BaseUrl { get; init; } = string.Empty;
    public string TranscribePath { get; init; } = "/transcribe";
    public string DecodingType { get; init; } = "tdt";
    public int TimeoutSeconds { get; init; } = 60;
}

public sealed class TtsOptions
{
    public string BaseUrl { get; init; } = string.Empty;
    public string SpeakPath { get; init; } = "/speak";
    public string Voicepack { get; init; } = string.Empty;
    public double? Alpha { get; init; }
    public double? Beta { get; init; }
    public double? Speed { get; init; }
    public string ResponseMimeType { get; init; } = "audio/wav";
    public string ResponseFileExtension { get; init; } = ".wav";
    public int TimeoutSeconds { get; init; } = 60;
}

public sealed class GeminiOptions
{
    public string BaseUrl { get; init; } = "https://generativelanguage.googleapis.com/v1beta/interactions";
    public string ApiKey { get; init; } = string.Empty;
    public string ModelName { get; init; } = string.Empty;
    public string SystemInstruction { get; init; } = "あなたは音声対話システムのアシスタントです。短く自然な日本語で返答してください。";
    public int TimeoutSeconds { get; init; } = 60;
}
