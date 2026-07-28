#import "MacAudioRecorder.h"

#import <AVFoundation/AVFoundation.h>
#import <AudioToolbox/AudioToolbox.h>
#import <Foundation/Foundation.h>

struct STSAudioRecorder {
    // macOSの録音担当オブジェクトです。
    AVAudioRecorder *recorder;
    // WAVを一時保存する場所です。C#側へコピーした後に削除します。
    NSString *filePath;
    // 入力音声のサンプルレートです。現在はC#側から16000を受け取ります。
    int sampleRate;
    // StartからStopまで録音中かを示します。
    BOOL running;
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

STSAudioRecorder *sts_audio_recorder_create(int sample_rate) {
    if (sample_rate <= 0) {
        return NULL;
    }

    STSAudioRecorder *recorder = calloc(1, sizeof(STSAudioRecorder));
    if (recorder == NULL) {
        return NULL;
    }

    recorder->sampleRate = sample_rate;
    recorder->filePath = [[NSTemporaryDirectory()
        stringByAppendingPathComponent:[NSString stringWithFormat:@"stsapp-recording-%@.wav", NSUUID.UUID.UUIDString]] copy];

    return recorder;
}

int sts_audio_recorder_start(STSAudioRecorder *recorder, char *error_message, int error_capacity) {
    if (recorder == NULL) {
        set_error(error_message, error_capacity, @"録音サービスを作成できませんでした。");
        return 0;
    }

    if (recorder->running) {
        set_error(error_message, error_capacity, @"録音はすでに開始されています。");
        return 0;
    }

    // 前回の一時ファイルが残っていても、今回の録音へ混ざらないように削除します。
    [[NSFileManager defaultManager] removeItemAtPath:recorder->filePath error:nil];

    // AVAudioRecorderが扱う録音形式を、STTへ送るWAVの仕様に合わせます。
    // モノラル・16bit PCMに固定して、毎回同じ形式のファイルを作ります。
    NSURL *fileURL = [NSURL fileURLWithPath:recorder->filePath];
    NSDictionary *settings = @{
        AVFormatIDKey: @(kAudioFormatLinearPCM),
        AVSampleRateKey: @(recorder->sampleRate),
        AVNumberOfChannelsKey: @1,
        AVLinearPCMBitDepthKey: @16,
        AVLinearPCMIsBigEndianKey: @NO,
        AVLinearPCMIsFloatKey: @NO,
        AVLinearPCMIsNonInterleaved: @NO
    };

    NSError *createError = nil;
    recorder->recorder = [[AVAudioRecorder alloc] initWithURL:fileURL
                                                      settings:settings
                                                         error:&createError];
    if (recorder->recorder == nil) {
        set_error(error_message, error_capacity,
                  createError.localizedDescription ?: @"WAV録音を初期化できませんでした。");
        return 0;
    }

    if (![recorder->recorder prepareToRecord]) {
        recorder->recorder = nil;
        set_error(error_message, error_capacity, @"WAV録音の準備に失敗しました。");
        return 0;
    }

    // AVAudioRecorderに録音時間と音声データの保存を任せます。
    // これにより、AVAudioEngineのコールバックを手作業で結合する必要がなくなります。
    if (![recorder->recorder record]) {
        recorder->recorder = nil;
        set_error(error_message, error_capacity,
                  @"macOSのマイク録音を開始できませんでした。マイク権限と入力デバイスを確認してください。");
        return 0;
    }

    recorder->running = YES;
    return 1;
}

int sts_audio_recorder_stop(STSAudioRecorder *recorder) {
    if (recorder == NULL || !recorder->running || recorder->recorder == nil) {
        return 0;
    }

    [recorder->recorder stop];
    recorder->running = NO;
    recorder->recorder = nil;
    return 1;
}

int sts_audio_recorder_copy_wav(STSAudioRecorder *recorder, uint8_t **data, int *size) {
    if (recorder == NULL || data == NULL || size == NULL || recorder->running) {
        return 0;
    }

    // Stop後に一時WAVを読み込み、C#が受け取れるmalloc領域へコピーします。
    // 呼び出し元のC#側は、コピー後にNativeFreeでこの領域を解放します。
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

    // C#側へコピーした後は、一時ファイルを残さないようにします。
    [[NSFileManager defaultManager] removeItemAtPath:recorder->filePath error:nil];
    return 1;
}

void sts_audio_recorder_destroy(STSAudioRecorder *recorder) {
    if (recorder == NULL) {
        return;
    }

    if (recorder->running && recorder->recorder != nil) {
        [recorder->recorder stop];
    }

    [[NSFileManager defaultManager] removeItemAtPath:recorder->filePath error:nil];
    recorder->recorder = nil;
    recorder->filePath = nil;
    free(recorder);
}
