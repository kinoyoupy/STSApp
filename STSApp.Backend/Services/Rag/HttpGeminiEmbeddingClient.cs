using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using STSApp.Backend.Options;

namespace STSApp.Backend.Services.Rag;

/// <summary>
/// Gemini Embedding APIをHTTPで呼び出す実装です。
/// 文書と質問でtaskTypeを分け、検索用途に合う意味ベクトルを取得します。
/// </summary>
public sealed class HttpGeminiEmbeddingClient : IEmbeddingClient
{
    private readonly HttpClient _httpClient;
    private readonly ExternalApiOptions _externalApiOptions;
    private readonly RagOptions _ragOptions;

    public HttpGeminiEmbeddingClient(
        HttpClient httpClient,
        IOptions<ExternalApiOptions> externalApiOptions,
        IOptions<RagOptions> ragOptions)
    {
        _httpClient = httpClient;
        _externalApiOptions = externalApiOptions.Value;
        _ragOptions = ragOptions.Value;
    }

    public Task<float[]> EmbedDocumentAsync(string text, string title, CancellationToken cancellationToken)
    {
        return EmbedAsync(text, "RETRIEVAL_DOCUMENT", title, cancellationToken);
    }

    public Task<float[]> EmbedQueryAsync(string text, CancellationToken cancellationToken)
    {
        return EmbedAsync(text, "RETRIEVAL_QUERY", null, cancellationToken);
    }

    private async Task<float[]> EmbedAsync(
        string text,
        string taskType,
        string? title,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_externalApiOptions.Gemini.ApiKey))
        {
            throw new InvalidOperationException("Gemini ApiKey is not configured for RAG Embedding.");
        }

        var modelPath = $"models/{_ragOptions.EmbeddingModelName}";
        var endpoint = $"https://generativelanguage.googleapis.com/v1beta/{modelPath}:embedContent";
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Add("x-goog-api-key", _externalApiOptions.Gemini.ApiKey);
        request.Content = JsonContent.Create(new GeminiEmbeddingRequest(
            modelPath,
            new GeminiEmbeddingContent([new GeminiEmbeddingPart(text)]),
            taskType,
            title,
            _ragOptions.EmbeddingDimensions));

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // Embedding対象の資料本文や質問がエラー本文へ含まれても、ログやAPI応答へ流さないようにします。
            throw new InvalidOperationException(
                $"Gemini Embedding API request failed. StatusCode={(int)response.StatusCode}.");
        }

        var result = await response.Content.ReadFromJsonAsync<GeminiEmbeddingResponse>(cancellationToken);
        var values = result?.Embedding?.Values;
        if (values is null || values.Count != _ragOptions.EmbeddingDimensions)
        {
            throw new InvalidOperationException(
                $"Gemini Embedding API response has an invalid vector dimension. Expected={_ragOptions.EmbeddingDimensions}, Actual={values?.Count ?? 0}");
        }

        return values.ToArray();
    }

    private sealed record GeminiEmbeddingRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("content")] GeminiEmbeddingContent Content,
        [property: JsonPropertyName("taskType")] string TaskType,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("outputDimensionality")] int OutputDimensionality);

    private sealed record GeminiEmbeddingContent(
        [property: JsonPropertyName("parts")] IReadOnlyList<GeminiEmbeddingPart> Parts);

    private sealed record GeminiEmbeddingPart(
        [property: JsonPropertyName("text")] string Text);

    private sealed class GeminiEmbeddingResponse
    {
        [JsonPropertyName("embedding")]
        public GeminiEmbedding? Embedding { get; init; }
    }

    private sealed class GeminiEmbedding
    {
        [JsonPropertyName("values")]
        public IReadOnlyList<float>? Values { get; init; }
    }
}
