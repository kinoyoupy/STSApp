using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using STSApp.Backend.Hubs;
using STSApp.Backend.Options;
using STSApp.Backend.Services;
using STSApp.Backend.Services.Storage;
using STSApp.Contracts.Requests;

namespace STSApp.Backend.Tests;

/// <summary>
/// セルフレビューで見つかった境界条件を、今後うっかり元へ戻さないためのテストです。
/// </summary>
public sealed class ReviewGuardTests : IDisposable
{
    private readonly string _contentRootPath =
        Path.Combine(Path.GetTempPath(), $"sts-review-test-{Guid.NewGuid():N}");

    [Fact]
    public void Create_conversation_request_rejects_title_longer_than_database_limit()
    {
        var request = new CreateConversationRequest { Title = new string('a', 256) };
        var results = new List<ValidationResult>();

        var isValid = Validator.TryValidateObject(
            request,
            new ValidationContext(request),
            results,
            validateAllProperties: true);

        Assert.False(isValid);
        Assert.Contains(results, result => result.MemberNames.Contains(nameof(CreateConversationRequest.Title)));
    }

    [Fact]
    public void SignalR_group_name_is_stable_and_separate_for_each_conversation()
    {
        var firstConversationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var secondConversationId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        Assert.Equal(
            "conversation:11111111111111111111111111111111",
            ConversationHub.GetGroupName(firstConversationId));
        Assert.NotEqual(
            ConversationHub.GetGroupName(firstConversationId),
            ConversationHub.GetGroupName(secondConversationId));
    }

    [Fact]
    public async Task Audio_storage_does_not_open_similarly_named_sibling_directory()
    {
        var siblingDirectory = Path.Combine(_contentRootPath, "storage", "audio-old");
        Directory.CreateDirectory(siblingDirectory);
        await File.WriteAllTextAsync(Path.Combine(siblingDirectory, "outside.wav"), "not audio");

        var storage = new LocalAudioFileStorage(
            Microsoft.Extensions.Options.Options.Create(
                new StorageOptions { AudioRootPath = "storage/audio" }),
            new TestWebHostEnvironment(_contentRootPath));

        var stream = await storage.OpenReadAsync(
            "storage/audio-old/outside.wav",
            CancellationToken.None);

        Assert.Null(stream);
    }

    [Fact]
    public async Task Audio_storage_creates_different_paths_for_the_same_turn()
    {
        var storage = new LocalAudioFileStorage(
            Microsoft.Extensions.Options.Options.Create(
                new StorageOptions { AudioRootPath = "storage/audio" }),
            new TestWebHostEnvironment(_contentRootPath));
        var turnId = Guid.NewGuid();

        // 同じターンに同種の音声が複数来ても、後の保存で前の音声を上書きしてはいけません。
        // 設計上の「1ターンに複数音声」を、実ファイル名でも維持できることを確認します。
        var first = await storage.SaveInputAudioAsync(
            turnId,
            CreateAudioFile("first"),
            CancellationToken.None);
        var second = await storage.SaveInputAudioAsync(
            turnId,
            CreateAudioFile("second"),
            CancellationToken.None);

        Assert.NotEqual(first.FilePath, second.FilePath);
        Assert.True(File.Exists(Path.Combine(_contentRootPath, first.FilePath)));
        Assert.True(File.Exists(Path.Combine(_contentRootPath, second.FilePath)));
    }

    [Fact]
    public async Task Audio_storage_removes_temporary_file_when_writing_fails()
    {
        var storage = new LocalAudioFileStorage(
            Microsoft.Extensions.Options.Options.Create(
                new StorageOptions { AudioRootPath = "storage/audio" }),
            new TestWebHostEnvironment(_contentRootPath));

        await Assert.ThrowsAsync<IOException>(() =>
            storage.SaveOutputAudioAsync(
                Guid.NewGuid(),
                new ThrowingReadStream(),
                "audio/wav",
                ".wav",
                CancellationToken.None));

        var audioRoot = Path.Combine(_contentRootPath, "storage", "audio");
        Assert.Empty(Directory.Exists(audioRoot)
            ? Directory.EnumerateFiles(audioRoot, "*", SearchOption.AllDirectories)
            : []);
    }

    [Fact]
    public async Task Audio_storage_delete_only_removes_file_inside_audio_root()
    {
        var storage = new LocalAudioFileStorage(
            Microsoft.Extensions.Options.Options.Create(
                new StorageOptions { AudioRootPath = "storage/audio" }),
            new TestWebHostEnvironment(_contentRootPath));
        var stored = await storage.SaveInputAudioAsync(
            Guid.NewGuid(),
            CreateAudioFile("voice"),
            CancellationToken.None);
        var outsidePath = Path.Combine(_contentRootPath, "outside.wav");
        await File.WriteAllTextAsync(outsidePath, "keep");

        await storage.DeleteAsync(stored.FilePath, CancellationToken.None);
        await storage.DeleteAsync("../outside.wav", CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(_contentRootPath, stored.FilePath)));
        Assert.True(File.Exists(outsidePath));
    }

    [Fact]
    public void Database_failure_detector_finds_wrapped_database_exception()
    {
        // 実際のDB例外は、処理内容を示す別の例外で包まれることがあります。
        // 内側まで確認できないとDB障害をSTTやGemini障害として誤表示するため、
        // 包まれたDbUpdateExceptionも検出できることを固定します。
        var exception = new InvalidOperationException(
            "会話保存処理に失敗しました。",
            new DbUpdateException("INSERTに失敗しました。"));

        Assert.True(DatabaseFailureDetector.IsDatabaseFailure(exception));
    }

    [Fact]
    public void Database_failure_detector_does_not_treat_normal_processing_error_as_database_error()
    {
        // あらゆる例外をDB障害にすると、利用者とログへ誤った原因を伝えてしまいます。
        // DB由来ではない通常の処理例外はfalseになることも同時に確認します。
        var exception = new InvalidOperationException("外部APIの応答を解析できませんでした。");

        Assert.False(DatabaseFailureDetector.IsDatabaseFailure(exception));
    }

    public void Dispose()
    {
        if (Directory.Exists(_contentRootPath))
        {
            Directory.Delete(_contentRootPath, recursive: true);
        }
    }

    private static IFormFile CreateAudioFile(string content)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "audioFile", "recording.wav")
        {
            Headers = new HeaderDictionary(),
            ContentType = "audio/wav"
        };
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public TestWebHostEnvironment(string contentRootPath)
        {
            ContentRootPath = contentRootPath;
            ContentRootFileProvider = new NullFileProvider();
            WebRootPath = contentRootPath;
            WebRootFileProvider = new NullFileProvider();
        }

        public string ApplicationName { get; set; } = "STSApp.Backend.Tests";
        public IFileProvider WebRootFileProvider { get; set; }
        public string WebRootPath { get; set; }
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }
    }

    private sealed class ThrowingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("Read failed.");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
