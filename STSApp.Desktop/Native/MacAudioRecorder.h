#ifndef STSAPP_MAC_AUDIO_RECORDER_H
#define STSAPP_MAC_AUDIO_RECORDER_H

#include <stdint.h>

typedef struct STSContinuousAudioRecorder STSContinuousAudioRecorder;

// 16kHz・モノラル・16bit PCMの20msぶんの音声データを通知します。
// callback中のsamplesは次のcallbackまでしか有効ではないため、必要なら呼び出し側でコピーします。
typedef void (*STSAudioFrameCallback)(const int16_t *samples, int sample_count, void *context);

// macOSが現在のDesktopアプリへマイク利用を許可しているかを確認します。
// 許可されている場合は1、それ以外の場合は0を返し、理由をerror_messageへ書き込みます。
int sts_continuous_audio_recorder_check_microphone_permission(
    char *error_message,
    int error_capacity);

// VADが発話開始・終話を判断できるよう、録音中の音声フレームを継続して受け取ります。
// 完成WAVの保存は、発話開始が確定してから別メソッドで始めます。
STSContinuousAudioRecorder *sts_continuous_audio_recorder_create(
    int sample_rate,
    STSAudioFrameCallback frame_callback,
    void *callback_context);
int sts_continuous_audio_recorder_start(
    STSContinuousAudioRecorder *recorder,
    char *error_message,
    int error_capacity);
int sts_continuous_audio_recorder_begin_audio_capture(
    STSContinuousAudioRecorder *recorder,
    char *error_message,
    int error_capacity);
int sts_continuous_audio_recorder_stop(STSContinuousAudioRecorder *recorder);
int sts_continuous_audio_recorder_copy_wav(
    STSContinuousAudioRecorder *recorder,
    uint8_t **data,
    int *size);
int sts_continuous_audio_recorder_get_last_error(
    STSContinuousAudioRecorder *recorder,
    char *error_message,
    int error_capacity);
void sts_continuous_audio_recorder_destroy(STSContinuousAudioRecorder *recorder);

#endif
