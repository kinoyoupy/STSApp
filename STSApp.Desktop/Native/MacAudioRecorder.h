#ifndef STSAPP_MAC_AUDIO_RECORDER_H
#define STSAPP_MAC_AUDIO_RECORDER_H

#include <stdint.h>

typedef struct STSAudioRecorder STSAudioRecorder;

STSAudioRecorder *sts_audio_recorder_create(int sample_rate);
int sts_audio_recorder_start(STSAudioRecorder *recorder, char *error_message, int error_capacity);
int sts_audio_recorder_stop(STSAudioRecorder *recorder);
int sts_audio_recorder_copy_wav(STSAudioRecorder *recorder, uint8_t **data, int *size);
void sts_audio_recorder_destroy(STSAudioRecorder *recorder);

#endif
