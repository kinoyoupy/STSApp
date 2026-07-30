namespace STSApp.Contracts.Enums;

/// <summary>
/// AI返答が何を根拠に作られたかを表します。
/// 資料が見つからなかった時も失敗ではないため、一般的な知識による返答を区別して残します。
/// </summary>
public enum AnswerBasis
{
    KnowledgeGrounded,
    GeneralKnowledge
}
