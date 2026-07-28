namespace STSApp.Desktop;

/// <summary>
/// Backendへ送信する録音結果です。
/// 実マイク録音に差し替えた後も、この形でBackendApiClientへ渡します。
/// </summary>
public sealed record RecordedAudio(
    byte[] Bytes,
    string FileName,
    string ContentType);
