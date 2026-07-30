using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace STSApp.Backend.Services;

/// <summary>
/// 例外がDB処理に由来するかを判定します。
/// 外側の例外だけでなくInnerExceptionも確認し、MySQL接続例外などをDatabase段階へ分類します。
/// </summary>
public static class DatabaseFailureDetector
{
    public static bool IsDatabaseFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            // DbExceptionはMySQLなど各DBドライバーの共通基底型です。
            // DbUpdateExceptionは、EF CoreがINSERT/UPDATE失敗を包んで返す例外です。
            if (current is DbException or DbUpdateException)
            {
                return true;
            }
        }

        return false;
    }
}
