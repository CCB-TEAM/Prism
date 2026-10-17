#define _CRT_SECURE_NO_WARNINGS
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include "bcf1_decoder.h"
#include "ueba_decoder.h"

#pragma pack(push, 1)
typedef struct {
    char chunkID[4];
    uint32_t chunkSize;
    char format[4];
    char subchunk1ID[4];
    uint32_t subchunk1Size;
    uint16_t audioFormat;
    uint16_t numChannels;
    uint32_t sampleRate;
    uint32_t byteRate;
    uint16_t blockAlign;
    uint16_t bitsPerSample;
    char subchunk2ID[4];
    uint32_t subchunk2Size;
} WAVHeader;
#pragma pack(pop)

int write_wav(const char* filename, float* samples, int num_samples, int sample_rate, int channels) {
    FILE* f = fopen(filename, "wb");
    if (!f) return -1;

    float min_val = 0, max_val = 0;
    for (int i = 0; i < num_samples * channels; i++) {
        if (samples[i] < min_val) min_val = samples[i];
        if (samples[i] > max_val) max_val = samples[i];
    }
    printf("Sample range: %f to %f\n", min_val, max_val);

    WAVHeader header;
    memcpy(header.chunkID, "RIFF", 4);
    memcpy(header.format, "WAVE", 4);
    memcpy(header.subchunk1ID, "fmt ", 4);
    header.subchunk1Size = 16;
    header.audioFormat = 1;
    header.numChannels = (uint16_t)channels;
    header.sampleRate = sample_rate;
    header.byteRate = sample_rate * channels * 2;
    header.blockAlign = (uint16_t)(channels * 2);
    header.bitsPerSample = 16;
    memcpy(header.subchunk2ID, "data", 4);
    header.subchunk2Size = (uint32_t)num_samples * (uint32_t)channels * 2;
    header.chunkSize = 36 + header.subchunk2Size;

    fwrite(&header, sizeof(header), 1, f);

    for (int i = 0; i < num_samples * channels; i++) {
        float sample = samples[i];
        if (sample > 1.0f) sample = 1.0f;
        if (sample < -1.0f) sample = -1.0f;
        int16_t pcm = (int16_t)(sample * 32767.0f);
        fwrite(&pcm, sizeof(int16_t), 1, f);
    }

    fclose(f);
    return 0;
}

int main(int argc, char* argv[]) {
    if (argc < 2) {
        printf("Usage: %s -d <input.binka>\n", argv[0]);
        printf("       %s -h\n", argv[0]);
        return 1;
    }

    if (strcmp(argv[1], "-h") == 0) {
        printf("BCF1/UEBA Binka Decoder\n");
        printf("Usage: %s -d <input.binka>\n", argv[0]);
        return 0;
    }

    if (strcmp(argv[1], "-d") != 0 || argc < 3) {
        printf("Invalid arguments. Use -h for help.\n");
        return 1;
    }

    const char* input_file = argv[2];

    char output_file[512];
    strcpy(output_file, input_file);
    char* ext = strrchr(output_file, '.');
    if (ext) {
        strcpy(ext, ".wav");
    }
    else {
        strcat(output_file, ".wav");
    }

    FILE* f = fopen(input_file, "rb");
    if (!f) {
        printf("Cannot open input file: %s\n", input_file);
        return 1;
    }

    fseek(f, 0, SEEK_END);
    long file_size = ftell(f);
    fseek(f, 0, SEEK_SET);

    uint8_t* buffer = (uint8_t*)malloc((size_t)file_size);
    fread(buffer, 1, (size_t)file_size, f);
    fclose(f);

    int version, channels, sample_rate, num_samples;
    uint32_t data_offset;
    int is_ueba = 0;

    if (buffer[0] == 0x31 && buffer[1] == 0x46 && buffer[2] == 0x43 && buffer[3] == 0x42) {
        is_ueba = 0;
        if (bcf1_parse_header(buffer, &version, &channels, &sample_rate, &num_samples, &data_offset) < 0) {
            printf("Invalid BCF1 header\n");
            free(buffer);
            return 1;
        }
        printf("Format: BCF1\n");
    }
    else if (buffer[0] == 0x41 && buffer[1] == 0x42 && buffer[2] == 0x45 && buffer[3] == 0x55) {
        is_ueba = 1;
        if (ueba_parse_header(buffer, &version, &channels, &sample_rate, &num_samples, &data_offset, (uint32_t)file_size) < 0) {
            printf("Invalid UEBA header\n");
            free(buffer);
            return 1;
        }
        printf("Format: UEBA\n");
    }
    else {
        printf("Unknown format (not BCF1 or UEBA)\n");
        free(buffer);
        return 1;
    }

    printf("  Version: %d\n", version);
    printf("  Channels: %d\n", channels);
    printf("  Sample Rate: %d Hz\n", sample_rate);
    printf("  Total Samples: %d (%.2f seconds)\n", num_samples, (float)num_samples / (float)sample_rate);
    printf("  Data Offset: 0x%X\n", data_offset);

    void* dec;
    int frame_samples;

    if (is_ueba) {
        dec = ueba_decoder_create(sample_rate, channels);
        frame_samples = ueba_get_frame_samples((ueba_decoder_t*)dec);
    }
    else {
        dec = bcf1_decoder_create(sample_rate, channels);
        frame_samples = bcf1_get_frame_samples((bcf1_decoder_t*)dec);
    }

    if (!dec) {
        printf("Failed to create decoder\n");
        free(buffer);
        return 1;
    }

    printf("Frame samples: %d\n", frame_samples);

    float* all_pcm = (float*)calloc((size_t)num_samples * (size_t)channels, sizeof(float));
    int total_samples_decoded = 0;
    int frame_count = 0;

    if (is_ueba) {
        total_samples_decoded = ueba_decode_frame((ueba_decoder_t*)dec, buffer + data_offset, (uint32_t)(file_size - data_offset), all_pcm);
        if (total_samples_decoded < 0) {
            printf("Decode error\n");
        }
        else {
            frame_count = (total_samples_decoded + frame_samples - 1) / frame_samples;
            printf("\rDecoding: %d frames, %d/%d samples", frame_count, total_samples_decoded, num_samples);
        }
    }
    else {
        uint32_t pos = data_offset;
        float* pcm_buffer = (float*)malloc((size_t)frame_samples * (size_t)channels * sizeof(float));

        while (pos < (uint32_t)file_size) {
            int samples = bcf1_decode_frame((bcf1_decoder_t*)dec, buffer + pos, (uint32_t)(file_size - pos), pcm_buffer);
            if (samples < 0) {
                printf("Decode error at frame %d\n", frame_count);
                break;
            }
            if (samples == 0) break;

            int remaining = num_samples - total_samples_decoded;
            int copy_samples = (samples < remaining) ? samples : remaining;

            memcpy(all_pcm + (size_t)total_samples_decoded * (size_t)channels, pcm_buffer, (size_t)copy_samples * (size_t)channels * sizeof(float));
            total_samples_decoded += copy_samples;
            frame_count++;

            if (frame_count % 100 == 0) {
                printf("\rDecoding: %d frames, %d/%d samples", frame_count, total_samples_decoded, num_samples);
                fflush(stdout);
            }

            uint32_t header = *(uint32_t*)(buffer + pos);
            uint16_t sync = header & 0xFFFF;
            uint16_t packet_size = (header >> 16) & 0xFFFF;

            uint32_t bytes_consumed = 4;
            if (packet_size == 0xFFFF) {
                if (pos + 8 > (uint32_t)file_size) break;
                packet_size = *(uint16_t*)(buffer + pos + 4);
                bytes_consumed += 4;
            }
            bytes_consumed += packet_size;
            pos += bytes_consumed;

            if (total_samples_decoded >= num_samples) break;
        }
        free(pcm_buffer);
    }

    printf("\nDecoded %d frames, %d samples\n", frame_count, total_samples_decoded);

    if (total_samples_decoded > 0) {
        if (write_wav(output_file, all_pcm, total_samples_decoded, sample_rate, channels) == 0) {
            printf("WAV saved to: %s\n", output_file);
        }
        else {
            printf("Failed to write WAV file\n");
        }
    }

    free(all_pcm);
    if (is_ueba) {
        ueba_decoder_destroy((ueba_decoder_t*)dec);
    }
    else {
        bcf1_decoder_destroy((bcf1_decoder_t*)dec);
    }
    free(buffer);

    return 0;
}