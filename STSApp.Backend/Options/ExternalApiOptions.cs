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
    /// </summary>
    public bool UseDevelopmentMocks { get; init; }

    public SttOptions Stt { get; init; } = new();
    public TtsOptions Tts { get; init; } = new();
    public GeminiOptions Gemini { get; init; } = new();
}

public sealed class SttOptions
{
    // URLを設定ファイルから読むのは、環境ごとに異なる接続先をコード変更なしで切り替えるためです。
    public string BaseUrl { get; init; } = string.Empty;
    // BaseUrlとパスを分けるのは、接続先のホストだけを環境ごとに変更しやすくするためです。
    public string TranscribePath { get; init; } = "/transcribe";
    // 設定値にするのは、認識方式を試すたびにコードを書き換えなくてよくするためです。
    public string DecodingType { get; init; } = "tdt";
    // 待ち時間を設けるのは、外部API停止時にBackend全体が待ち続けるのを防ぐためです。
    public int TimeoutSeconds { get; init; } = 60;
}

public sealed class TtsOptions
{
    // 接続先を設定値にするのは、実APIを使わない開発環境にも対応するためです。
    public string BaseUrl { get; init; } = string.Empty;
    // パスを分けるのは、TTS APIのホストと機能の場所を別々に変更できるようにするためです。
    public string SpeakPath { get; init; } = "/speak";
    // 任意設定として持つのは、声質を変えない利用方法も残すためです。
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
    // リクエスト先を設定にするのは、モデル提供側の接続先変更に対応しやすくするためです。
    public string BaseUrl { get; init; } = "https://generativelanguage.googleapis.com/v1beta/interactions";
    // APIキーをBackendだけに置くのは、Desktopアプリを通してキーが見えるのを防ぐためです。
    public string ApiKey { get; init; } = string.Empty;
    // モデル名を設定にするのは、利用モデルを変える時にコードを変更しないためです。
    public string ModelName { get; init; } = string.Empty;
    // 返答の役割や文体をGeminiへ伝える初期指示です。
    public string SystemInstruction { get; init; } = "あなたは音声対話システムのアシスタントです。短く自然な日本語で返答してください。";
    public int TimeoutSeconds { get; init; } = 60;
}
