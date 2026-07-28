using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using STSApp.Contracts.Events;

namespace STSApp.Desktop;

/// <summary>
/// BackendのSignalR Hubへ接続し、会話ターンの状態通知を受け取るクライアントです。
///
/// SignalRを別クラスにする理由は、MainWindowに接続管理の細かい処理を書き込まないためです。
/// RESTがこちらから問い合わせる通信なのに対し、SignalRはBackendから状態を知らせてもらう通信です。
/// </summary>
public sealed class ConversationHubClient : IAsyncDisposable
{
    private readonly HubConnection _connection;

    public ConversationHubClient(string backendBaseUrl)
    {
        var hubUrl = new Uri(new Uri(backendBaseUrl.TrimEnd('/') + "/"), "hubs/conversation");

        // HubConnection はSignalRの接続本体です。
        // WithAutomaticReconnect を付けることで、Backend再起動などで一時的に切れても再接続を試みます。
        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .AddJsonProtocol(options =>
            {
                // BackendのSignalR通知も enum を snake_case 文字列で送ります。
                // Desktop側も同じ変換設定にして、ProcessingStage.Stt などへ戻せるようにします。
                options.PayloadSerializerOptions.Converters.Add(
                    new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
            })
            .WithAutomaticReconnect()
            .Build();

        _connection.On<TurnStatusChangedEvent>("turnStatusChanged", value =>
        {
            // Backend側の SendAsync("turnStatusChanged", ...) と同じ名前で受信します。
            // 受け取った後はMainWindowへeventとして渡し、画面更新はMainWindow側で行います。
            TurnStatusChanged?.Invoke(value);
        });

        _connection.On<TranscriptionCompletedEvent>("transcriptionCompleted", value =>
        {
            TranscriptionCompleted?.Invoke(value);
        });

        _connection.On<AssistantTextCompletedEvent>("assistantTextCompleted", value =>
        {
            AssistantTextCompleted?.Invoke(value);
        });

        _connection.On<SpeechSynthesisCompletedEvent>("speechSynthesisCompleted", value =>
        {
            SpeechSynthesisCompleted?.Invoke(value);
        });

        _connection.On<TurnFailedEvent>("turnFailed", value =>
        {
            TurnFailed?.Invoke(value);
        });
    }

    public event Action<TurnStatusChangedEvent>? TurnStatusChanged;
    public event Action<TranscriptionCompletedEvent>? TranscriptionCompleted;
    public event Action<AssistantTextCompletedEvent>? AssistantTextCompleted;
    public event Action<SpeechSynthesisCompletedEvent>? SpeechSynthesisCompleted;
    public event Action<TurnFailedEvent>? TurnFailed;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_connection.State == HubConnectionState.Disconnected)
        {
            await _connection.StartAsync(cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }
}
