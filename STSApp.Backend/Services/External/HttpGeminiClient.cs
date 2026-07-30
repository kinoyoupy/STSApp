using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using STSApp.Backend.Options;

namespace STSApp.Backend.Services.External;

/// <summary>
/// Gemini APIをHTTPで呼び出す実装です。
/// 初期版ではストリーミングを使わず、1回のリクエストで返答テキストを受け取ります。
/// </summary>
public sealed class HttpGeminiClient : IGeminiClient
{
    private const int RecentTurnLimit = 6;

    private readonly HttpClient _httpClient;
    private readonly GeminiOptions _options;

    public HttpGeminiClient(HttpClient httpClient, IOptions<ExternalApiOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value.Gemini;
    }

    public async Task<string> GenerateReplyAsync(GeminiReplyRequest replyRequest, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            throw new InvalidOperationException("Gemini ApiKey is not configured.");
        }

        if (string.IsNullOrWhiteSpace(_options.ModelName))
        {
            throw new InvalidOperationException("Gemini ModelName is not configured.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.BaseUrl);

        // APIキーはAvalonia側には置かず、BackendからGeminiへ送ります。
        // DesktopアプリにAPIキーを入れると、配布時にキーが見えやすくなるためです。
        request.Headers.Add("x-goog-api-key", _options.ApiKey);

        // Geminiへは「モデル名」「システム指示」「入力テキスト」を送ります。
        // 入力テキストには、現在の発話だけでなく直近履歴も含めます。
        request.Content = JsonContent.Create(new GeminiRequest(
            _options.ModelName,
            BuildSystemInstruction(replyRequest.AnswerBasis),
            BuildInput(replyRequest)));

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // 応答本文に入力文や会話内容が含まれても、ログやDBへ残さないよう状態コードだけを扱います。
            throw new InvalidOperationException(
                $"Gemini API request failed. StatusCode={(int)response.StatusCode}.");
        }

        var result = await response.Content.ReadFromJsonAsync<GeminiResponse>(cancellationToken);

        if (result is null)
        {
            throw new InvalidOperationException("Gemini API response was empty.");
        }

        var outputText = result.GetOutputText();
        if (string.IsNullOrWhiteSpace(outputText))
        {
            throw new InvalidOperationException("Gemini API response did not contain output text.");
        }

        return outputText.Trim();
    }

    private string BuildSystemInstruction(STSApp.Contracts.Enums.AnswerBasis answerBasis)
    {
        var ragInstruction = answerBasis switch
        {
            STSApp.Contracts.Enums.AnswerBasis.KnowledgeGrounded =>
                "以下に渡されるVoiceLink資料だけを根拠に回答してください。資料に書かれていないVoiceLink固有の内容を推測で補わないでください。資料名、類似度、内部の検索処理には触れないでください。",
            STSApp.Contracts.Enums.AnswerBasis.GeneralKnowledge =>
                "VoiceLinkに関する資料は見つかっていません。直近の会話履歴にVoiceLink固有の情報が含まれていても根拠として使わず、VoiceLink固有の仕様・料金・保存期間などを推測して答えないでください。質問が一般的な内容であれば一般論として回答してください。",
            _ => throw new InvalidOperationException($"Unsupported answer basis: {answerBasis}")
        };

        return $"{_options.SystemInstruction}\n\n{ragInstruction}";
    }

    private static string BuildInput(GeminiReplyRequest request)
    {
        // Geminiへ渡す内容をここで組み立てます。
        // DBには全履歴を保存しますが、APIへは直近数ターンだけ渡して入力を大きくしすぎないようにします。
        var builder = new StringBuilder();

        if (request.RecentTurns.Count > 0)
        {
            builder.AppendLine("直近の会話履歴:");

            // 会話履歴をすべて送ると入力が大きくなりすぎるため、直近数ターンに絞ります。
            // DBには全履歴を残し、Geminiへ渡す履歴だけを短くする、という分担です。
            foreach (var turn in request.RecentTurns.TakeLast(RecentTurnLimit))
            {
                builder.AppendLine($"ユーザー: {turn.UserText}");
                builder.AppendLine($"アシスタント: {turn.AssistantText}");
            }

            builder.AppendLine();
        }

        if (request.References.Count > 0)
        {
            builder.AppendLine("VoiceLink資料（この資料だけを根拠に回答すること）:");
            foreach (var reference in request.References)
            {
                builder.AppendLine($"見出し: {reference.Heading}");
                builder.AppendLine(reference.Content);
                builder.AppendLine();
            }
        }

        builder.AppendLine("現在のユーザー発話:");
        builder.AppendLine(request.UserText);

        return builder.ToString();
    }

    private sealed record GeminiRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("system_instruction")] string SystemInstruction,
        [property: JsonPropertyName("input")] string Input);

    private sealed class GeminiResponse
    {
        [JsonPropertyName("output_text")]
        public string? OutputText { get; init; }

        [JsonPropertyName("steps")]
        public IReadOnlyList<GeminiStep>? Steps { get; init; }

        public string? GetOutputText()
        {
            if (!string.IsNullOrWhiteSpace(OutputText))
            {
                return OutputText;
            }

            // Interactions APIでは、返答文がsteps内のmodel_outputに入る場合があります。
            // その中からtext形式の内容を取り出し、複数の文章があれば順番に結合します。
            var modelOutputTexts = Steps?
                .Where(step => string.Equals(step.Type, "model_output", StringComparison.OrdinalIgnoreCase))
                .SelectMany(step => step.Content ?? Array.Empty<GeminiContent>())
                .Where(content => string.Equals(content.Type, "text", StringComparison.OrdinalIgnoreCase))
                .Select(content => content.Text)
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToArray();

            return modelOutputTexts is { Length: > 0 }
                ? string.Join(Environment.NewLine, modelOutputTexts)
                : null;
        }
    }

    private sealed class GeminiStep
    {
        [JsonPropertyName("type")]
        public string? Type { get; init; }

        [JsonPropertyName("content")]
        public IReadOnlyList<GeminiContent>? Content { get; init; }
    }

    private sealed class GeminiContent
    {
        [JsonPropertyName("type")]
        public string? Type { get; init; }

        [JsonPropertyName("text")]
        public string? Text { get; init; }
    }
}
