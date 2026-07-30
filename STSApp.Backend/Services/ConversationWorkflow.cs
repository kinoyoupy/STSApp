using Microsoft.AspNetCore.SignalR;
using STSApp.Backend.Hubs;
using STSApp.Backend.Repositories;
using STSApp.Backend.Services.External;
using STSApp.Backend.Services.Rag;
using STSApp.Backend.Services.Storage;
using STSApp.Contracts.Enums;
using STSApp.Contracts.Events;
using STSApp.Contracts.Models;

namespace STSApp.Backend.Services;

/// <summary>
/// 1回の音声入力に対する処理の流れをまとめます。
/// 音声アップロード、STT、RAG、Gemini、TTSの順番をここで制御します。
/// </summary>
public sealed class ConversationWorkflow : IConversationWorkflow
{
    private readonly IConversationRepository _repository;
    private readonly IAudioFileStorage _audioFileStorage;
    private readonly ISttClient _sttClient;
    private readonly IKnowledgeSearchService _knowledgeSearchService;
    private readonly IKnowledgeRepository _knowledgeRepository;
    private readonly IGeminiClient _geminiClient;
    private readonly ITtsClient _ttsClient;
    private readonly IHubContext<ConversationHub> _hubContext;
    private readonly ILogger<ConversationWorkflow> _logger;

    public ConversationWorkflow(
        IConversationRepository repository,
        IAudioFileStorage audioFileStorage,
        ISttClient sttClient,
        IKnowledgeSearchService knowledgeSearchService,
        IKnowledgeRepository knowledgeRepository,
        IGeminiClient geminiClient,
        ITtsClient ttsClient,
        IHubContext<ConversationHub> hubContext,
        ILogger<ConversationWorkflow> logger)
    {
        _repository = repository;
        _audioFileStorage = audioFileStorage;
        _sttClient = sttClient;
        _knowledgeSearchService = knowledgeSearchService;
        _knowledgeRepository = knowledgeRepository;
        _geminiClient = geminiClient;
        _ttsClient = ttsClient;
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task<ConversationTurnProcessingResult> ProcessAudioTurnAsync(
        Guid conversationId,
        IFormFile audioFile,
        CancellationToken cancellationToken)
    {
        // 1回の発話送信に対して、まず「処理中」の会話ターンを作ります。
        // 先にIDを作っておくことで、upload/STT/RAG/Gemini/TTSのイベントや音声ファイルを同じturnIdへ紐づけられます。
        var turn = await _repository.CreateProcessingTurnAsync(conversationId, cancellationToken);

        StoredAudioFile storedAudio;
        try
        {
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
            storedAudio = await _audioFileStorage.SaveInputAudioAsync(turn.Id, audioFile, cancellationToken);
            var durationMs = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds;

            // 音声ファイル本体はDBに入れず、storage/audio/input/... に保存します。
            // DBには「どこに保存したか」という参照情報だけを残します。
            try
            {
                await _repository.AddAudioFileAsync(
                    turn.Id,
                    AudioFileKind.Input,
                    storedAudio.FilePath,
                    storedAudio.MimeType,
                    storedAudio.FileSizeBytes,
                    cancellationToken);
            }
            catch
            {
                // DBから参照できない音声を残すと、履歴削除や保存期間管理の対象から漏れます。
                // DB登録だけが失敗した場合は、直前に作ったファイルを補償削除してから元の例外を返します。
                await TryDeleteUnregisteredAudioAsync(turn.Id, storedAudio.FilePath);
                throw;
            }

            await AddEventAndNotifyAsync(
                conversationId,
                turn.Id,
                ProcessingStage.Upload,
                TurnEventType.Completed,
                "音声アップロード処理が完了しました。",
                durationMs,
                cancellationToken);
        }
        catch (Exception ex)
        {
            // 録音保存で止まったターンをProcessingのまま残さないため、
            // STTへ進む前の失敗も記録します。
            // ただし、音声ファイルの保存ではなくDBへの参照情報保存で失敗した場合は、
            // Uploadと表示すると原因を誤るためDatabase段階として扱います。
            var failureStage = DatabaseFailureDetector.IsDatabaseFailure(ex)
                ? ProcessingStage.Database
                : ProcessingStage.Upload;
            var userMessage = failureStage == ProcessingStage.Database
                ? "会話データをDBへ保存できませんでした。"
                : "音声ファイルを保存できませんでした。";

            await RecordFailureWithIndependentTokenWhenStoppingAsync(
                conversationId,
                turn.Id,
                failureStage,
                userMessage,
                ex,
                cancellationToken);

            throw;
        }

        var currentStage = ProcessingStage.Stt;

        try
        {
            // STT: ユーザーの音声を文字に変換します。
            // ここが成功すると、AvaloniaのチャットUIにユーザー発話を表示できます。
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

            await TryNotifyAsync(
                conversationId,
                "transcriptionCompleted",
                new TranscriptionCompletedEvent(conversationId, turn.Id, userText),
                cancellationToken);

            currentStage = ProcessingStage.Rag;

            // RAG: ユーザー発話に近いVoiceLink資料を検索します。
            // 「0件」は資料に答えがないという正常な検索結果ですが、API・DB・ベクトルの問題は失敗として止めます。
            await AddEventAndNotifyAsync(
                conversationId,
                turn.Id,
                ProcessingStage.Rag,
                TurnEventType.Started,
                "関連資料の検索を開始しました。",
                null,
                cancellationToken);

            var ragStartedAt = DateTime.UtcNow;
            var ragResult = await _knowledgeSearchService.SearchAsync(userText, cancellationToken);
            var ragDurationMs = (int)(DateTime.UtcNow - ragStartedAt).TotalMilliseconds;

            // Geminiへ渡した資料と同じ内容を、ターンへスナップショットとして残します。
            // 資料が更新・削除されても、過去の回答時点の根拠を後から確認できるようにするためです。
            await _knowledgeRepository.AddTurnReferencesAsync(turn.Id, ragResult.References, cancellationToken);

            await AddEventAndNotifyAsync(
                conversationId,
                turn.Id,
                ProcessingStage.Rag,
                TurnEventType.Completed,
                "関連資料の検索が完了しました。",
                ragDurationMs,
                cancellationToken);

            currentStage = ProcessingStage.Gemini;

            // Gemini: STTで得たユーザー発話と直近履歴を使って、AI返答テキストを生成します。
            // 初期版ではストリーミングではなく、返答テキストが完成してから通知します。
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
                new GeminiReplyRequest(
                    userText,
                    recentTurns,
                    ragResult.AnswerBasis,
                    ragResult.References),
                cancellationToken);
            var geminiDurationMs = (int)(DateTime.UtcNow - geminiStartedAt).TotalMilliseconds;

            await _repository.UpdateAssistantTextAsync(
                turn.Id,
                assistantText,
                ragResult.AnswerBasis,
                cancellationToken);

            await AddEventAndNotifyAsync(
                conversationId,
                turn.Id,
                ProcessingStage.Gemini,
                TurnEventType.Completed,
                "Gemini応答生成が完了しました。",
                geminiDurationMs,
                cancellationToken);

            await TryNotifyAsync(
                conversationId,
                "assistantTextCompleted",
                new AssistantTextCompletedEvent(conversationId, turn.Id, assistantText, ragResult.AnswerBasis),
                cancellationToken);

            currentStage = ProcessingStage.Tts;

            // TTS: Geminiが作った返答テキストを音声に変換します。
            // テキストは先にUIへ出し、音声は生成が終わってからaudioIdを通知します。
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

                AudioFileDto outputAudio;
                try
                {
                    outputAudio = await _repository.AddAudioFileAsync(
                        turn.Id,
                        AudioFileKind.Output,
                        storedOutputAudio.FilePath,
                        storedOutputAudio.MimeType,
                        storedOutputAudio.FileSizeBytes,
                        cancellationToken);
                }
                catch
                {
                    // TTS音声も、DB登録前に失敗した時だけ補償削除します。
                    // 登録後に後続処理が失敗した音声は、失敗ターンの記録として残します。
                    await TryDeleteUnregisteredAudioAsync(turn.Id, storedOutputAudio.FilePath);
                    throw;
                }

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
                await TryNotifyAsync(
                    conversationId,
                    "speechSynthesisCompleted",
                    new SpeechSynthesisCompletedEvent(conversationId, turn.Id, outputAudio.Id),
                    cancellationToken);

                return new ConversationTurnProcessingResult(
                    turn.Id,
                    outputAudio.Id);
            }
        }
        catch (Exception ex)
        {
            // どの段階で失敗したかを currentStage に入れておくことで、
            // STT・RAG・Gemini・TTSのどこで失敗したかをDBとSignalRへ同じ意味で残せます。
            var failureStage = DatabaseFailureDetector.IsDatabaseFailure(ex)
                ? ProcessingStage.Database
                : currentStage;
            var userMessage = failureStage switch
            {
                ProcessingStage.Database => "会話データをDBへ保存できませんでした。",
                ProcessingStage.Tts => "返答音声を生成できませんでした。",
                ProcessingStage.Gemini => "AI返答を生成できませんでした。",
                ProcessingStage.Rag => "関連資料を検索できませんでした。",
                _ => "音声を文字に変換できませんでした。"
            };

            await RecordFailureWithIndependentTokenWhenStoppingAsync(
                conversationId,
                turn.Id,
                failureStage,
                userMessage,
                ex,
                cancellationToken);

            throw;
        }
    }

    private async Task TryDeleteUnregisteredAudioAsync(Guid turnId, string filePath)
    {
        try
        {
            // 元の処理がキャンセルされていても孤立ファイルは削除する必要があるため、
            // 補償削除には既にキャンセルされた処理トークンを渡しません。
            await _audioFileStorage.DeleteAsync(filePath, CancellationToken.None);
        }
        catch (Exception cleanupException)
        {
            // 補償削除の失敗で元のDB例外を上書きすると、利用者へ誤った原因を伝えてしまいます。
            // ファイルパスや音声内容は出さず、運用上必要な識別情報だけをログへ残します。
            _logger.LogWarning(
                "DB未登録音声の補償削除に失敗しました。TurnId={TurnId}, ErrorType={ErrorType}",
                turnId,
                cleanupException.GetType().Name);
        }
    }

    private async Task RecordFailureWithIndependentTokenWhenStoppingAsync(
        Guid conversationId,
        Guid turnId,
        ProcessingStage stage,
        string userMessage,
        Exception exception,
        CancellationToken processingCancellationToken)
    {
        if (!processingCancellationToken.IsCancellationRequested)
        {
            await RecordFailureAndNotifyAsync(
                conversationId,
                turnId,
                stage,
                userMessage,
                exception,
                processingCancellationToken);
            return;
        }

        // Backend停止によって本処理のトークンがキャンセルされても、同じトークンで失敗保存すると
        // SaveChangesも即座に中断され、ターンがprocessingのまま残ります。
        // 終了猶予の範囲で失敗状態だけを保存できるよう、短い独立トークンへ切り替えます。
        using var failureRecordingTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await RecordFailureAndNotifyAsync(
            conversationId,
            turnId,
            stage,
            userMessage,
            exception,
            failureRecordingTokenSource.Token);
    }

    private async Task RecordFailureAndNotifyAsync(
        Guid conversationId,
        Guid turnId,
        ProcessingStage stage,
        string userMessage,
        Exception exception,
        CancellationToken cancellationToken)
    {
        // 失敗時のDB更新、調査用イベント、画面通知を1か所へ集めます。
        // Uploadだけ別の例外処理を持っても、記録内容が後続ステージとずれないようにするためです。
        // 例外本文には、外部APIの応答やローカルファイル名などが含まれる可能性があります。
        // DBと通常ログには管理された情報だけを残し、利用者の発話や環境情報が意図せず残るのを防ぎます。
        _logger.LogError(
            "Conversation turn {TurnId} failed at stage {Stage}. ExceptionType={ExceptionType}.",
            turnId,
            stage,
            exception.GetType().Name);

        var notificationStage = stage;
        var notificationMessage = userMessage;

        try
        {
            await _repository.MarkTurnFailedAsync(
                turnId,
                stage,
                userMessage,
                cancellationToken);
        }
        catch (Exception databaseException)
        {
            // DB障害時は「DBへ失敗を書き込む」処理自体も失敗します。
            // 元の例外を隠さず両方をログへ残し、画面通知はDatabase段階へ切り替えます。
            _logger.LogError(
                "Could not persist failed status for conversation turn {TurnId}. ExceptionType={ExceptionType}.",
                turnId,
                databaseException.GetType().Name);
            notificationStage = ProcessingStage.Database;
            notificationMessage = "エラー情報をDBへ保存できませんでした。";
        }

        try
        {
            await _repository.AddTurnEventAsync(
                turnId,
                notificationStage,
                TurnEventType.Failed,
                notificationMessage,
                null,
                cancellationToken);
        }
        catch (Exception databaseException)
        {
            // turn_eventsへ保存できなくてもSignalR通知は止めません。
            // DBとリアルタイム通知を完全に一体化すると、DB停止時に画面へ何も届かなくなるためです。
            _logger.LogError(
                "Could not persist failure event for conversation turn {TurnId}. ExceptionType={ExceptionType}.",
                turnId,
                databaseException.GetType().Name);
            notificationStage = ProcessingStage.Database;
            notificationMessage = "エラー情報をDBへ保存できませんでした。";
        }

        try
        {
            await _hubContext.Clients
                .Group(ConversationHub.GetGroupName(conversationId))
                .SendAsync(
                    "turnFailed",
                    new TurnFailedEvent(
                        conversationId,
                        turnId,
                        notificationStage,
                        notificationMessage),
                    cancellationToken);
        }
        catch (Exception signalRException)
        {
            // SignalRが切れていてもBackend処理の元例外を置き換えないよう、ログだけ残します。
            // Desktopは後からREST履歴を取得してDB上の状態を確認できます。
            _logger.LogError(
                "Could not notify failure for conversation turn {TurnId}. ExceptionType={ExceptionType}.",
                turnId,
                signalRException.GetType().Name);
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
        // DB保存とSignalR通知を必ずセットにしたいので、共通メソッドにしています。
        // DBのturn_eventsは後から履歴・調査に使い、SignalRは今開いている画面の即時更新に使います。
        await _repository.AddTurnEventAsync(
            turnId,
            stage,
            eventType,
            message,
            durationMs,
            cancellationToken);

        await TryNotifyAsync(
            conversationId,
            "turnStatusChanged",
            new TurnStatusChangedEvent(conversationId, turnId, stage, eventType, message),
            cancellationToken);
    }

    private async Task TryNotifyAsync<TMessage>(
        Guid conversationId,
        string methodName,
        TMessage message,
        CancellationToken cancellationToken)
    {
        try
        {
            // SignalRは現在開いている画面への通知であり、会話処理そのものではありません。
            // 通知に失敗してもDBには結果が残るため、STT・Gemini・TTSを失敗扱いにしません。
            await _hubContext.Clients
                .Group(ConversationHub.GetGroupName(conversationId))
                .SendAsync(methodName, message, cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Could not send SignalR method {MethodName} for conversation {ConversationId}. ExceptionType={ExceptionType}.",
                methodName,
                conversationId,
                exception.GetType().Name);
        }
    }
}
