#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <signal.h>
#include <string.h>
#include <time.h>
#include <unistd.h>
#include <sys/socket.h>
#include <netinet/in.h>
#include <arpa/inet.h>
#include <aaudio/AAudio.h>
#include <android/log.h>
#include <math.h>
#include <errno.h>

#define FREQUENCY 1
#define BUFFER_SIZE 4096
#define LOG_INTERVAL 200
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, "AudioPlayer", __VA_ARGS__)
#define DEFAULT_PORT 27777

static volatile int g_running = 1;

static void handle_signal(int sig) {
    (void)sig;
    g_running = 0;
}

static const char* format_str(aaudio_format_t fmt) {
    switch (fmt) {
        case AAUDIO_FORMAT_INVALID:     return "INVALID";
        case AAUDIO_FORMAT_UNSPECIFIED: return "UNSPECIFIED";
        case AAUDIO_FORMAT_PCM_I16:     return "PCM_I16";
        case AAUDIO_FORMAT_PCM_FLOAT:   return "PCM_FLOAT";
        case AAUDIO_FORMAT_PCM_I32:     return "PCM_I32";
        default:                        return "UNKNOWN";
    }
}

static int wait_for_tcp_connection(int port) {
    int server_fd = socket(AF_INET, SOCK_STREAM, 0);
    if (server_fd < 0) {
        fprintf(stderr, "[audio_player] Failed to create TCP socket: errno=%d\n", errno);
        return -1;
    }

    int opt = 1;
    setsockopt(server_fd, SOL_SOCKET, SO_REUSEADDR, &opt, sizeof(opt));

    struct sockaddr_in addr;
    memset(&addr, 0, sizeof(addr));
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = htonl(INADDR_ANY); // 0.0.0.0 — ADB connects from external interface, not loopback
    addr.sin_port = htons(port);

    if (bind(server_fd, (struct sockaddr*)&addr, sizeof(addr)) < 0) {
        fprintf(stderr, "[audio_player] bind TCP 0.0.0.0:%d failed: errno=%d\n", port, errno);
        close(server_fd);
        return -1;
    }

    if (listen(server_fd, 1) < 0) {
        fprintf(stderr, "[audio_player] listen failed: errno=%d\n", errno);
        close(server_fd);
        return -1;
    }

    fprintf(stderr, "[audio_player] Waiting for TCP connection on 0.0.0.0:%d...\n", port);
    LOGI("Waiting for TCP connection on 0.0.0.0:%d", port);

    // Set 10-second timeout on accept
    struct timeval tv;
    tv.tv_sec = 10;
    tv.tv_usec = 0;
    setsockopt(server_fd, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof(tv));

    struct sockaddr_in client_addr;
    socklen_t client_len = sizeof(client_addr);
    int client_fd = accept(server_fd, (struct sockaddr*)&client_addr, &client_len);
    close(server_fd);

    if (client_fd < 0) {
        if (errno == EAGAIN || errno == EWOULDBLOCK) {
            fprintf(stderr, "[audio_player] accept TIMEOUT (10s) - no connection arrived\n");
            LOGI("accept TIMEOUT after 10s");
        } else {
            fprintf(stderr, "[audio_player] accept failed: errno=%d\n", errno);
            LOGI("accept FAILED: errno=%d", errno);
        }
        return -1;
    }

    LOGI("TCP connection accepted fd=%d", client_fd);
    fprintf(stderr, "[audio_player] TCP connection accepted\n");
    return client_fd;
}

int main(int argc, char *argv[]) {
    // Force stderr unbuffered so all messages appear in logs even on crash
    setvbuf(stderr, NULL, _IONBF, 0);

    LOGI("main() entered");

    fprintf(stderr, "[audio_player] [Function] int main() start\n");

    int32_t sample_rate = 48000;
    int32_t channels = 2;
    int32_t bits = 32;
    int32_t buffer_ms = 50;
    int32_t port = 0; // 0 = stdin mode, >0 = TCP mode

    if (argc > 1) sample_rate = atoi(argv[1]);
    if (argc > 2) channels = atoi(argv[2]);
    if (argc > 3) bits = atoi(argv[3]);
    if (argc > 4) buffer_ms = atoi(argv[4]);
    if (argc > 5) port = atoi(argv[5]);

    fprintf(stderr, "[audio_player] Config: %dHz %dbit %dch %dms port=%d\n",
            sample_rate, bits, channels, buffer_ms, port);

    aaudio_format_t requested_format;
    int bytes_per_sample;
    if (bits == 32) {
        requested_format = AAUDIO_FORMAT_PCM_I32;
        bytes_per_sample = 4;
    } else {
        requested_format = AAUDIO_FORMAT_PCM_I16;
        bytes_per_sample = 2;
    }
    fprintf(stderr, "[audio_player] Requested format: %s (%d)\n", format_str(requested_format), requested_format);

    signal(SIGINT, handle_signal);
    signal(SIGTERM, handle_signal);

    AAudioStreamBuilder *builder = NULL;
    AAudioStream *stream = NULL;
    aaudio_result_t result;

    result = AAudio_createStreamBuilder(&builder);
    if (result != AAUDIO_OK) {
        fprintf(stderr, "[audio_player] AAudio_createStreamBuilder failed: %d\n", result);
        return 1;
    }
    fprintf(stderr, "[audio_player] AAudio_createStreamBuilder ok\n");

    AAudioStreamBuilder_setDirection(builder, AAUDIO_DIRECTION_OUTPUT);
    AAudioStreamBuilder_setSampleRate(builder, sample_rate);
    AAudioStreamBuilder_setChannelCount(builder, channels);
    AAudioStreamBuilder_setFormat(builder, requested_format);
    AAudioStreamBuilder_setBufferCapacityInFrames(builder, sample_rate * buffer_ms / 1000);
    AAudioStreamBuilder_setPerformanceMode(builder, AAUDIO_PERFORMANCE_MODE_LOW_LATENCY);

    result = AAudioStreamBuilder_openStream(builder, &stream);
    if (result != AAUDIO_OK) {
        fprintf(stderr, "[audio_player] AAudioStreamBuilder_openStream failed: %d\n", result);
        AAudioStreamBuilder_delete(builder);
        return 1;
    }
    AAudioStreamBuilder_delete(builder);

    // Log actual stream parameters after opening
    aaudio_format_t actual_format = AAudioStream_getFormat(stream);
    int32_t actual_sample_rate = AAudioStream_getSampleRate(stream);
    int32_t actual_channels = AAudioStream_getChannelCount(stream);
    int32_t actual_buffer_size = AAudioStream_getBufferSizeInFrames(stream);
    int32_t actual_capacity = AAudioStream_getBufferCapacityInFrames(stream);
    int32_t actual_perf_mode = AAudioStream_getPerformanceMode(stream);
    int32_t frames_per_burst = AAudioStream_getFramesPerBurst(stream);

    fprintf(stderr, "[audio_player] AAudioStream opened\n");
    fprintf(stderr, "[audio_player]   Format: %s (%d)\n", format_str(actual_format), actual_format);
    fprintf(stderr, "[audio_player]   SampleRate: %d (requested %d)\n", actual_sample_rate, sample_rate);
    fprintf(stderr, "[audio_player]   Channels: %d (requested %d)\n", actual_channels, channels);
    fprintf(stderr, "[audio_player]   BufferSize: %d frames\n", actual_buffer_size);
    fprintf(stderr, "[audio_player]   BufferCapacity: %d frames\n", actual_capacity);
    fprintf(stderr, "[audio_player]   FramesPerBurst: %d\n", frames_per_burst);
    fprintf(stderr, "[audio_player]   PerfMode: %d (0=None 1=PowerSave 2=LowLatency)\n", actual_perf_mode);

    // Update frame size based on actual format
    switch (actual_format) {
        case AAUDIO_FORMAT_PCM_I16:
            bytes_per_sample = 2;
            break;
        case AAUDIO_FORMAT_PCM_FLOAT:
        case AAUDIO_FORMAT_PCM_I32:
            bytes_per_sample = 4;
            break;
        default:
            fprintf(stderr, "[audio_player] Unsupported actual format: %d, aborting\n", actual_format);
            AAudioStream_close(stream);
            return 1;
    }
    int frame_size = bytes_per_sample * actual_channels;
    fprintf(stderr, "[audio_player]   Using bytes_per_sample=%d frame_size=%d\n",
            bytes_per_sample, frame_size);

    // ---- Setup input source (TCP or stdin) ----
    int input_fd = STDIN_FILENO;
    if (port > 0) {
        int tcp_fd = wait_for_tcp_connection(port);
        if (tcp_fd < 0) {
            fprintf(stderr, "[audio_player] TCP connection FAILED, exiting\n");
            LOGI("TCP connection FAILED, exiting");
            AAudioStream_close(stream);
            return 1;
        }
        input_fd = tcp_fd;
        fprintf(stderr, "[audio_player] Using TCP input on fd=%d\n", input_fd);
    } else {
        fprintf(stderr, "[audio_player] Using stdin input\n");
    }

    LOGI("requesting stream start...");
    result = AAudioStream_requestStart(stream);
    if (result != AAUDIO_OK) {
        fprintf(stderr, "[audio_player] AAudioStream_requestStart failed: %d\n", result);
        LOGI("requestStart FAILED: %d", result);
        AAudioStream_close(stream);
        if (input_fd != STDIN_FILENO) close(input_fd);
        return 1;
    }
    LOGI("stream started OK");
    fprintf(stderr, "[audio_player] [Function] int main() normal debug informations: playback started\n");

    uint8_t buf[BUFFER_SIZE];
    ssize_t bytes_read;
    int64_t frames_written = 0;
    int64_t loop_count = 0;
    int64_t total_bytes_read = 0;

    int first_data = 1;
    int notified = 0;

    while (g_running && (bytes_read = read(input_fd, buf, sizeof(buf))) > 0) {
        if (first_data) {
            first_data = 0;
            LOGI("First PCM data received: %zd bytes", bytes_read);
            fprintf(stderr, "[audio_player] First PCM data received: %zd bytes (input_fd=%d)\n", bytes_read, input_fd);
        }
        total_bytes_read += bytes_read;
        loop_count++;

        int32_t frame_count = (int32_t)(bytes_read / frame_size);
        if (frame_count == 0) {
            fprintf(stderr, "[audio_player] WARNING: bytes_read=%zd < frame_size=%d, skipping\n",
                    bytes_read, frame_size);
            continue;
        }

        int32_t written = AAudioStream_write(stream, buf, frame_count, 200000000LL); // 200ms timeout
        if (written < 0) {
            fprintf(stderr, "[audio_player] AAudioStream_write error: %d\n", written);
            LOGI("AAudioStream_write FAILED: %d", written);
            break;
        }
        frames_written += written;

        // Send phone notification after first successful audio write (not before,
        // so system() calls don't delay stream startup). Failures are non-fatal.
        if (!notified) {
            notified = 1;
            int wake_ret = system("input keyevent KEYCODE_WAKEUP");
            int notify_ret = system("cmd notification post -t \"AudioBridge\" \"audio_bridge\" \"Start\"");
            if (wake_ret != 0 || notify_ret != 0) {
                fprintf(stderr, "[audio_player] Notification skipped (wake=%d notify=%d) — non-fatal\n", wake_ret, notify_ret);
            } else {
                fprintf(stderr, "[audio_player] User notification sent\n");
            }
        }

        // Periodic logging to confirm data flow
        if (loop_count % LOG_INTERVAL == 0) {
            int32_t xrun_count = AAudioStream_getXRunCount(stream);
            int32_t state = AAudioStream_getState(stream);
            fprintf(stderr, "[audio_player] Progress: loop=%lld total_bytes=%lld bytes_read=%zd "
                    "frames=%d written=%d total_frames=%lld xruns=%d state=%d\n",
                    (long long)loop_count, (long long)total_bytes_read, bytes_read,
                    frame_count, written, (long long)frames_written,
                    xrun_count, state);
        }
    }

    fprintf(stderr, "[audio_player] EOF: loop=%lld total_bytes=%lld total_frames=%lld g_running=%d\n",
            (long long)loop_count, (long long)total_bytes_read,
            (long long)frames_written, g_running);
    fprintf(stderr, "[audio_player] EOF received, draining...\n");
    AAudioStream_requestStop(stream);
    AAudioStream_close(stream);

    if (input_fd != STDIN_FILENO) {
        close(input_fd);
    }

    fprintf(stderr, "[audio_player] [Function] int main() end\n");
    return 0;
}
