#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <string.h>
#include <math.h>
#include <unistd.h>
#include <aaudio/AAudio.h>
#include <android/log.h>

#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, "AudioPlayer", __VA_ARGS__)

static volatile int g_running = 1;

int main() {
    setvbuf(stderr, NULL, _IONBF, 0);
    LOGI("test_tone started");

    int32_t sample_rate = 48000;
    int32_t channels = 2;
    aaudio_format_t fmt = AAUDIO_FORMAT_PCM_I32;
    int bytes_per_sample = 4;
    int frame_size = bytes_per_sample * channels;

    AAudioStreamBuilder *builder = NULL;
    AAudioStream *stream = NULL;

    AAudio_createStreamBuilder(&builder);
    AAudioStreamBuilder_setDirection(builder, AAUDIO_DIRECTION_OUTPUT);
    AAudioStreamBuilder_setSampleRate(builder, sample_rate);
    AAudioStreamBuilder_setChannelCount(builder, channels);
    AAudioStreamBuilder_setFormat(builder, fmt);
    AAudioStreamBuilder_setBufferCapacityInFrames(builder, sample_rate * 50 / 1000);
    AAudioStreamBuilder_setPerformanceMode(builder, AAUDIO_PERFORMANCE_MODE_LOW_LATENCY);

    aaudio_result_t result = AAudioStreamBuilder_openStream(builder, &stream);
    if (result != AAUDIO_OK) {
        fprintf(stderr, "[test_tone] openStream failed: %d\n", result);
        LOGI("openStream FAILED: %d", result);
        AAudioStreamBuilder_delete(builder);
        return 1;
    }
    AAudioStreamBuilder_delete(builder);

    LOGI("stream opened OK");
    fprintf(stderr, "[test_tone] Stream opened\n");

    aaudio_format_t actual_fmt = AAudioStream_getFormat(stream);
    int32_t actual_sr = AAudioStream_getSampleRate(stream);
    int32_t actual_ch = AAudioStream_getChannelCount(stream);
    int32_t perf = AAudioStream_getPerformanceMode(stream);
    fprintf(stderr, "[test_tone] Format=%d SR=%d Ch=%d PerfMode=%d\n",
            actual_fmt, actual_sr, actual_ch, perf);
    LOGI("stream info: fmt=%d sr=%d ch=%d perf=%d", actual_fmt, actual_sr, actual_ch, perf);

    result = AAudioStream_requestStart(stream);
    if (result != AAUDIO_OK) {
        fprintf(stderr, "[test_tone] requestStart failed: %d\n", result);
        LOGI("requestStart FAILED: %d", result);
        AAudioStream_close(stream);
        return 1;
    }
    LOGI("requestStart OK");
    fprintf(stderr, "[test_tone] Stream started\n");

    // Generate 440Hz sine wave, PCM_I32, 2ch
    int buf_frames = 480; // 10ms
    int buf_size = buf_frames * frame_size;
    int32_t *buf = (int32_t*)malloc(buf_size);

    fprintf(stderr, "[test_tone] Generating 440Hz tone for 5 seconds...\n");
    LOGI("generating tone");

    for (int i = 0; i < 5 * 100; i++) { // 5 seconds, 10ms per iteration
        for (int f = 0; f < buf_frames; f++) {
            double t = (double)(i * buf_frames + f) / sample_rate;
            int32_t sample = (int32_t)(sin(2.0 * M_PI * 440.0 * t) * 2147483647.0);
            buf[f * 2] = sample;
            buf[f * 2 + 1] = sample;
        }

        int32_t written = AAudioStream_write(stream, buf, buf_frames, 2000000000LL);
        if (written < 0) {
            fprintf(stderr, "[test_tone] write error: %d\n", written);
            LOGI("write FAILED: %d", written);
            break;
        }

        if (i % 50 == 0) {
            int32_t xruns = AAudioStream_getXRunCount(stream);
            int32_t state = AAudioStream_getState(stream);
            fprintf(stderr, "[test_tone] progress: i=%d written=%d xruns=%d state=%d\n",
                    i, written, xruns, state);
        }
    }

    LOGI("tone generation done");
    fprintf(stderr, "[test_tone] Done, cleaning up\n");
    free(buf);
    AAudioStream_requestStop(stream);
    AAudioStream_close(stream);

    fprintf(stderr, "[test_tone] end\n");
    LOGI("test_tone end");
    return 0;
}
