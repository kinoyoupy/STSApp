using STSApp.Backend.Services.Rag;
using STSApp.Contracts.Enums;

namespace STSApp.Backend.Services.External;

/// <summary>
/// Geminiへ渡す会話情報とRAG検索結果をまとめた入力です。
/// 資料の有無と回答方針を同じ入力にすることで、呼び出し側とGemini側の判断をずらしません。
/// </summary>
public sealed record GeminiReplyRequest(
    string UserText,
    IReadOnlyList<(string UserText, string AssistantText)> RecentTurns,
    AnswerBasis AnswerBasis,
    IReadOnlyList<RetrievedKnowledgeChunk> References);
