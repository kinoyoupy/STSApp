using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using STSApp.Backend.Options;

namespace STSApp.Backend.Services.External;

/// <summary>
/// 既存STT APIをHTTPで呼び出す実装です。
/// 仕様: POST /transcribe?decoding_type=tdt、multipart/form-data の file に音声を入れます。
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

        // STT APIはJSONではなく、multipart/form-dataで音声ファイルを受け取ります。
        // form.Add の第2引数 "file" は、STT API仕様で決まっているフィールド名です。
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);

        form.Add(fileContent, "file", fileName);
        request.Content = form;

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // 外部APIのエラー本文には、送信した音声の認識結果や診断情報が含まれる可能性があります。
            // 例外はログとturn_eventsにも渡るため、本文を含めずHTTP状態コードだけを記録します。
            throw new InvalidOperationException(
                $"STT API request failed. StatusCode={(int)response.StatusCode}.");
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
