using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Options;
using STSApp.Backend.Options;
using STSApp.Backend.Services.External;
using STSApp.Contracts.Enums;

namespace STSApp.Backend.Tests;

public sealed class HttpGeminiStreamingTests
{
    [Fact]
    public async Task Streams_text_deltas_and_ignores_unknown_events()
    {
        var handler = new CapturingHandler(CreateSseResponse(
            "data: {\"event_type\":\"interaction.created\"}\n\n"
            + "data: {\"event_type\":\"future.event\",\"value\":1}\n\n"
            + "data: {\"event_type\":\"step.delta\",\"delta\":{\"type\":\"text\",\"text\":\"一文目。\"}}\n\n"
            + "data: {\"event_type\":\"step.delta\",\"delta\":{\"type\":\"text\",\"text\":\"二文目。\"}}\n\n"
            + "data: {\"event_type\":\"interaction.completed\"}\n\n"
            + "data: [DONE]\n\n"));
        var client = CreateClient(handler);
        var deltas = new List<string>();

        await foreach (var delta in client.StreamReplyAsync(CreateRequest(), CancellationToken.None))
        {
            deltas.Add(delta);
        }

        Assert.Equal(["一文目。", "二文目。"], deltas);
        Assert.Contains("\"stream\":true", handler.RequestBody);
        Assert.Contains(handler.Accept, value => value.MediaType == "text/event-stream");
    }

    [Fact]
    public async Task Rejects_stream_without_completion_event()
    {
        var client = CreateClient(new CapturingHandler(CreateSseResponse(
            "data: {\"event_type\":\"step.delta\",\"delta\":{\"type\":\"text\",\"text\":\"途中\"}}\n\n")));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => CollectAsync(client));

        Assert.Contains("before completion", exception.Message);
    }

    [Fact]
    public async Task Rejects_invalid_stream_event_without_exposing_body()
    {
        var client = CreateClient(new CapturingHandler(CreateSseResponse("data: {PRIVATE}\n\n")));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => CollectAsync(client));

        Assert.DoesNotContain("PRIVATE", exception.Message);
        Assert.Contains("invalid stream event", exception.Message);
    }

    [Fact]
    public async Task Reads_sse_lines_split_across_individual_network_bytes()
    {
        const string body = "data: {\"event_type\":\"step.delta\",\"delta\":{\"type\":\"text\",\"text\":\"分割。\"}}\n\n"
            + "data: {\"event_type\":\"interaction.completed\"}\n\n";
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new OneByteAtATimeStream(Encoding.UTF8.GetBytes(body)))
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        var client = CreateClient(new CapturingHandler(response));
        var deltas = new List<string>();

        await foreach (var delta in client.StreamReplyAsync(CreateRequest(), CancellationToken.None))
        {
            deltas.Add(delta);
        }

        Assert.Equal(["分割。"], deltas);
    }

    [Fact]
    public async Task Rejects_completed_stream_without_text()
    {
        var client = CreateClient(new CapturingHandler(CreateSseResponse(
            "data: {\"event_type\":\"interaction.completed\"}\n\n")));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => CollectAsync(client));

        Assert.Contains("did not contain output text", exception.Message);
    }

    [Fact]
    public async Task Honors_cancellation_before_sending_the_request()
    {
        var client = CreateClient(new CapturingHandler(CreateSseResponse(string.Empty)));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in client.StreamReplyAsync(CreateRequest(), cancellation.Token))
            {
            }
        });
    }

    private static async Task CollectAsync(HttpGeminiClient client)
    {
        await foreach (var _ in client.StreamReplyAsync(CreateRequest(), CancellationToken.None))
        {
        }
    }

    private static HttpGeminiClient CreateClient(HttpMessageHandler handler)
    {
        return new HttpGeminiClient(
            new HttpClient(handler),
            Microsoft.Extensions.Options.Options.Create(new ExternalApiOptions
            {
                Gemini = new GeminiOptions
                {
                    BaseUrl = "http://localhost/interactions",
                    ApiKey = "test-key",
                    ModelName = "test-model"
                }
            }));
    }

    private static GeminiReplyRequest CreateRequest()
    {
        return new GeminiReplyRequest("質問", [], AnswerBasis.GeneralKnowledge, []);
    }

    private static HttpResponseMessage CreateSseResponse(string body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
        };
        return response;
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public CapturingHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        public string RequestBody { get; private set; } = string.Empty;
        public IReadOnlyList<MediaTypeWithQualityHeaderValue> Accept { get; private set; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            Accept = request.Headers.Accept.ToArray();
            return _response;
        }
    }

    private sealed class OneByteAtATimeStream : MemoryStream
    {
        public OneByteAtATimeStream(byte[] buffer)
            : base(buffer)
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => base.Read(buffer, offset, Math.Min(count, 1));

        public override int Read(Span<byte> buffer)
            => base.Read(buffer[..Math.Min(buffer.Length, 1)]);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
    }
}
