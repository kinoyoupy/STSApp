using Microsoft.EntityFrameworkCore;
using STSApp.Backend.Data;

namespace STSApp.Backend.Health;

/// <summary>
/// BackendからMySQLへ接続できるかを確認するための簡易ヘルスチェックです。
/// テーブル作成前でも、DBへの接続可否だけを確認できます。
/// </summary>
public sealed class DatabaseHealthCheck
{
    private readonly StsDbContext? _dbContext;

    public DatabaseHealthCheck(StsDbContext? dbContext = null)
    {
        _dbContext = dbContext;
    }

    public async Task<bool> CanConnectAsync(CancellationToken cancellationToken)
    {
        if (_dbContext is null)
        {
            return false;
        }

        return await _dbContext.Database.CanConnectAsync(cancellationToken);
    }
}
