namespace STSApp.Contracts.Enums;

/// <summary>
/// 音声ファイルが、ユーザー入力側の音声か、AI返答側の音声かを表します。
/// DBでは MySQL ENUM('input', 'output') として扱う想定です。
/// </summary>
public enum AudioFileKind
{
    // ユーザーがPushToTalkで録音し、Backendへ送った音声です。
    Input,

    // Geminiの返答をTTSへ渡して生成した、AI側の音声です。
    Output
}
