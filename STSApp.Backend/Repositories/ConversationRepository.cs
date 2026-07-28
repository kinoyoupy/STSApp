using Microsoft.EntityFrameworkCore;
using STSApp.Backend.Data;
using STSApp.Backend.Domain.Entities;
using STSApp.Contracts.Enums;
using STSApp.Contracts.Models;

namespace STSApp.Backend.Repositories;

/// <summary>
/// conversations / conversation_turns を扱うRepositoryです。
/// ControllerやWorkflowがDbContextを直接触りすぎないように、DB操作をここへ集めます。
/// </summary>
public sealed class ConversationRepository : IConversationRepository
{
    private readonly StsDbContext _dbContext;

    public ConversationRepository(StsDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<ConversationDto> CreateConversationAsync(
        string? title,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var conversation = new ConversationEntity
        {
            Id = Guid.NewGuid(),
            Title = string.IsNullOrWhiteSpace(title) ? "新しい会話" : title.Trim(),
            CreatedAt = now,
            UpdatedAt = now
        };

        _dbContext.Conversations.Add(conversation);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return ToDto(conversation);
    }

    public async Task<IReadOnlyList<ConversationDto>> ListConversationsAsync(
        CancellationToken cancellationToken)
    {
        var conversations = await _dbContext.Conversations
            .AsNoTracking()
            .OrderByDescending(x => x.UpdatedAt)
            .ToListAsync(cancellationToken);

        return conversations.Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<ConversationTurnDto>> ListConversationTurnsAsync(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        var turns = await _dbContext.ConversationTurns
            .AsNoTracking()
            .Where(x => x.ConversationId == conversationId)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(cancellationToken);

        return turns.Select(ToDto).ToList();
    }

    public async Task<ConversationTurnDto> CreateProcessingTurnAsync(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        // 音声アップロード直後は、まだSTT/Gemini/TTSが終わっていません。
        // そのため、まずProcessing状態のターンを作り、後続処理で内容を埋めていきます。
        var now = DateTime.UtcNow;
        var turn = new ConversationTurnEntity
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            Status = TurnStatus.Processing,
            CreatedAt = now,
            UpdatedAt = now
        };

        _dbContext.ConversationTurns.Add(turn);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return ToDto(turn);
    }

    public async Task AddTurnEventAsync(
        Guid turnId,
        ProcessingStage stage,
        TurnEventType eventType,
        string? message,
        int? durationMs,
        CancellationToken cancellationToken)
    {
        // turn_events は「いつ、どの処理段階で、何が起きたか」を残すログです。
        // UIに表示しない細かい情報でも、後から調査できるようDBへ保存します。
        var turnEvent = new TurnEventEntity
        {
            ConversationTurnId = turnId,
            Stage = stage,
            EventType = eventType,
            Message = message,
            DurationMs = durationMs,
            OccurredAt = DateTime.UtcNow
        };

        _dbContext.TurnEvents.Add(turnEvent);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<AudioFileDto> AddAudioFileAsync(
        Guid turnId,
        AudioFileKind kind,
        string filePath,
        string mimeType,
        long? fileSizeBytes,
        CancellationToken cancellationToken)
    {
        // 音声ファイル本体はDBに入れません。
        // DBには、ファイルの種類(input/output)、保存パス、MIMEタイプ、サイズだけを保存します。
        var audioFile = new AudioFileEntity
        {
            Id = Guid.NewGuid(),
            ConversationTurnId = turnId,
            Kind = kind,
            FilePath = filePath,
            MimeType = mimeType,
            FileSizeBytes = fileSizeBytes,
            CreatedAt = DateTime.UtcNow
        };

        _dbContext.AudioFiles.Add(audioFile);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return ToDto(audioFile);
    }

    public async Task<AudioFileDto?> GetAudioFileAsync(
        Guid audioId,
        CancellationToken cancellationToken)
    {
        var audioFile = await _dbContext.AudioFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == audioId, cancellationToken);

        return audioFile is null ? null : ToDto(audioFile);
    }

    public async Task UpdateUserTextAsync(
        Guid turnId,
        string userText,
        CancellationToken cancellationToken)
    {
        var turn = await _dbContext.ConversationTurns
            .FirstAsync(x => x.Id == turnId, cancellationToken);

        turn.UserText = userText;
        turn.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAssistantTextAsync(
        Guid turnId,
        string assistantText,
        CancellationToken cancellationToken)
    {
        var turn = await _dbContext.ConversationTurns
            .FirstAsync(x => x.Id == turnId, cancellationToken);

        turn.AssistantText = assistantText;
        turn.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<(string UserText, string AssistantText)>> ListRecentCompletedTurnsAsync(
        Guid conversationId,
        Guid excludeTurnId,
        int maxTurns,
        CancellationToken cancellationToken)
    {
        // Geminiへ渡す履歴は、ユーザー発話とAI返答の両方が揃っているターンに絞ります。
        // 現在処理中のターンは user_text だけ先に入るため、履歴からは除外します。
        var turns = await _dbContext.ConversationTurns
            .AsNoTracking()
            .Where(x => x.ConversationId == conversationId)
            .Where(x => x.Id != excludeTurnId)
            .Where(x => x.UserText != null && x.AssistantText != null)
            .OrderByDescending(x => x.CreatedAt)
            .Take(maxTurns)
            .OrderBy(x => x.CreatedAt)
            .Select(x => new
            {
                UserText = x.UserText!,
                AssistantText = x.AssistantText!
            })
            .ToListAsync(cancellationToken);

        return turns
            .Select(x => (x.UserText, x.AssistantText))
            .ToList();
    }

    public async Task MarkTurnCompletedAsync(
        Guid turnId,
        CancellationToken cancellationToken)
    {
        // ターン全体が完了するのは、初期版ではTTS音声の保存まで終わったタイミングです。
        // STTやGeminiだけが成功しても、音声対話としてはまだ処理中です。
        var turn = await _dbContext.ConversationTurns
            .FirstAsync(x => x.Id == turnId, cancellationToken);

        turn.Status = TurnStatus.Completed;
        turn.ErrorStage = null;
        turn.ErrorMessage = null;
        turn.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkTurnFailedAsync(
        Guid turnId,
        ProcessingStage errorStage,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        // 失敗時はターン本体に「現在状態としての失敗」を残します。
        // さらに詳細な時系列はturn_events側に残るため、一覧表示と詳細調査を分けられます。
        var turn = await _dbContext.ConversationTurns
            .FirstAsync(x => x.Id == turnId, cancellationToken);

        turn.Status = TurnStatus.Failed;
        turn.ErrorStage = errorStage;
        turn.ErrorMessage = errorMessage;
        turn.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static ConversationDto ToDto(ConversationEntity entity)
    {
        return new ConversationDto(
            entity.Id,
            entity.Title,
            entity.CreatedAt,
            entity.UpdatedAt);
    }

    private static ConversationTurnDto ToDto(ConversationTurnEntity entity)
    {
        return new ConversationTurnDto(
            entity.Id,
            entity.ConversationId,
            entity.UserText,
            entity.AssistantText,
            entity.Status,
            entity.ErrorStage,
            entity.ErrorMessage,
            entity.CreatedAt,
            entity.UpdatedAt);
    }

    private static AudioFileDto ToDto(AudioFileEntity entity)
    {
        return new AudioFileDto(
            entity.Id,
            entity.ConversationTurnId,
            entity.Kind,
            entity.FilePath,
            entity.MimeType,
            entity.DurationMs,
            entity.FileSizeBytes,
            entity.CreatedAt);
    }
}
