using Microsoft.AspNetCore.Mvc;
using STSApp.Backend.Repositories;
using STSApp.Backend.Services.Storage;

namespace STSApp.Backend.Controllers;

/// <summary>
/// 保存済み音声ファイルを取得するためのAPIです。
/// audio_files テーブルに保存された参照情報を使って、実ファイルを返します。
/// </summary>
[ApiController]
[Route("api/audio")]
public sealed class AudioController : ControllerBase
{
    private readonly IConversationRepository _repository;
    private readonly IAudioFileStorage _audioFileStorage;

    public AudioController(
        IConversationRepository repository,
        IAudioFileStorage audioFileStorage)
    {
        _repository = repository;
        _audioFileStorage = audioFileStorage;
    }

    [HttpGet("{audioId:guid}")]
    public async Task<IActionResult> GetAudio(
        Guid audioId,
        CancellationToken cancellationToken)
    {
        var audioFile = await _repository.GetAudioFileAsync(audioId, cancellationToken);
        if (audioFile is null)
        {
            return NotFound();
        }

        var stream = await _audioFileStorage.OpenReadAsync(audioFile.FilePath, cancellationToken);
        if (stream is null)
        {
            return NotFound();
        }

        // Avalonia側で再生しやすいよう、DBに保存しているMIMEタイプで返します。
        // enableRangeProcessing は、音声再生で途中読み込みが必要になった時のために有効にします。
        return File(stream, audioFile.MimeType, enableRangeProcessing: true);
    }
}
