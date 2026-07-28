#import "MacAudioRecorder.h"

#import <AVFoundation/AVFoundation.h>
#import <Foundation/Foundation.h>

// WebRTC VADへ渡す1回ぶんの音声データです。
// 16,000Hzでは、20ms = 320サンプルになります。
static const int kVadFrameSampleCount = 320;
// 話し始めを検知するまでに到着した直前400msぶんだけは保持します。
// VADは数フレーム続いてから発話開始と決めるため、この余白がないと最初の音が欠けるためです。
static const NSUInteger kPreRollByteCount = 16000 * 2 * 400 / 1000;

struct STSContinuousAudioRecorder {
    AVAudioEngine *engine;
    AVAudioConverter *converter;
    AVAudioFormat *targetFormat;
    AVAudioFile *outputFile;
    NSString *filePath;
    NSMutableData *pendingPcmData;
    NSMutableData *preRollPcmData;
    NSObject *syncLock;
    NSString *lastError;
    STSAudioFrameCallback frameCallback;
    void *callbackContext;
    int sampleRate;
    BOOL running;
    BOOL savingAudio;
};

static void set_error(char *destination, int capacity, NSString *message) {
    if (destination == NULL || capacity <= 0) {
        return;
    }

    const char *utf8 = message.UTF8String;
    if (utf8 == NULL) {
        destination[0] = '\0';
        return;
    }

    snprintf(destination, (size_t)capacity, "%s", utf8);
}

static void remember_error(STSContinuousAudioRecorder *recorder, NSString *message) {
    // 音声処理中に最初に起きたエラーを残します。
    // 後続のエラーで上書きすると、最初の原因を追えなくなるためです。
    if (recorder != NULL && recorder->lastError == nil) {
        recorder->lastError = [message copy];
    }
}

static void publish_complete_vad_frames(
    STSContinuousAudioRecorder *recorder,
    const int16_t *samples,
    int sampleCount) {
    if (recorder == NULL || samples == NULL || sampleCount <= 0) {
        return;
    }

    [recorder->pendingPcmData appendBytes:samples
                                   length:(NSUInteger)sampleCount * sizeof(int16_t)];

    const NSUInteger frameByteCount = kVadFrameSampleCount * sizeof(int16_t);
    while (recorder->pendingPcmData.length >= frameByteCount) {
        // callbackへ渡す領域はpendingPcmDataが所有しています。
        // callbackが戻った直後に先頭を取り除くため、呼び出し側は必要ならその場でコピーします。
        if (recorder->frameCallback != NULL) {
            recorder->frameCallback(
                (const int16_t *)recorder->pendingPcmData.bytes,
                kVadFrameSampleCount,
                recorder->callbackContext);
        }

        [recorder->pendingPcmData replaceBytesInRange:NSMakeRange(0, frameByteCount)
                                           withBytes:NULL
                                              length:0];
    }
}

static BOOL write_pcm_data(
    STSContinuousAudioRecorder *recorder,
    const void *bytes,
    NSUInteger byteCount,
    NSError **error) {
    if (recorder == NULL || recorder->outputFile == nil || recorder->targetFormat == nil) {
        return NO;
    }

    const NSUInteger bytesPerSample = sizeof(int16_t);
    if (bytes == NULL || byteCount == 0 || byteCount % bytesPerSample != 0) {
        return YES;
    }

    const AVAudioFrameCount frameCount = (AVAudioFrameCount)(byteCount / bytesPerSample);
    AVAudioPCMBuffer *buffer = [[AVAudioPCMBuffer alloc]
        initWithPCMFormat:recorder->targetFormat
             frameCapacity:frameCount];
    if (buffer == nil || buffer.audioBufferList->mNumberBuffers == 0) {
        return NO;
    }

    buffer.frameLength = frameCount;
    memcpy(buffer.audioBufferList->mBuffers[0].mData, bytes, byteCount);
    return [recorder->outputFile writeFromBuffer:buffer error:error];
}

static void append_pre_roll_audio(
    STSContinuousAudioRecorder *recorder,
    const void *bytes,
    NSUInteger byteCount) {
    [recorder->preRollPcmData appendBytes:bytes length:byteCount];

    if (recorder->preRollPcmData.length <= kPreRollByteCount) {
        return;
    }

    const NSUInteger excessLength = recorder->preRollPcmData.length - kPreRollByteCount;
    [recorder->preRollPcmData replaceBytesInRange:NSMakeRange(0, excessLength)
                                       withBytes:NULL
                                          length:0];
}

static void process_input_buffer(
    STSContinuousAudioRecorder *recorder,
    AVAudioPCMBuffer *inputBuffer) {
    if (recorder == NULL || !recorder->running || recorder->converter == nil) {
        return;
    }

    AVAudioFormat *inputFormat = inputBuffer.format;
    if (inputFormat.sampleRate <= 0 || inputBuffer.frameLength == 0) {
        return;
    }

    // 入力デバイスの44.1kHzや48kHzを、VADとSTTで共通に扱える16kHzへ変換します。
    // 少し余裕を持たせた容量にすることで、変換後のサンプルが途中で切れるのを防ぎます。
    const double rateRatio = recorder->targetFormat.sampleRate / inputFormat.sampleRate;
    const AVAudioFrameCount outputCapacity =
        (AVAudioFrameCount)((double)inputBuffer.frameLength * rateRatio) + 32;
    AVAudioPCMBuffer *outputBuffer = [[AVAudioPCMBuffer alloc]
        initWithPCMFormat:recorder->targetFormat
             frameCapacity:outputCapacity];
    if (outputBuffer == nil) {
        remember_error(recorder, @"VAD用の音声変換バッファを作成できませんでした。");
        return;
    }

    __block BOOL suppliedInput = NO;
    NSError *conversionError = nil;
    AVAudioConverterOutputStatus conversionStatus =
        [recorder->converter convertToBuffer:outputBuffer
                                        error:&conversionError
                           withInputFromBlock:^AVAudioBuffer * _Nullable(
                               AVAudioPacketCount inNumberOfPackets,
                               AVAudioConverterInputStatus *outStatus) {
            if (suppliedInput) {
                *outStatus = AVAudioConverterInputStatus_NoDataNow;
                return nil;
            }

            suppliedInput = YES;
            *outStatus = AVAudioConverterInputStatus_HaveData;
            return inputBuffer;
        }];

    if (conversionStatus == AVAudioConverterOutputStatus_Error || conversionError != nil) {
        remember_error(
            recorder,
            conversionError.localizedDescription ?: @"マイク音声を16kHzへ変換できませんでした。");
        return;
    }

    if (outputBuffer.frameLength == 0) {
        return;
    }

    // targetFormatは16bit・モノラル・インターリーブ形式です。
    // そのため最初の音声バッファに、連続したint16のサンプル列が入ります。
    const AudioBufferList *audioBufferList = outputBuffer.audioBufferList;
    if (audioBufferList->mNumberBuffers == 0 || audioBufferList->mBuffers[0].mData == NULL) {
        remember_error(recorder, @"変換済み音声のPCMデータを取得できませんでした。");
        return;
    }

    const int sampleCount = (int)(audioBufferList->mBuffers[0].mDataByteSize / sizeof(int16_t));

    // VAD判定用の音声は待機中も必要です。一方、WAVは発話開始を検知した後だけ保存します。
    // これにより、ユーザーが話しかけるまでの長い無音をBackendへ送らずに済みます。
    @synchronized (recorder->syncLock) {
        if (recorder->savingAudio) {
            NSError *writeError = nil;
            if (![recorder->outputFile writeFromBuffer:outputBuffer error:&writeError]) {
                remember_error(
                    recorder,
                    writeError.localizedDescription ?: @"変換済み音声をWAVへ保存できませんでした。");
            }
        } else {
            append_pre_roll_audio(
                recorder,
                audioBufferList->mBuffers[0].mData,
                audioBufferList->mBuffers[0].mDataByteSize);
        }
    }

    publish_complete_vad_frames(
        recorder,
        (const int16_t *)audioBufferList->mBuffers[0].mData,
        sampleCount);
}

STSContinuousAudioRecorder *sts_continuous_audio_recorder_create(
    int sample_rate,
    STSAudioFrameCallback frame_callback,
    void *callback_context) {
    if (sample_rate <= 0) {
        return NULL;
    }

    STSContinuousAudioRecorder *recorder = calloc(1, sizeof(STSContinuousAudioRecorder));
    if (recorder == NULL) {
        return NULL;
    }

    recorder->sampleRate = sample_rate;
    recorder->frameCallback = frame_callback;
    recorder->callbackContext = callback_context;
    recorder->syncLock = [[NSObject alloc] init];
    recorder->filePath = [[NSTemporaryDirectory()
        stringByAppendingPathComponent:[NSString stringWithFormat:@"stsapp-continuous-recording-%@.wav", NSUUID.UUID.UUIDString]] copy];

    return recorder;
}

int sts_continuous_audio_recorder_start(
    STSContinuousAudioRecorder *recorder,
    char *error_message,
    int error_capacity) {
    if (recorder == NULL) {
        set_error(error_message, error_capacity, @"連続録音サービスを作成できませんでした。");
        return 0;
    }

    if (recorder->running) {
        set_error(error_message, error_capacity, @"録音はすでに開始されています。");
        return 0;
    }

    [[NSFileManager defaultManager] removeItemAtPath:recorder->filePath error:nil];
    recorder->lastError = nil;
    recorder->pendingPcmData = [NSMutableData data];
    recorder->preRollPcmData = [NSMutableData data];
    recorder->savingAudio = NO;
    recorder->engine = [[AVAudioEngine alloc] init];

    AVAudioInputNode *inputNode = recorder->engine.inputNode;
    if (inputNode == nil) {
        set_error(error_message, error_capacity, @"macOSのマイク入力を取得できませんでした。");
        recorder->engine = nil;
        return 0;
    }

    // 入力デバイスの形式には依存せず、アプリ内部の共通形式を明示します。
    recorder->targetFormat = [[AVAudioFormat alloc]
        initWithCommonFormat:AVAudioPCMFormatInt16
                   sampleRate:recorder->sampleRate
                     channels:1
                  interleaved:YES];
    if (recorder->targetFormat == nil) {
        set_error(error_message, error_capacity, @"16kHzモノラル音声の形式を作成できませんでした。");
        recorder->engine = nil;
        return 0;
    }

    AVAudioFormat *inputFormat = [inputNode outputFormatForBus:0];
    recorder->converter = [[AVAudioConverter alloc] initFromFormat:inputFormat
                                                            toFormat:recorder->targetFormat];
    if (recorder->converter == nil) {
        set_error(error_message, error_capacity, @"マイク音声を16kHzへ変換する準備ができませんでした。");
        recorder->engine = nil;
        return 0;
    }

    recorder->running = YES;
    // formatにnilを渡すと、マイクが実際に出力している形式で受け取れます。
    // 受け取った直後に上記のconverterで16kHzへ統一します。
    [inputNode installTapOnBus:0
                    bufferSize:1024
                        format:nil
                         block:^(AVAudioPCMBuffer *buffer, AVAudioTime *when) {
        process_input_buffer(recorder, buffer);
    }];

    [recorder->engine prepare];
    NSError *engineError = nil;
    if (![recorder->engine startAndReturnError:&engineError]) {
        [inputNode removeTapOnBus:0];
        recorder->running = NO;
        recorder->converter = nil;
        recorder->engine = nil;
        set_error(
            error_message,
            error_capacity,
            engineError.localizedDescription ?: @"macOSの連続マイク録音を開始できませんでした。");
        return 0;
    }

    return 1;
}

int sts_continuous_audio_recorder_begin_audio_capture(
    STSContinuousAudioRecorder *recorder,
    char *error_message,
    int error_capacity) {
    if (recorder == NULL || !recorder->running || recorder->targetFormat == nil) {
        set_error(error_message, error_capacity, @"音声入力の待機が開始されていません。");
        return 0;
    }

    @synchronized (recorder->syncLock) {
        if (recorder->savingAudio) {
            return 1;
        }

        [[NSFileManager defaultManager] removeItemAtPath:recorder->filePath error:nil];
        NSError *fileError = nil;
        NSURL *fileURL = [NSURL fileURLWithPath:recorder->filePath];
        recorder->outputFile = [[AVAudioFile alloc] initForWriting:fileURL
                                                           settings:recorder->targetFormat.settings
                                                      commonFormat:AVAudioPCMFormatInt16
                                                       interleaved:YES
                                                             error:&fileError];
        if (recorder->outputFile == nil) {
            set_error(
                error_message,
                error_capacity,
                fileError.localizedDescription ?: @"発話保存用のWAVファイルを作成できませんでした。");
            return 0;
        }

        NSError *writeError = nil;
        if (!write_pcm_data(
                recorder,
                recorder->preRollPcmData.bytes,
                recorder->preRollPcmData.length,
                &writeError)) {
            recorder->outputFile = nil;
            set_error(
                error_message,
                error_capacity,
                writeError.localizedDescription ?: @"発話開始直前の音声を保存できませんでした。");
            return 0;
        }

        [recorder->preRollPcmData setLength:0];
        recorder->savingAudio = YES;
    }

    return 1;
}

int sts_continuous_audio_recorder_stop(STSContinuousAudioRecorder *recorder) {
    if (recorder == NULL || !recorder->running || recorder->engine == nil) {
        return 0;
    }

    AVAudioInputNode *inputNode = recorder->engine.inputNode;
    [inputNode removeTapOnBus:0];
    [recorder->engine stop];

    @synchronized (recorder->syncLock) {
        recorder->running = NO;
        // AVAudioFileを閉じてから読み出すことで、WAVヘッダーを含めて確定させます。
        recorder->outputFile = nil;
        recorder->savingAudio = NO;
        recorder->preRollPcmData = nil;
        recorder->converter = nil;
        recorder->targetFormat = nil;
        recorder->engine = nil;
    }

    return recorder->lastError == nil ? 1 : 0;
}

int sts_continuous_audio_recorder_copy_wav(
    STSContinuousAudioRecorder *recorder,
    uint8_t **data,
    int *size) {
    if (recorder == NULL || data == NULL || size == NULL || recorder->running || recorder->lastError != nil) {
        return 0;
    }

    NSData *wavData = [NSData dataWithContentsOfFile:recorder->filePath];
    if (wavData == nil || wavData.length == 0 || wavData.length > INT32_MAX) {
        return 0;
    }

    uint8_t *copy = malloc(wavData.length);
    if (copy == NULL) {
        return 0;
    }

    memcpy(copy, wavData.bytes, wavData.length);
    *data = copy;
    *size = (int)wavData.length;

    [[NSFileManager defaultManager] removeItemAtPath:recorder->filePath error:nil];
    return 1;
}

int sts_continuous_audio_recorder_get_last_error(
    STSContinuousAudioRecorder *recorder,
    char *error_message,
    int error_capacity) {
    if (recorder == NULL || recorder->lastError == nil) {
        return 0;
    }

    set_error(error_message, error_capacity, recorder->lastError);
    return 1;
}

void sts_continuous_audio_recorder_destroy(STSContinuousAudioRecorder *recorder) {
    if (recorder == NULL) {
        return;
    }

    if (recorder->running && recorder->engine != nil) {
        AVAudioInputNode *inputNode = recorder->engine.inputNode;
        [inputNode removeTapOnBus:0];
        [recorder->engine stop];
    }

    [[NSFileManager defaultManager] removeItemAtPath:recorder->filePath error:nil];
    recorder->pendingPcmData = nil;
    recorder->preRollPcmData = nil;
    recorder->outputFile = nil;
    recorder->converter = nil;
    recorder->targetFormat = nil;
    recorder->engine = nil;
    recorder->filePath = nil;
    recorder->lastError = nil;
    recorder->syncLock = nil;
    free(recorder);
}
