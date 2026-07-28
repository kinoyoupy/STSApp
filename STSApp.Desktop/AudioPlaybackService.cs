using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace STSApp.Desktop;

/// <summary>
/// Backendから取得した返答音声を再生するクラスです。
///
/// 再生処理を別クラスにする理由は、画面のコードにOS固有の再生方法を混ぜないためです。
/// macOSのafplayはファイルを再生する仕組みなので、Backendから受け取ったbyte[]を一時ファイルへ書きます。
/// </summary>
public sealed class AudioPlaybackService
{
    public async Task PlayWavAsync(
        byte[] audioBytes,
        CancellationToken cancellationToken)
    {
        if (audioBytes.Length == 0)
        {
            throw new InvalidOperationException("Audio data is empty.");
        }

        var tempFilePath = Path.Combine(
            Path.GetTempPath(),
            $"stsapp-playback-{Guid.NewGuid():N}.wav");

        // afplayはファイルパスを受け取って再生するコマンドなので、
        // Backendから取得したバイト列を一度一時WAVファイルとして保存します。
        await File.WriteAllBytesAsync(tempFilePath, audioBytes, cancellationToken);

        try
        {
            await PlayWithAfplayAsync(tempFilePath, cancellationToken);
        }
        finally
        {
            TryDelete(tempFilePath);
        }
    }

    private static async Task PlayWithAfplayAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            // macOS標準の音声再生コマンドです。
            // Windows/Linux対応をする場合は、このクラスを差し替える想定です。
            FileName = "/usr/bin/afplay",
            ArgumentList = { filePath },
            UseShellExecute = false,
            CreateNoWindow = true
        });

        if (process is null)
        {
            throw new InvalidOperationException("Could not start afplay.");
        }

        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"afplay exited with code {process.ExitCode}.");
        }
    }

    private static void TryDelete(string filePath)
    {
        try
        {
            File.Delete(filePath);
        }
        catch
        {
            // 再生後の一時ファイル削除に失敗しても、アプリの処理自体は継続します。
        }
    }
}
