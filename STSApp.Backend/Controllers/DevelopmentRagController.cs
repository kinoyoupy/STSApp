using Microsoft.AspNetCore.Mvc;
using STSApp.Backend.Services;
using STSApp.Backend.Services.Rag;

namespace STSApp.Backend.Controllers;

/// <summary>
/// 開発中に資料の変更を明示的にDBへ取り込むためのAPIです。
/// 運用画面や認証を今回追加しないため、Development環境以外では存在しないものとして扱います。
/// </summary>
[ApiController]
[Route("api/development/rag")]
public sealed class DevelopmentRagController : ControllerBase
{
    private readonly IHostEnvironment _environment;
    private readonly IKnowledgeBaseIndexer _indexer;
    private readonly ILogger<DevelopmentRagController> _logger;

    public DevelopmentRagController(
        IHostEnvironment environment,
        IKnowledgeBaseIndexer indexer,
        ILogger<DevelopmentRagController> logger)
    {
        _environment = environment;
        _indexer = indexer;
        _logger = logger;
    }

    [HttpPost("reindex")]
    public async Task<ActionResult<RagReindexResult>> Reindex(CancellationToken cancellationToken)
    {
        if (!_environment.IsDevelopment())
        {
            return NotFound();
        }

        try
        {
            return Ok(await _indexer.ReindexAsync(cancellationToken));
        }
        catch (Exception ex) when (!DatabaseFailureDetector.IsDatabaseFailure(ex))
        {
            // Embedding APIや資料解析の失敗は、再インデックス処理の502として返します。
            // DB障害までここで捕まえると原因が同じ表示になるため、DB例外は共通処理へ渡して503にします。
            // 資料本文やローカルパスが例外文へ含まれる可能性があるため、その本文は応答・ログへ出しません。
            _logger.LogError(
                "RAG reindex failed. ExceptionType={ExceptionType}.",
                ex.GetType().Name);

            return Problem(
                title: "RAG資料の再インデックスに失敗しました。",
                detail: "資料の読み込みまたはEmbedding処理を完了できませんでした。Backendの設定と資料を確認してください。",
                statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
