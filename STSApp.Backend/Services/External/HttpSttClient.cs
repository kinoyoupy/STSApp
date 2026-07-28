using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using STSApp.Backend.Options;

namespace STSApp.Backend.Services.External;

/// <summary>
/// 既存STT APIをHTTPで呼び出す実装です。
///
/// STT APIとの通信をここへ分ける理由は、音声対話の流れとHTTPの細かい作業を分離するためです。
/// WorkflowはURLやJSONの形を知らずに、音声を渡して文字を受け取るだけで済みます。
/// </summary>
public sealed class HttpSttClient : ISttClient
{
    private readonly HttpClient _httpClient;
    private readonly SttOptions _options;

    public HttpSttClient(HttpClient httpClient, IOptions<ExternalApiOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value.Stt;
    }

    public async Task<string> TranscribeAsync(
        Stream audioStream,
        string fileName,
        string contentType,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            throw new InvalidOperationException("STT BaseUrl is not configured.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildRequestUri());
        using var form = new MultipartFormDataContent();
        using var fileContent = new StreamContent(audioStream);

        // multipart/form-dataを使う理由は、文字列中心のJSONでは音声ファイルをそのまま送れないためです。
        // form.Addの第2引数「file」は相手のAPIが決めた名前なので、ここを変えると受け取ってもらえません。
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);

        form.Add(fileContent, "file", fileName);
        request.Content = form;

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"STT API request failed. StatusCode={(int)response.StatusCode}, Body={body}");
        }

        var result = await response.Content.ReadFromJsonAsync<SttResponse>(cancellationToken);

        if (result is null)
        {
            throw new InvalidOperationException("STT API response was empty.");
        }

        if (string.IsNullOrWhiteSpace(result.Text))
        {
            throw new InvalidOperationException("STT API response did not contain recognized text.");
        }

        return result.Text;
    }

    private Uri BuildRequestUri()
    {
        var baseUri = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        var path = _options.TranscribePath.TrimStart('/');
        var builder = new UriBuilder(new Uri(baseUri, path));

        if (!string.IsNullOrWhiteSpace(_options.DecodingType))
        {
            // decoding_type は tdt / ctc などを切り替えるためのSTT API側パラメータです。
            // 設定ファイルで値を変えられるようにして、コード変更なしで試せるようにしています。
            builder.Query = $"decoding_type={Uri.EscapeDataString(_options.DecodingType)}";
        }

        return builder.Uri;
    }

    private sealed class SttResponse
    {
        [JsonPropertyName("text")]
        public string? Text { get; init; }

        [JsonPropertyName("confidence")]
        public double? Confidence { get; init; }

        [JsonPropertyName("duration")]
        public double? Duration { get; init; }
    }
}
