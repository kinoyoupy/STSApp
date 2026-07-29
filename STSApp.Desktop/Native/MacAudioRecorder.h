#ifndef STSAPP_MAC_AUDIO_RECORDER_H
#define STSAPP_MAC_AUDIO_RECORDER_H

#include <stdint.h>

typedef struct STSAudioRecorder STSAudioRecorder;
typedef struct STSContinuousAudioRecorder STSContinuousAudioRecorder;

// 16kHz・モノラル・16bit PCMの20msぶんの音声データを通知します。
// callback中のsamplesは次のcallbackまでしか有効ではないため、必要なら呼び出し側でコピーします。
typedef void (*STSAudioFrameCallback)(const int16_t *samples, int sample_count, void *context);

STSAudioRecorder *sts_audio_recorder_create(int sample_rate);
int sts_audio_recorder_start(STSAudioRecorder *recorder, char *error_message, int error_capacity);
int sts_audio_recorder_stop(STSAudioRecorder *recorder);
int sts_audio_recorder_copy_wav(STSAudioRecorder *recorder, uint8_t **data, int *size);
void sts_audio_recorder_destroy(STSAudioRecorder *recorder);

// VAD導入用の連続録音APIです。既存のSTSAudioRecorderは残したまま、
// 録音中の音声フレームを受け取れる経路を別に用意します。
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
