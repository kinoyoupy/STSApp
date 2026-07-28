namespace STSApp.Contracts.Enums;

/// <summary>
/// 音声ファイルが、ユーザー入力側の音声か、AI返答側の音声かを表します。
/// DBでは MySQL ENUM('input', 'output') として扱う想定です。
/// </summary>
public enum AudioFileKind
{
    Input,
    Output
}
