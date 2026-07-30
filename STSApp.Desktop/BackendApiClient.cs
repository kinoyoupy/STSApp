using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using STSApp.Contracts.Models;
using STSApp.Contracts.Requests;
using STSApp.Contracts.Responses;

namespace STSApp.Desktop;

/// <summary>
/// AvaloniaアプリからBackendのREST APIを呼び出すための小さなクライアントです。
/// 画面コードからHttpClientの細かい処理を直接触らないように、ここへ集めます。
/// </summary>
public sealed class BackendApiClient : IDisposable
{
    // System.Text.Json は既定ではC#のenum文字列をそのまま扱います。
    // Backend側は snake_case の文字列としてenumを返すため、Desktop側も同じ設定を使います。
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly HttpClient _httpClient;
    private readonly HttpClient _audioTurnHttpClient;

    public BackendApiClient(string baseUrl)
    {
        // HttpClientはAPI呼び出しごとにnewせず、クラス内で使い回します。
        // 短時間に何度も作ると接続管理が複雑になり、通信の不安定さにつながるためです。
        _httpClient = CreateHttpClient(baseUrl, TimeSpan.FromSeconds(60));

        // 音声送信APIはSTT・RAG・Gemini・TTSを順番に待つため、合計時間を固定60秒で切れません。
        // Windowを閉じた時のCancellationTokenは引き続き使い、利用者がアプリを閉じた時は待機を終了します。
        _audioTurnHttpClient = CreateHttpClient(baseUrl, Timeout.InfiniteTimeSpan);
    }

    private static HttpClient CreateHttpClient(string baseUrl, TimeSpan timeout)
    {
        return new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = timeout
        };
    }

    public async Task<Guid> CreateConversationAsync(
        string? title,
        CancellationToken cancellationToken)
    {
        // 会話セッションをBackendに作ります。
        // ここで返るconversationIdは、以降の音声アップロードや履歴取得で使う「会話のキー」です。
        var response = await _httpClient.PostAsJsonAsync(
            "api/conversations",
            new CreateConversationRequest { Title = title },
            JsonOptions,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ConversationCreatedResponse>(
            JsonOptions,
            cancellationToken);

        if (result is null)
        {
            throw new InvalidOperationException("Backend returned empty conversation response.");
        }

        return result.ConversationId;
    }

    public async Task<IReadOnlyList<ConversationTurnDto>> ListConversationTurnsAsync(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        // DBに保存済みの会話ターンを取得します。
        // SignalRはリアルタイム通知、REST履歴取得は「保存済み状態の再取得」という役割です。
        var turns = await _httpClient.GetFromJsonAsync<IReadOnlyList<ConversationTurnDto>>(
            $"api/conversations/{conversationId}/turns",
            JsonOptions,
            cancellationToken);

        return turns ?? Array.Empty<ConversationTurnDto>();
    }

    public async Task<TurnCreatedResponse> UploadAudioTurnAsync(
        Guid conversationId,
        RecordedAudio audio,
        CancellationToken cancellationToken)
    {
        // 音声ファイル送信はJSONではなく multipart/form-data を使います。
        // これはブラウザやアプリからファイルを送る時によく使われる形式です。
        using var form = new MultipartFormDataContent();
        using var audioContent = new ByteArrayContent(audio.Bytes);

        audioContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(audio.ContentType);

        // Backend側のControllerは IFormFile audioFile という名前で受け取るため、
        // multipartのフィールド名も "audioFile" に合わせます。
        form.Add(audioContent, "audioFile", audio.FileName);

        var response = await _audioTurnHttpClient.PostAsync(
            $"api/conversations/{conversationId}/turns/audio",
            form,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorMessage = await ReadShortErrorMessageAsync(response, cancellationToken);
            throw new InvalidOperationException(errorMessage);
        }

        var result = await response.Content.ReadFromJsonAsync<TurnCreatedResponse>(
            JsonOptions,
            cancellationToken);

        if (result is null)
        {
            throw new InvalidOperationException("Backend returned empty turn response.");
        }

        return result;
    }

    private static async Task<string> ReadShortErrorMessageAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return $"Backend returned {(int)response.StatusCode} {response.ReasonPhrase}.";
        }

        // ProblemDetailsのJSONやHTMLが長くなる場合があるため、画面に出す文字数は絞ります。
        // 詳細調査はBackendログやDBのturn_eventsで行います。
        var trimmed = responseText.Length > 300
            ? responseText[..300] + "..."
            : responseText;

        return $"Backend returned {(int)response.StatusCode} {response.ReasonPhrase}: {trimmed}";
    }

    public async Task<byte[]> DownloadAudioAsync(
        Guid audioId,
        CancellationToken cancellationToken)
    {
        // TTSで生成された返答音声をBackendから取得します。
        // Backend側ではaudio_files.idをaudioIdとして扱い、実ファイルを読み出して返します。
        var response = await _httpClient.GetAsync(
            $"api/audio/{audioId}",
            cancellationToken);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var memoryStream = new MemoryStream();
        await stream.CopyToAsync(memoryStream, cancellationToken);

        return memoryStream.ToArray();
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _audioTurnHttpClient.Dispose();
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        // Backendは enum を "failed" や "stt" のような snake_case 文字列で返します。
        // Desktop側も同じ設定にしないと、履歴取得時にJSONをDTOへ戻せません。
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));

        return options;
    }
}
