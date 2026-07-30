using Microsoft.Extensions.Options;
using STSApp.Backend.Options;

namespace STSApp.Backend.Services.Storage;

/// <summary>
/// ローカルファイルシステムへ音声ファイルを保存する実装です。
/// 初期版ではDockerやクラウドストレージではなく、Backend配下の storage/audio を使います。
/// </summary>
public sealed class LocalAudioFileStorage : IAudioFileStorage
{
    private readonly StorageOptions _options;
    private readonly IWebHostEnvironment _environment;

    public LocalAudioFileStorage(
        IOptions<StorageOptions> options,
        IWebHostEnvironment environment)
    {
        _options = options.Value;
        _environment = environment;
    }

    public async Task<StoredAudioFile> SaveInputAudioAsync(
        Guid turnId,
        IFormFile audioFile,
        CancellationToken cancellationToken)
    {
        // Avaloniaから送られてきたユーザー発話音声を保存します。
        // ファイル名は元の名前ではなくturnIdを使い、DB上のターンと追跡しやすくします。
        var extension = Path.GetExtension(audioFile.FileName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = GuessExtension(audioFile.ContentType);
        }

        // turn_idで追跡しつつ、保存ごとのUUIDも付けます。
        // 同じターンへ複数の入力音声が来ても、前のファイルを上書きしないためです。
        var relativePath = Path.Combine(
            _options.AudioRootPath,
            "input",
            DateTime.UtcNow.ToString("yyyyMMdd"),
            BuildUniqueFileName(turnId, extension));

        var fileSizeBytes = await SaveAtomicallyAsync(
            relativePath,
            output => audioFile.CopyToAsync(output, cancellationToken));

        return new StoredAudioFile(
            NormalizePath(relativePath),
            string.IsNullOrWhiteSpace(audioFile.ContentType) ? "application/octet-stream" : audioFile.ContentType,
            fileSizeBytes);
    }

    public async Task<StoredAudioFile> SaveOutputAudioAsync(
        Guid turnId,
        Stream audioStream,
        string mimeType,
        string fileExtension,
        CancellationToken cancellationToken)
    {
        var extension = NormalizeExtension(fileExtension, mimeType);

        // TTSで生成された返答音声は output 配下に保存します。
        // 再生成などで同じターンに複数の出力ができても、保存ごとのUUIDで別ファイルにします。
        var relativePath = Path.Combine(
            _options.AudioRootPath,
            "output",
            DateTime.UtcNow.ToString("yyyyMMdd"),
            BuildUniqueFileName(turnId, extension));

        var fileSizeBytes = await SaveAtomicallyAsync(
            relativePath,
            output => audioStream.CopyToAsync(output, cancellationToken));

        return new StoredAudioFile(
            NormalizePath(relativePath),
            string.IsNullOrWhiteSpace(mimeType) ? "application/octet-stream" : mimeType,
            fileSizeBytes);
    }

    public Task DeleteAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var absolutePath = GetValidatedStoragePath(filePath);
        if (absolutePath is not null && File.Exists(absolutePath))
        {
            File.Delete(absolutePath);
        }

        return Task.CompletedTask;
    }

    public Task<Stream?> OpenReadAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        // DBに保存しているfilePathは、BackendのContentRootPathから見た相対パスです。
        // 実際に返す時は絶対パスへ変換してファイルを開きます。
        var absolutePath = GetValidatedStoragePath(filePath);
        if (absolutePath is null)
        {
            return Task.FromResult<Stream?>(null);
        }

        if (!File.Exists(absolutePath))
        {
            return Task.FromResult<Stream?>(null);
        }

        Stream stream = File.OpenRead(absolutePath);
        return Task.FromResult<Stream?>(stream);
    }

    private async Task<long> SaveAtomicallyAsync(
        string relativePath,
        Func<Stream, Task> writeAsync)
    {
        var absolutePath = Path.GetFullPath(Path.Combine(_environment.ContentRootPath, relativePath));
        var directory = Path.GetDirectoryName(absolutePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // 完成ファイルへ直接書くと、中断時に壊れたファイルが正式な名前で残ります。
        // 同じフォルダの一時ファイルへ書き切ってから移動し、完成した音声だけを公開します。
        var temporaryPath = $"{absolutePath}.tmp-{Guid.NewGuid():N}";
        try
        {
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true))
            {
                await writeAsync(output);
                await output.FlushAsync();
            }

            File.Move(temporaryPath, absolutePath);
            return new FileInfo(absolutePath).Length;
        }
        catch
        {
            // 一時ファイル削除まで失敗しても、本来の書き込み・移動エラーを上書きしません。
            // 呼び出し側が「保存に失敗した」という最初の原因を正しく扱えることを優先します。
            TryDeleteTemporaryFile(temporaryPath);
            throw;
        }
    }

    private static void TryDeleteTemporaryFile(string temporaryPath)
    {
        try
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
        catch
        {
            // ここで例外を投げると、保存に失敗した本来の原因が後片付けエラーへ置き換わります。
            // プロセス異常終了も含む残存一時ファイルの定期回収は、将来の運用機能として分けて扱います。
        }
    }

    private string? GetValidatedStoragePath(string filePath)
    {
        var absolutePath = Path.GetFullPath(Path.Combine(_environment.ContentRootPath, filePath));
        var storageRootPath = Path.GetFullPath(Path.Combine(_environment.ContentRootPath, _options.AudioRootPath));

        // DBにはBackendが生成した相対パスを保存します。
        // 読み取りと削除のどちらでも、storage/audio の外へ出ないことを同じ規則で確認します。
        var relativePathFromStorageRoot = Path.GetRelativePath(storageRootPath, absolutePath);

        // 文字列のStartsWithだけでは、storage/audio-oldのような「名前が似た別フォルダ」も
        // storage/audio配下だと誤認する可能性があります。相対パスに戻し、親へ出ていないかで判定します。
        if (Path.IsPathRooted(relativePathFromStorageRoot)
            || relativePathFromStorageRoot == ".."
            || relativePathFromStorageRoot.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
        {
            return null;
        }

        return absolutePath;
    }

    private static string GuessExtension(string? contentType)
    {
        return contentType?.ToLowerInvariant() switch
        {
            "audio/wav" or "audio/x-wav" => ".wav",
            "audio/mpeg" => ".mp3",
            "audio/mp4" => ".m4a",
            "audio/webm" => ".webm",
            _ => ".bin"
        };
    }

    private static string NormalizeExtension(string fileExtension, string? contentType)
    {
        if (!string.IsNullOrWhiteSpace(fileExtension))
        {
            return fileExtension.StartsWith('.') ? fileExtension : "." + fileExtension;
        }

        return GuessExtension(contentType);
    }

    private static string NormalizePath(string path)
    {
        return path.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string BuildUniqueFileName(Guid turnId, string extension)
    {
        return $"{turnId:N}-{Guid.NewGuid():N}{extension}";
    }
}
