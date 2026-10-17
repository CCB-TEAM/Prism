#include "binka_ue_encode.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>

#ifdef _MSC_VER
#define strcasecmp _stricmp
#endif

static void* alloc_thunk(uintptr_t bytes) { return malloc(bytes); }
static void free_thunk(void* ptr) { free(ptr); }

#pragma pack(push, 1)
typedef struct {
    uint16_t audioFormat;
    uint16_t numChannels;
    uint32_t sampleRate;
    uint32_t byteRate;
    uint16_t blockAlign;
    uint16_t bitsPerSample;
} WAVFmtData;
#pragma pack(pop)

static int read_wav(const char* path, void** pcm_data, uint32_t* pcm_len,
    uint32_t* sample_rate, uint16_t* channels)
{
    FILE* f = fopen(path, "rb");
    if (!f) { fprintf(stderr, "Error: Cannot open: %s\n", path); return -1; }

    char tag[4];
    if (fread(tag, 1, 4, f) != 4 || memcmp(tag, "RIFF", 4) != 0) {
        fprintf(stderr, "Error: Not a valid WAV file\n"); fclose(f); return -1;
    }
    fseek(f, 4, SEEK_CUR);
    if (fread(tag, 1, 4, f) != 4 || memcmp(tag, "WAVE", 4) != 0) {
        fprintf(stderr, "Error: Not a valid WAV file\n"); fclose(f); return -1;
    }

    WAVFmtData fmt = {};
    uint32_t data_size = 0;
    int got_fmt = 0, got_data = 0;

    while (!feof(f) && !got_data) {
        if (fread(tag, 1, 4, f) != 4) break;
        uint32_t chunk_size;
        if (fread(&chunk_size, 4, 1, f) != 1) break;

        if (memcmp(tag, "fmt ", 4) == 0) {
            if (chunk_size < 16) { fclose(f); return -1; }
            if (fread(&fmt, 1, 16, f) != 16) { fclose(f); return -1; }
            if (chunk_size > 16) fseek(f, chunk_size - 16, SEEK_CUR);
            got_fmt = 1;
        }
        else if (memcmp(tag, "data", 4) == 0) {
            data_size = chunk_size;
            got_data = 1;
        }
        else {
            fseek(f, chunk_size, SEEK_CUR);
        }
        if (chunk_size & 1) fseek(f, 1, SEEK_CUR);
    }

    if (!got_fmt || !got_data || fmt.bitsPerSample != 16) {
        fprintf(stderr, "Error: Only 16-bit WAV supported\n"); fclose(f); return -1;
    }

    *sample_rate = fmt.sampleRate;
    *channels = fmt.numChannels;
    *pcm_len = data_size;
    *pcm_data = malloc(data_size);
    if (fread(*pcm_data, 1, data_size, f) != data_size) {
        fprintf(stderr, "Error: Incomplete PCM read\n"); free(*pcm_data); fclose(f); return -1;
    }
    fclose(f);
    return 0;
}

static void print_usage(const char* exe) {
    fprintf(stderr, "Bink Audio Encoder\n\n");
    fprintf(stderr, "Usage: %s <input.wav> [-f 0|1] [-q 0-9]\n\n", exe);
    fprintf(stderr, "  -f 0   ABEU format (default)\n");
    fprintf(stderr, "  -f 1   1FCB format\n");
    fprintf(stderr, "  -q N   quality 0-9 (default 4, 0=highest)\n");
}

int main(int argc, char** argv)
{
    if (argc < 2) {
        print_usage(argv[0]);
        return 1;
    }

    const char* input_path = NULL;
    int format = BINKA_FORMAT_ABEU;
    int quality = 4;
    int gen_seek = -1;

    for (int i = 1; i < argc; i++) {
        if (strcmp(argv[i], "-f") == 0 && i + 1 < argc) {
            format = atoi(argv[++i]);
            if (format != 0 && format != 1) {
                fprintf(stderr, "Error: -f must be 0 or 1\n"); return 1;
            }
        }
        else if (strcmp(argv[i], "-q") == 0 && i + 1 < argc) {
            quality = atoi(argv[++i]);
            if (quality < 0 || quality > 9) {
                fprintf(stderr, "Error: -q must be 0-9\n"); return 1;
            }
        }
        else if (strcmp(argv[i], "-s") == 0) {
            gen_seek = 1;
        }
        else if (argv[i][0] != '-') {
            if (!input_path) {
                input_path = argv[i];
            }
            else {
                fprintf(stderr, "Error: Unexpected argument: %s\n", argv[i]); return 1;
            }
        }
        else {
            fprintf(stderr, "Error: Unknown option: %s\n", argv[i]); return 1;
        }
    }

    if (!input_path) {
        print_usage(argv[0]);
        return 1;
    }

    if (gen_seek < 0)
        gen_seek = (format == BINKA_FORMAT_ABEU) ? 1 : 0;

    char output_path[1024];
    strncpy(output_path, input_path, sizeof(output_path) - 16);
    output_path[sizeof(output_path) - 16] = '\0';
    char* ext = strrchr(output_path, '.');
    if (ext && strcasecmp(ext, ".wav") == 0)
        strcpy(ext, ".binka");
    else
        strcat(output_path, ".binka");

    void* pcm_data = NULL;
    uint32_t pcm_len = 0;
    uint32_t sample_rate = 0;
    uint16_t channels = 0;

    if (read_wav(input_path, &pcm_data, &pcm_len, &sample_rate, &channels) != 0)
        return 1;

    const char* format_name = (format == BINKA_FORMAT_1FCB) ? "1FCB" : "ABEU";
    printf("Input:  %s\n", input_path);
    printf("  %u Hz, %u ch, %u bytes PCM\n", sample_rate, channels, pcm_len);
    printf("Encode: format=%s quality=%d seek=%d\n", format_name, quality, gen_seek);

    void* out_data = NULL;
    uint32_t out_len = 0;

    uint8_t result = UECompressBinkAudioEx(pcm_data, pcm_len, sample_rate, channels,
        (uint8_t)quality, (uint8_t)format, (uint8_t)gen_seek, 4096,
        alloc_thunk, free_thunk, &out_data, &out_len);

    free(pcm_data);

    if (result != BINKA_COMPRESS_SUCCESS) {
        fprintf(stderr, "Error: Encoding failed (code %d)\n", result);
        return 1;
    }

    FILE* fout = fopen(output_path, "wb");
    if (!fout) { fprintf(stderr, "Error: Cannot write: %s\n", output_path); free(out_data); return 1; }
    fwrite(out_data, 1, out_len, fout);
    fclose(fout);
    free(out_data);

    printf("Output: %s (%u bytes, %.2f:1)\n", output_path, out_len, (float)pcm_len / out_len);
    return 0;
}