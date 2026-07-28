using Microsoft.EntityFrameworkCore;

namespace STSApp.Backend.Data;

/// <summary>
/// 開発環境向けに、DBスキーマが無い場合だけ作成する初期化処理です。
/// 本格運用ではMigration管理に切り替える想定ですが、まずは実装確認を進めるために使います。
/// </summary>
public sealed class DatabaseInitializer
{
    private readonly StsDbContext? _dbContext;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(StsDbContext? dbContext, ILogger<DatabaseInitializer> logger)
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
}
