namespace STSApp.Desktop;

/// <summary>
/// Backendへ送信する録音結果です。
/// 実マイク録音に差し替えた後も、この形でBackendApiClientへ渡します。
/// </summary>
public sealed record RecordedAudio(
    // WAVファイル全体をメモリ上に持ったデータです。
    byte[] Bytes,
    // Backendへ送るmultipartのファイル名です。
    string FileName,
    // BackendとSTTが音声形式を判断するためのMIMEタイプです。
    string ContentType);
