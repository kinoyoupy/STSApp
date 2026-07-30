using Microsoft.AspNetCore.SignalR;

namespace STSApp.Backend.Hubs;

/// <summary>
/// 会話ターンの状態変化をAvaloniaへ通知するためのSignalR Hubです。
/// 具体的な通知メソッド名は、Contracts/Events の型と合わせて使います。
/// </summary>
public sealed class ConversationHub : Hub
{
    public Task JoinConversation(Guid conversationId)
    {
        // 通知先を会話ごとに分けます。
        // Desktop側で不要な通知を捨てるだけでは、他の会話本文も一度は端末へ届いてしまうためです。
        return Groups.AddToGroupAsync(Context.ConnectionId, GetGroupName(conversationId));
    }

    public static string GetGroupName(Guid conversationId)
    {
        return $"conversation:{conversationId:N}";
    }
}
