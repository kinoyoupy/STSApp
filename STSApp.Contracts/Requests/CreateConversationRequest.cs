using System.ComponentModel.DataAnnotations;

namespace STSApp.Contracts.Requests;

/// <summary>
/// 新しい会話セッションを作る時のリクエストです。
/// title を省略した場合、Backend側で既定のタイトルを付ける想定です。
/// </summary>
public sealed class CreateConversationRequest
{
    // DB側のtitleは255文字です。
    // API入口でも同じ上限を検査し、DB例外になる前に「入力が長すぎる」と400で返せるようにします。
    // 通常のクラスのプロパティへ付けることで、ASP.NET Coreと単体テストが同じ情報を検証します。
    [StringLength(255, ErrorMessage = "会話タイトルは255文字以内で入力してください。")]
    public string? Title { get; init; }
}
