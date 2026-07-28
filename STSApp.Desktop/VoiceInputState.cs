namespace STSApp.Desktop;

/// <summary>
/// 画面が今どの段階にいるかを表します。
///
/// 録音中かどうかだけでは、録音終了後にBackendが処理中なのか、
/// 次の発話を受け付けてよいのかを区別できません。
/// そのため、音声入力に関する状態を一か所で表します。
/// </summary>
public enum VoiceInputState
{
    /// <summary>
    /// 音声入力を開始できる状態です。
    /// </summary>
    Ready,

    /// <summary>
    /// マイクを開き、VADによる発話開始を待つ状態です。
    /// </summary>
    Listening,

    /// <summary>
    /// マイクから音声を録音している状態です。
    /// </summary>
    Recording,

    /// <summary>
    /// 録音済み音声をBackendがSTT、Gemini、TTSの順で処理している状態です。
    /// 処理中に新しい音声を受け付けると会話の順序が混ざるため、録音開始を受け付けません。
    /// </summary>
    Processing,

    /// <summary>
    /// 録音またはBackend処理でエラーになった状態です。
    /// ボタンをもう一度押すと、改めて録音を開始できます。
    /// </summary>
    Error
}
