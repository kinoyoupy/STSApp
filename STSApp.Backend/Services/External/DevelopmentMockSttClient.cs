namespace STSApp.Backend.Services.External;

/// <summary>
/// 実STT APIを設定する前に、録音後の処理フローを最後まで確認するための開発用STTです。
/// 実際の音声認識は行わず、固定の文字列を返します。
/// </summary>
public sealed class DevelopmentMockSttClient : ISttClient
{
    public Task<string> TranscribeAsync(
        Stream audioStream,
        string fileName,
        string contentType,
        CancellationToken cancellationToken)
    {
        // audioStream を読む必要はありませんが、引数として受け取れる形にしておくことで、
        // 実STT APIへ差し替えた時も Workflow 側の呼び出し方を変えずに済みます。
        var text = $"これは開発用STTの文字起こし結果です。受信ファイル: {fileName}";
        return Task.FromResult(text);
    }
}
