using System.Net.Http.Json;
using System.Buffers.Binary;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using STSApp.Backend.Options;

namespace STSApp.Backend.Services.External;

/// <summary>
/// 既存TTS APIをHTTPで呼び出す実装です。
/// 仕様: POST /speak、JSONで text などを送り、WAV形式のバイナリを受け取ります。
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

        // TTS APIはJSONでテキストや話者設定を受け取ります。
        // voicepack/alpha/beta/speed は任意なので、設定されていなければnullとして送ります。
        request.Content = JsonContent.Create(new TtsRequest(
            text,
            ToNullIfWhiteSpace(_options.Voicepack),
            _options.Alpha,
            _options.Beta,
            _options.Speed));

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // TTSへ送った返答文がエラー本文へ含まれても、ログやDBへ残さないようにします。
            throw new InvalidOperationException(
                $"TTS API request failed. StatusCode={(int)response.StatusCode}.");
        }

        // HttpResponseMessageを破棄した後も保存処理で読めるように、いったんメモリへコピーします。
        var audioBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (audioBytes.Length == 0)
        {
            throw new InvalidOperationException("TTS API response audio was empty.");
        }

        ValidateWaveResponse(response, audioBytes);

        return new GeneratedSpeech(
            new MemoryStream(audioBytes),
            _options.ResponseMimeType,
            _options.ResponseFileExtension);
    }

    private static void ValidateWaveResponse(HttpResponseMessage response, byte[] audioBytes)
    {
        // HTTP 200でも、APIや中継サーバーがJSON・HTMLのエラー本文を返す場合があります。
        // それを.wavとして保存するとBackendでは成功、Desktopでは再生失敗となり、
        // 本当はTTSで起きた問題を別の段階の問題として扱ってしまうため、保存前に実データを確認します。
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!string.IsNullOrWhiteSpace(mediaType)
            && !IsAcceptedWaveContentType(mediaType))
        {
            throw new InvalidOperationException(
                $"TTS API response was not WAV audio. ContentType={mediaType}.");
        }

        // 一般的なWAVは、先頭4バイトがRIFF、8バイト目からの4バイトがWAVEです。
        // さらに音声形式を示すfmtチャンクと、実音声を示すdataチャンクも必要です。
        // 名前だけWAVに見える壊れたデータを保存しないよう、チャンク境界まで確認します。
        if (audioBytes.Length < 12
            || audioBytes[0] != (byte)'R'
            || audioBytes[1] != (byte)'I'
            || audioBytes[2] != (byte)'F'
            || audioBytes[3] != (byte)'F'
            || audioBytes[8] != (byte)'W'
            || audioBytes[9] != (byte)'A'
            || audioBytes[10] != (byte)'V'
            || audioBytes[11] != (byte)'E')
        {
            throw new InvalidOperationException("TTS API response did not contain a valid WAV header.");
        }

        var hasFormatChunk = false;
        var hasAudioDataChunk = false;
        var chunkOffset = 12;

        while (chunkOffset + 8 <= audioBytes.Length)
        {
            var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(
                audioBytes.AsSpan(chunkOffset + 4, 4));
            var chunkDataOffset = chunkOffset + 8;
            var chunkEndOffset = (long)chunkDataOffset + chunkSize;
            if (chunkEndOffset > audioBytes.Length)
            {
                throw new InvalidOperationException("TTS API response contained a broken WAV chunk.");
            }

            var chunkId = audioBytes.AsSpan(chunkOffset, 4);
            if (chunkId.SequenceEqual("fmt "u8))
            {
                hasFormatChunk = chunkSize >= 16;
            }
            else if (chunkId.SequenceEqual("data"u8))
            {
                hasAudioDataChunk = chunkSize > 0;
            }

            // RIFFチャンクは奇数サイズの場合に1バイトの詰め物を置くため、その分も読み飛ばします。
            chunkOffset = checked((int)(chunkEndOffset + (chunkSize & 1)));
        }

        if (!hasFormatChunk || !hasAudioDataChunk)
        {
            throw new InvalidOperationException(
                "TTS API response did not contain WAV format and audio data chunks.");
        }
    }

    private static bool IsAcceptedWaveContentType(string mediaType)
    {
        // application/octet-streamは「汎用バイナリ」という意味です。
        // WAVヘッダーが正しければ音声として判定できるため、既存APIとの互換性を保って受け入れます。
        return mediaType.Equals("audio/wav", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("audio/x-wav", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("audio/wave", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase);
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
