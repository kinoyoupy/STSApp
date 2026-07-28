using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using STSApp.Backend.Options;

namespace STSApp.Backend.Services.External;

/// <summary>
/// 既存TTS APIをHTTPで呼び出す実装です。
///
/// TTS APIとの通信をここへ分ける理由は、AI返答の作成と音声ファイル取得を別の責任にするためです。
/// そのため、WorkflowはTTSのURLやレスポンスの細部を意識せずに済みます。
/// </summary>
public sealed class HttpTtsClient : ITtsClient
{
    private readonly HttpClient _httpClient;
    private readonly TtsOptions _options;

    public HttpTtsClient(HttpClient httpClient, IOptions<ExternalApiOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value.Tts;
    }

    public async Task<GeneratedSpeech> SynthesizeAsync(
        string text,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            throw new InvalidOperationException("TTS BaseUrl is not configured.");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("TTS request text is empty.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildRequestUri());

        // JSONで送る理由は、TTS APIが文章と声質・速度の設定を項目ごとに受け取る仕様だからです。
        // 任意項目を未設定のまま送る理由は、使わない設定でTTSの既定値を上書きしないためです。
        request.Content = JsonContent.Create(new TtsRequest(
            text,
            ToNullIfWhiteSpace(_options.Voicepack),
            _options.Alpha,
            _options.Beta,
            _options.Speed));

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"TTS API request failed. StatusCode={(int)response.StatusCode}, Body={body}");
        }

        // HttpResponseMessageを破棄した後も保存処理で読めるように、いったんメモリへコピーします。
        var audioBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (audioBytes.Length == 0)
        {
            throw new InvalidOperationException("TTS API response audio was empty.");
        }

        return new GeneratedSpeech(
            new MemoryStream(audioBytes),
            _options.ResponseMimeType,
            _options.ResponseFileExtension);
    }

    private Uri BuildRequestUri()
    {
        // BaseUrl と SpeakPath を分けておくと、
        // URL本体を隠したまま /speak のようなパスだけをコードで扱えます。
        var baseUri = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        var path = _options.SpeakPath.TrimStart('/');
        return new Uri(baseUri, path);
    }

    private static string? ToNullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private sealed record TtsRequest(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("voicepack")] string? Voicepack,
        [property: JsonPropertyName("alpha")] double? Alpha,
        [property: JsonPropertyName("beta")] double? Beta,
        [property: JsonPropertyName("speed")] double? Speed);
}
