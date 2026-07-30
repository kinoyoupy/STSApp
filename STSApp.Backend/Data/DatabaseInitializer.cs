using Microsoft.EntityFrameworkCore;
using STSApp.Backend.Domain.Entities;
using STSApp.Contracts.Enums;

namespace STSApp.Backend.Data;

/// <summary>
/// 開発環境のスキーマ作成と、前回停止時に残った未完了ターンの回収を行います。
/// スキーマ作成は開発環境だけで使い、未完了ターンの回収はどの環境でも起動時に行います。
/// </summary>
public sealed class DatabaseInitializer
{
    private readonly StsDbContext? _dbContext;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(
        ILogger<DatabaseInitializer> logger,
        StsDbContext? dbContext = null)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task EnsureCreatedAsync(CancellationToken cancellationToken)
    {
        if (_dbContext is null)
        {
            _logger.LogWarning("MySQL connection string is not configured. Database initialization was skipped.");
            return;
        }

        var created = await _dbContext.Database.EnsureCreatedAsync(cancellationToken);
        if (created)
        {
            _logger.LogInformation("Database schema was created.");
        }
        else
        {
            _logger.LogInformation("Database schema already exists.");
        }
    }

    public async Task RecoverInterruptedTurnsAsync(CancellationToken cancellationToken)
    {
        if (_dbContext is null)
        {
            return;
        }

        // Backend起動直後には、このプロセスが処理中のターンはまだ存在しません。
        // そのため、この時点でprocessingのターンは前回の停止で完了状態を書けなかったものと判断できます。
        var interruptedTurns = await _dbContext.ConversationTurns
            .Where(turn => turn.Status == TurnStatus.Processing)
            .ToListAsync(cancellationToken);

        if (interruptedTurns.Count == 0)
        {
            return;
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        var occurredAt = DateTime.UtcNow;

        foreach (var turn in interruptedTurns)
        {
            // 最後に開始していた処理段階を失敗箇所として残します。
            // イベントが1件もない場合は、ターン作成直後に止まったものとしてuploadを使います。
            var lastStage = await _dbContext.TurnEvents
                .Where(turnEvent => turnEvent.ConversationTurnId == turn.Id)
                .OrderByDescending(turnEvent => turnEvent.OccurredAt)
                .Select(turnEvent => (ProcessingStage?)turnEvent.Stage)
                .FirstOrDefaultAsync(cancellationToken)
                ?? ProcessingStage.Upload;

            const string recoveryMessage = "Backend停止により処理を完了できませんでした。";
            turn.Status = TurnStatus.Failed;
            turn.ErrorStage = lastStage;
            turn.ErrorMessage = recoveryMessage;
            turn.UpdatedAt = occurredAt;

            _dbContext.TurnEvents.Add(new TurnEventEntity
            {
                ConversationTurnId = turn.Id,
                Stage = lastStage,
                EventType = TurnEventType.Failed,
                Message = recoveryMessage,
                OccurredAt = occurredAt
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogWarning(
            "Recovered {TurnCount} conversation turns that were left processing by a previous Backend stop.",
            interruptedTurns.Count);
    }
}
