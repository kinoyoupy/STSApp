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

        // turn_id をファイル名に含めることで、DB・ログ・SignalR通知と追跡しやすくします。
        var relativePath = Path.Combine(
            _options.AudioRootPath,
            "input",
            DateTime.UtcNow.ToString("yyyyMMdd"),
            $"{turnId:N}{extension}");

        var absolutePath = Path.Combine(_environment.ContentRootPath, relativePath);
        var directory = Path.GetDirectoryName(absolutePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var output = File.Create(absolutePath);
        await audioFile.CopyToAsync(output, cancellationToken);

        return new StoredAudioFile(
            NormalizePath(relativePath),
            string.IsNullOrWhiteSpace(audioFile.ContentType) ? "application/octet-stream" : audioFile.ContentType,
            audioFile.Length);
    }

    public async Task<StoredAudioFile> SaveOutputAudioAsync(
        Guid turnId,
        Stream audioStream,
        string mimeType,
        string fileExtension,
        CancellationToken cancellationToken)
    {
        // TTSの返答音声は、入力音声と混ざらないようoutput側へ保存します。
        // DBにはこの戻り値のFilePathを登録し、音声本体はDBへ格納しません。
        var extension = NormalizeExtension(fileExtension, mimeType);

        // TTSで生成された返答音声は output 配下に保存します。
        // input/output を分けておくことで、後から音声ファイルの用途を追いやすくします。
        var relativePath = Path.Combine(
            _options.AudioRootPath,
            "output",
            DateTime.UtcNow.ToString("yyyyMMdd"),
            $"{turnId:N}{extension}");

        var absolutePath = Path.Combine(_environment.ContentRootPath, relativePath);
        var directory = Path.GetDirectoryName(absolutePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using (var output = File.Create(absolutePath))
        {
            await audioStream.CopyToAsync(output, cancellationToken);
        }

        var fileSizeBytes = new FileInfo(absolutePath).Length;

        return new StoredAudioFile(
            NormalizePath(relativePath),
            string.IsNullOrWhiteSpace(mimeType) ? "application/octet-stream" : mimeType,
            fileSizeBytes);
    }

    public Task<Stream?> OpenReadAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        // DBに保存しているfilePathは、BackendのContentRootPathから見た相対パスです。
        // 実際に返す時は絶対パスへ変換してファイルを開きます。
        var absolutePath = Path.GetFullPath(Path.Combine(_environment.ContentRootPath, filePath));
        var storageRootPath = Path.GetFullPath(Path.Combine(_environment.ContentRootPath, _options.AudioRootPath));

        // DBにはBackendが生成した相対パスを保存します。
        // 念のため、storage/audio の外を読みに行かないことも確認します。
        if (!absolutePath.StartsWith(storageRootPath, StringComparison.Ordinal))
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
}
