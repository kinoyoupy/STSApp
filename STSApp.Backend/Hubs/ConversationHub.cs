using Microsoft.AspNetCore.SignalR;

namespace STSApp.Backend.Hubs;

/// <summary>
/// 会話ターンの状態変化をAvaloniaへ通知するためのSignalR Hubです。
/// 具体的な通知メソッド名は、Contracts/Events の型と合わせて使います。
/// </summary>
public sealed class ConversationHub : Hub
{
}
