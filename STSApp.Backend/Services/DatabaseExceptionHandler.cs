using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace STSApp.Backend.Services;

/// <summary>
/// Controllerへ到達する前後を問わず、未処理のDB例外を共通の503応答へ変換します。
/// 各Controllerが同じcatch処理を持つと、存在確認など一部のDB呼び出しを囲み忘れるためです。
/// </summary>
public sealed class DatabaseExceptionHandler : IExceptionHandler
{
    private readonly ILogger<DatabaseExceptionHandler> _logger;

    public DatabaseExceptionHandler(ILogger<DatabaseExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (!DatabaseFailureDetector.IsDatabaseFailure(exception))
        {
            return false;
        }

        // DB例外の本文には接続先やSQL断片が含まれる場合があります。
        // 調査に必要な入口を残しつつ、設定値や会話内容を通常ログへ書き出さないようにします。
        _logger.LogError(
            "Unhandled database failure for {Method} {Path}. ExceptionType={ExceptionType}.",
            httpContext.Request.Method,
            httpContext.Request.Path,
            exception.GetType().Name);

        httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await httpContext.Response.WriteAsJsonAsync(
            new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "データベースへ接続できませんでした。",
                Detail = "会話データを保存または取得できませんでした。MySQLの状態を確認してください。"
            },
            cancellationToken);

        return true;
    }
}
