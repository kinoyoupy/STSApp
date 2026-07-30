using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Options;
using STSApp.Backend.Options;
using STSApp.Backend.Services.External;
using STSApp.Backend.Services.Rag;

namespace STSApp.Backend.Tests;

/// <summary>
/// 外部APIのエラー本文を、例外経由でログやDBへ残さないためのテストです。
/// </summary>
public sealed class ExternalApiErrorPrivacyTests
{
    private const string PrivateResponseBody = "PRIVATE_USER_CONTENT_MUST_NOT_BE_LOGGED";

    [Fact]
    public async Task Stt_error_does_not_include_response_body()
    {
        var client = new HttpSttClient(
            CreateFailingHttpClient(),
            Microsoft.Extensions.Options.Options.Create(CreateOptions()));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.TranscribeAsync(
                new MemoryStream([1, 2, 3]),
                "recording.wav",
                "audio/wav",
                CancellationToken.None));

        Assert.DoesNotContain(PrivateResponseBody, exception.Message);
        Assert.Contains("StatusCode=400", exception.Message);
    }

    [Fact]
    public async Task Gemini_error_does_not_include_response_body()
    {
        var client = new HttpGeminiClient(
            CreateFailingHttpClient(),
            Microsoft.Extensions.Options.Options.Create(CreateOptions()));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GenerateReplyAsync(
                new GeminiReplyRequest(
                    "質問",
                    [],
                    STSApp.Contracts.Enums.AnswerBasis.GeneralKnowledge,
                    []),
                CancellationToken.None));

        Assert.DoesNotContain(PrivateResponseBody, exception.Message);
    }

    [Fact]
    public async Task Tts_error_does_not_include_response_body()
    {
        var client = new HttpTtsClient(
            CreateFailingHttpClient(),
            Microsoft.Extensions.Options.Options.Create(CreateOptions()));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SynthesizeAsync("返答", CancellationToken.None));

        Assert.DoesNotContain(PrivateResponseBody, exception.Message);
    }

    [Fact]
    public async Task Tts_rejects_json_even_when_status_is_success()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"error\":\"generation failed\"}", Encoding.UTF8, "application/json")
        };
        var client = new HttpTtsClient(
            new HttpClient(new StaticResponseHttpMessageHandler(response)),
            Microsoft.Extensions.Options.Options.Create(CreateOptions()));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SynthesizeAsync("返答", CancellationToken.None));

        Assert.Contains("not WAV audio", exception.Message);
    }

    [Fact]
    public async Task Tts_rejects_audio_content_without_wave_header()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12])
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        var client = new HttpTtsClient(
            new HttpClient(new StaticResponseHttpMessageHandler(response)),
            Microsoft.Extensions.Options.Options.Create(CreateOptions()));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SynthesizeAsync("返答", CancellationToken.None));

        Assert.Contains("valid WAV header", exception.Message);
    }

    [Fact]
    public async Task Tts_rejects_wave_signature_without_format_and_audio_chunks()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.ASCII.GetBytes("RIFF\0\0\0\0WAVE"))
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        var client = new HttpTtsClient(
            new HttpClient(new StaticResponseHttpMessageHandler(response)),
            Microsoft.Extensions.Options.Options.Create(CreateOptions()));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SynthesizeAsync("返答", CancellationToken.None));

        Assert.Contains("format and audio data chunks", exception.Message);
    }

    [Fact]
    public async Task Tts_accepts_wave_header_returned_as_generic_binary()
    {
        var waveBytes = CreateMinimalWave();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(waveBytes)
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var client = new HttpTtsClient(
            new HttpClient(new StaticResponseHttpMessageHandler(response)),
            Microsoft.Extensions.Options.Options.Create(CreateOptions()));

        var speech = await client.SynthesizeAsync("返答", CancellationToken.None);
        await using var audioStream = speech.AudioStream;

        Assert.Equal(waveBytes.Length, audioStream.Length);
    }

    private static byte[] CreateMinimalWave()
    {
        // 16-bit・モノラル・16kHzのPCM WAVと、2バイト分の無音データです。
        return
        [
            (byte)'R', (byte)'I', (byte)'F', (byte)'F',
            38, 0, 0, 0,
            (byte)'W', (byte)'A', (byte)'V', (byte)'E',
            (byte)'f', (byte)'m', (byte)'t', (byte)' ',
            16, 0, 0, 0,
            1, 0,
            1, 0,
            128, 62, 0, 0,
            0, 125, 0, 0,
            2, 0,
            16, 0,
            (byte)'d', (byte)'a', (byte)'t', (byte)'a',
            2, 0, 0, 0,
            0, 0
        ];
    }

    [Fact]
    public async Task Embedding_error_does_not_include_response_body()
    {
        var client = new HttpGeminiEmbeddingClient(
            CreateFailingHttpClient(),
            Microsoft.Extensions.Options.Options.Create(CreateOptions()),
            Microsoft.Extensions.Options.Options.Create(new RagOptions
            {
                EmbeddingModelName = "gemini-embedding-001",
                EmbeddingDimensions = 768
            }));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.EmbedQueryAsync("質問", CancellationToken.None));

        Assert.DoesNotContain(PrivateResponseBody, exception.Message);
    }

    private static HttpClient CreateFailingHttpClient()
    {
        return new HttpClient(new FailingHttpMessageHandler());
    }

    private static ExternalApiOptions CreateOptions()
    {
        return new ExternalApiOptions
        {
            Stt = new SttOptions { BaseUrl = "http://localhost" },
            Tts = new TtsOptions { BaseUrl = "http://localhost" },
            Gemini = new GeminiOptions
            {
                BaseUrl = "http://localhost",
                ApiKey = "test-key",
                ModelName = "test-model"
            }
        };
    }

    private sealed class FailingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(PrivateResponseBody)
            });
        }
    }

    private sealed class StaticResponseHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public StaticResponseHttpMessageHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(_response);
        }
    }
}
