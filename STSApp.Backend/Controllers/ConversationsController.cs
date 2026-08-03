using Microsoft.AspNetCore.Mvc;
using STSApp.Backend.Repositories;
using STSApp.Backend.Services;
using STSApp.Backend.Services.Storage;
using STSApp.Contracts.Models;
using STSApp.Contracts.Requests;
using STSApp.Contracts.Responses;

namespace STSApp.Backend.Controllers;

/// <summary>
/// 会話セッションと会話履歴を扱うAPIです。
/// 音声処理そのものは、別途Workflowサービスへ切り出します。
/// </summary>
[ApiController]
[Route("api/conversations")]
public sealed class ConversationsController : ControllerBase
{
    private readonly IConversationRepository _repository;
    private readonly IConversationWorkflow _conversationWorkflow;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly IAudioFileCleanupService _audioFileCleanupService;
    private readonly ILogger<ConversationsController> _logger;

    public ConversationsController(
        IConversationRepository repository,
        IConversationWorkflow conversationWorkflow,
        IHostApplicationLifetime applicationLifetime,
        IAudioFileCleanupService audioFileCleanupService,
        ILogger<ConversationsController> logger)
    {
        _repository = repository;
        _conversationWorkflow = conversationWorkflow;
        _applicationLifetime = applicationLifetime;
        _audioFileCleanupService = audioFileCleanupService;
        _logger = logger;
    }

    [HttpPost]
    public async Task<ActionResult<ConversationCreatedResponse>> CreateConversation(
        CreateConversationRequest request,
        CancellationToken cancellationToken)
    {
        var conversation = await _repository.CreateConversationAsync(request.Title, cancellationToken);
        return Ok(new ConversationCreatedResponse(conversation.Id));
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ConversationDto>>> ListConversations(
        CancellationToken cancellationToken)
    {
        var conversations = await _repository.ListConversationsAsync(cancellationToken);
        return Ok(conversations);
    }

    [HttpGet("{conversationId:guid}/turns")]
    public async Task<ActionResult<IReadOnlyList<ConversationTurnDto>>> ListConversationTurns(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        if (!await _repository.ConversationExistsAsync(conversationId, cancellationToken))
        {
            return Problem(
                title: "会話セッションが見つかりません。",
                detail: "指定されたconversationIdに対応する会話セッションは存在しません。",
                statusCode: StatusCodes.Status404NotFound);
        }

        var turns = await _repository.ListConversationTurnsAsync(conversationId, cancellationToken);
        return Ok(turns);
    }

    [HttpPost("{conversationId:guid}/turns/audio")]
    public async Task<ActionResult<TurnCreatedResponse>> CreateAudioTurn(
        Guid conversationId,
        IFormFile? audioFile,
        CancellationToken cancellationToken)
    {
        if (!await _repository.ConversationExistsAsync(conversationId, cancellationToken))
        {
            return Problem(
                title: "会話セッションが見つかりません。",
                detail: "指定されたconversationIdに対応する会話セッションは存在しません。",
                statusCode: StatusCodes.Status404NotFound);
        }

        if (audioFile is null)
        {
            ModelState.AddModelError(nameof(audioFile), "音声ファイルを指定してください。");
            return ValidationProblem(ModelState);
        }

        if (audioFile.Length == 0)
        {
            ModelState.AddModelError(nameof(audioFile), "音声ファイルが空です。");
            return ValidationProblem(ModelState);
        }

        try
        {
            // 音声処理は複数の外部APIを順番に呼ぶため、DesktopのHTTP切断より長く続く場合があります。
            // RequestAbortedをそのまま渡すと、正常処理中でもターンがprocessingのまま残るため、
            // Backendアプリ自体が停止する時だけキャンセルされるトークンを使います。
            var result = await _conversationWorkflow.ProcessAudioTurnAsync(
                conversationId,
                audioFile,
                _applicationLifetime.ApplicationStopping);

            return Ok(new TurnCreatedResponse(
                conversationId,
                result.TurnId,
                result.OutputAudioIds));
        }
        catch (Exception ex) when (!DatabaseFailureDetector.IsDatabaseFailure(ex))
        {
            // Workflow側では、失敗したステージやエラー情報をDBへ保存しています。
            // Controller側では、Avaloniaが画面表示に使いやすいHTTPレスポンスへ変換します。
            // 生の例外文には外部API応答やファイル名が含まれる可能性があるため、
            // 通常ログには会話IDと例外の種類だけを残します。
            _logger.LogError(
                "Audio conversation processing failed for conversation {ConversationId}. ExceptionType={ExceptionType}.",
                conversationId,
                ex.GetType().Name);

            return Problem(
                title: "音声処理に失敗しました。",
                detail: "音声対話の処理を完了できませんでした。少し時間を置いて再度お試しください。",
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    [HttpDelete("{conversationId:guid}/audio")]
    public async Task<IActionResult> DeleteConversationAudio(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        if (!await _repository.ConversationExistsAsync(conversationId, cancellationToken))
        {
            return Problem(
                title: "会話セッションが見つかりません。",
                detail: "指定されたconversationIdに対応する会話セッションは存在しません。",
                statusCode: StatusCodes.Status404NotFound);
        }

        await _audioFileCleanupService.CleanupConversationAsync(
            conversationId,
            cancellationToken);

        return NoContent();
    }
}
