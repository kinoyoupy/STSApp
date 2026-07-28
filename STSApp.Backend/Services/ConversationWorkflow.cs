using Microsoft.AspNetCore.SignalR;
using STSApp.Backend.Hubs;
using STSApp.Backend.Repositories;
using STSApp.Backend.Services.External;
using STSApp.Backend.Services.Storage;
using STSApp.Contracts.Enums;
using STSApp.Contracts.Events;
using STSApp.Contracts.Models;

namespace STSApp.Backend.Services;

/// <summary>
/// 1回の音声入力に対する処理の流れをまとめます。
/// 音声アップロード、STT、Gemini、TTSの順番をここで制御します。
///
/// 各処理をここに集める理由は、音声対話の順番と失敗箇所を1か所で管理するためです。
/// STTやGeminiの内部処理までここへ書かず、担当クラスへ分けることで、
/// APIを差し替えても全体の流れを読み直さずに済みます。
/// </summary>
public sealed class ConversationWorkflow : IConversationWorkflow
{
    private readonly IConversationRepository _repository;
    private readonly IAudioFileStorage _audioFileStorage;
    private readonly ISttClient _sttClient;
    private readonly IGeminiClient _geminiClient;
    private readonly ITtsClient _ttsClient;
    private readonly IHubContext<ConversationHub> _hubContext;

    public ConversationWorkflow(
        IConversationRepository repository,
        IAudioFileStorage audioFileStorage,
        ISttClient sttClient,
        IGeminiClient geminiClient,
        ITtsClient ttsClient,
        IHubContext<ConversationHub> hubContext)
    {
        _repository = repository;
        _audioFileStorage = audioFileStorage;
        _sttClient = sttClient;
        _geminiClient = geminiClient;
        _ttsClient = ttsClient;
        _hubContext = hubContext;
    }

    public async Task<ConversationTurnDto> ProcessAudioTurnAsync(
        Guid conversationId,
        IFormFile audioFile,
        CancellationToken cancellationToken)
    {
        // 1回のPushToTalk送信に対して、まず「処理中」の会話ターンを作ります。
        // 先にIDを作っておくことで、upload/STT/Gemini/TTSのイベントや音声ファイルを同じturnIdへ紐づけられます。
        var turn = await _repository.CreateProcessingTurnAsync(conversationId, cancellationToken);

        // 以降の AddEventAndNotifyAsync は、
        // 1. turn_events へ処理履歴を保存する
        // 2. SignalRでAvaloniaへリアルタイム通知する
        // という2つを同時に行います。
        await AddEventAndNotifyAsync(
            conversationId,
            turn.Id,
            ProcessingStage.Upload,
            TurnEventType.Started,
            "音声アップロード処理を開始しました。",
            null,
            cancellationToken);

        var startedAt = DateTime.UtcNow;
        var storedAudio = await _audioFileStorage.SaveInputAudioAsync(turn.Id, audioFile, cancellationToken);
        var durationMs = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds;

        // 音声ファイル本体はDBに入れず、storage/audio/input/... に保存します。
        // DBには「どこに保存したか」という参照情報だけを残します。
        await _repository.AddAudioFileAsync(
            turn.Id,
            AudioFileKind.Input,
            storedAudio.FilePath,
            storedAudio.MimeType,
            storedAudio.FileSizeBytes,
            cancellationToken);

        await AddEventAndNotifyAsync(
            conversationId,
            turn.Id,
            ProcessingStage.Upload,
            TurnEventType.Completed,
            "音声アップロード処理が完了しました。",
            durationMs,
            cancellationToken);

        var currentStage = ProcessingStage.Stt;

        try
        {
            // STTを最初に呼ぶ理由は、Geminiが音声ファイルではなく文字を受け取る設計だからです。
            // ここで音声を文字へ変え、以降の処理で使えるユーザー発話を作ります。
            await AddEventAndNotifyAsync(
                conversationId,
                turn.Id,
                ProcessingStage.Stt,
                TurnEventType.Started,
                "STT処理を開始しました。",
                null,
                cancellationToken);

            await using var inputAudioStream = audioFile.OpenReadStream();
            var sttStartedAt = DateTime.UtcNow;
            var userText = await _sttClient.TranscribeAsync(
                inputAudioStream,
                audioFile.FileName,
                storedAudio.MimeType,
                cancellationToken);
            var sttDurationMs = (int)(DateTime.UtcNow - sttStartedAt).TotalMilliseconds;

            await _repository.UpdateUserTextAsync(turn.Id, userText, cancellationToken);

            await AddEventAndNotifyAsync(
                conversationId,
                turn.Id,
                ProcessingStage.Stt,
                TurnEventType.Completed,
                "STT処理が完了しました。",
                sttDurationMs,
                cancellationToken);

            await _hubContext.Clients.All.SendAsync(
                "transcriptionCompleted",
                new TranscriptionCompletedEvent(conversationId, turn.Id, userText),
                cancellationToken);

            currentStage = ProcessingStage.Gemini;

            // Geminiへ音声ではなく文字を渡す理由は、今回のGeminiの役割を返答文の生成に限定するためです。
            // 直近履歴も渡すのは、現在の発話だけでは会話の流れを判断できない場合があるためです。
            // 文章が完成してから通知するのは、初期版で途中の文章を扱う処理を増やさないためです。
            await AddEventAndNotifyAsync(
                conversationId,
                turn.Id,
                ProcessingStage.Gemini,
                TurnEventType.Started,
                "Gemini応答生成を開始しました。",
                null,
                cancellationToken);

            var recentTurns = await _repository.ListRecentCompletedTurnsAsync(
                conversationId,
                turn.Id,
                maxTurns: 6,
                cancellationToken);

            var geminiStartedAt = DateTime.UtcNow;
            var assistantText = await _geminiClient.GenerateReplyAsync(
                userText,
                recentTurns,
                cancellationToken);
            var geminiDurationMs = (int)(DateTime.UtcNow - geminiStartedAt).TotalMilliseconds;

            await _repository.UpdateAssistantTextAsync(turn.Id, assistantText, cancellationToken);

            await AddEventAndNotifyAsync(
                conversationId,
                turn.Id,
                ProcessingStage.Gemini,
                TurnEventType.Completed,
                "Gemini応答生成が完了しました。",
                geminiDurationMs,
                cancellationToken);

            await _hubContext.Clients.All.SendAsync(
                "assistantTextCompleted",
                new AssistantTextCompletedEvent(conversationId, turn.Id, assistantText),
                cancellationToken);

            currentStage = ProcessingStage.Tts;

            // TTSを最後に呼ぶ理由は、まずGeminiの返答文を確定させる必要があるためです。
            // 返答文を先に画面へ表示するのは、音声生成を待つ間もAIの結果を確認できるようにするためです。
            await AddEventAndNotifyAsync(
                conversationId,
                turn.Id,
                ProcessingStage.Tts,
                TurnEventType.Started,
                "TTS音声生成を開始しました。",
                null,
                cancellationToken);

            var ttsStartedAt = DateTime.UtcNow;
            var generatedSpeech = await _ttsClient.SynthesizeAsync(
                assistantText,
                cancellationToken);

            await using (generatedSpeech.AudioStream)
            {
                // 返答音声も入力音声と同じく、実ファイルはstorage/audio/output/...へ保存し、
                // DBには参照情報を保存します。
                var storedOutputAudio = await _audioFileStorage.SaveOutputAudioAsync(
                    turn.Id,
                    generatedSpeech.AudioStream,
                    generatedSpeech.MimeType,
                    generatedSpeech.FileExtension,
                    cancellationToken);

                var outputAudio = await _repository.AddAudioFileAsync(
                    turn.Id,
                    AudioFileKind.Output,
                    storedOutputAudio.FilePath,
                    storedOutputAudio.MimeType,
                    storedOutputAudio.FileSizeBytes,
                    cancellationToken);

                var ttsDurationMs = (int)(DateTime.UtcNow - ttsStartedAt).TotalMilliseconds;

                await AddEventAndNotifyAsync(
                    conversationId,
                    turn.Id,
                    ProcessingStage.Tts,
                    TurnEventType.Completed,
                    "TTS音声生成が完了しました。",
                    ttsDurationMs,
                    cancellationToken);

                await _repository.MarkTurnCompletedAsync(turn.Id, cancellationToken);

                // AvaloniaはこのaudioIdを使って GET /api/audio/{audioId} を呼び、音声を取得・再生します。
                await _hubContext.Clients.All.SendAsync(
                    "speechSynthesisCompleted",
                    new SpeechSynthesisCompletedEvent(conversationId, turn.Id, outputAudio.Id),
                    cancellationToken);
            }

            return turn with
            {
                UserText = userText,
                AssistantText = assistantText,
                Status = TurnStatus.Completed
            };
        }
        catch (Exception ex)
        {
            // 失敗した段階を記録する理由は、「音声・文字起こし・AI・音声合成」のどこに問題があるかを区別するためです。
            // 同じ情報をDBと画面通知へ残すことで、今すぐの案内と後からの調査の両方に使えます。
            var userMessage = currentStage switch
            {
                ProcessingStage.Tts => "返答音声を生成できませんでした。",
                ProcessingStage.Gemini => "AI返答を生成できませんでした。",
                _ => "音声を文字に変換できませんでした。"
            };

            await _repository.MarkTurnFailedAsync(
                turn.Id,
                currentStage,
                userMessage,
                cancellationToken);

            await AddEventAndNotifyAsync(
                conversationId,
                turn.Id,
                currentStage,
                TurnEventType.Failed,
                ex.Message,
                null,
                cancellationToken);

            await _hubContext.Clients.All.SendAsync(
                "turnFailed",
                new TurnFailedEvent(
                    conversationId,
                    turn.Id,
                    currentStage,
                    userMessage),
                cancellationToken);

            throw;
        }
    }

    private async Task AddEventAndNotifyAsync(
        Guid conversationId,
        Guid turnId,
        ProcessingStage stage,
        TurnEventType eventType,
        string? message,
        int? durationMs,
        CancellationToken cancellationToken)
    {
        // この処理を共通化する理由は、状態をDBへ保存したのに画面へ通知し忘れる不一致を防ぐためです。
        // DBは後から確認するため、SignalRは今見ている画面へ知らせるために使い分けます。
        await _repository.AddTurnEventAsync(
            turnId,
            stage,
            eventType,
            message,
            durationMs,
            cancellationToken);

        await _hubContext.Clients.All.SendAsync(
            "turnStatusChanged",
            new TurnStatusChangedEvent(conversationId, turnId, stage, eventType, message),
            cancellationToken);
    }
}
