using Microsoft.AspNetCore.Mvc;
using STSApp.Backend.Repositories;
using STSApp.Backend.Services;
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

    public ConversationsController(
        IConversationRepository repository,
        IConversationWorkflow conversationWorkflow)
    {
        _repository = repository;
        _conversationWorkflow = conversationWorkflow;
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
        var turns = await _repository.ListConversationTurnsAsync(conversationId, cancellationToken);
        return Ok(turns);
    }

    [HttpPost("{conversationId:guid}/turns/audio")]
    public async Task<ActionResult<TurnCreatedResponse>> CreateAudioTurn(
        Guid conversationId,
        IFormFile audioFile,
        CancellationToken cancellationToken)
    {
        if (audioFile.Length == 0)
        {
            return BadRequest("音声ファイルが空です。");
        }

        try
        {
            var turn = await _conversationWorkflow.ProcessAudioTurnAsync(
                conversationId,
                audioFile,
                cancellationToken);

            return Ok(new TurnCreatedResponse(conversationId, turn.Id));
        }
        catch (Exception)
        {
            // Workflow側では、失敗したステージやエラー情報をDBへ保存しています。
            // Controller側では、Avaloniaが画面表示に使いやすいHTTPレスポンスへ変換します。
            return Problem(
                title: "音声処理に失敗しました。",
                detail: "音声対話の処理を完了できませんでした。少し時間を置いて再度お試しください。",
                statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
