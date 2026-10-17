#include "ueba_decoder.h"
#include "binka_dec.h"
#include <stdlib.h>
#include <string.h>

typedef struct {
    uint8_t* buf;
    uint32_t buf_size;
    uint32_t filled;
    int samples;
} packet_t;

struct ueba_decoder_t {
    float* fbuf;
    packet_t pkt;
    binka_handle_t* handle;
    int channels;
    int frame_samples;
};

ueba_decoder_t* ueba_decoder_create(int sample_rate, int channels) {
    ueba_decoder_t* dec = (ueba_decoder_t*)calloc(1, sizeof(ueba_decoder_t));
    if (!dec) return NULL;
    dec->channels = channels;
    dec->handle = binka_init(sample_rate, channels, BINKA_UEBA);
    if (!dec->handle) goto fail;
    dec->frame_samples = binka_get_frame_samples(dec->handle);
    if (dec->frame_samples <= 0) goto fail;
    dec->pkt.buf_size = 16384;
    dec->pkt.buf = (uint8_t*)calloc(dec->pkt.buf_size, 1);
    if (!dec->pkt.buf) goto fail;
    dec->fbuf = (float*)calloc(dec->frame_samples * channels, sizeof(float));
    if (!dec->fbuf) goto fail;
    return dec;
fail:
    if (dec->handle) binka_free(dec->handle);
    free(dec->pkt.buf);
    free(dec->fbuf);
    free(dec);
    return NULL;
}

void ueba_decoder_destroy(ueba_decoder_t* dec) {
    if (!dec) return;
    if (dec->handle) binka_free(dec->handle);
    free(dec->pkt.buf);
    free(dec->fbuf);
    free(dec);
}

int ueba_parse_header(uint8_t* data, int* version, int* channels, int* sample_rate, int* num_samples, uint32_t* data_offset, uint32_t file_size) {
    if (data[0] != 0x41 || data[1] != 0x42 || data[2] != 0x45 || data[3] != 0x55)
        return -1;

    *version = data[4];
    *channels = data[5];
    *sample_rate = *(int32_t*)(data + 8);
    *num_samples = *(int32_t*)(data + 12);

    uint16_t flags = *(uint16_t*)(data + 18);

    if (*version != 1 || flags != 1) {
        return -1;
    }

    int seek_entries = *(uint16_t*)(data + 24);
    uint32_t pos = 28 + seek_entries * 2;

    *data_offset = pos;

    return 0;
}

static int read_frame(uint8_t* data, uint32_t* offset, uint32_t size, packet_t* pkt) {
    while (*offset + 4 <= size) {
        uint32_t header = *(uint32_t*)(data + *offset);

        if (header == 0x5345454B || header == 0x4B454553) {
            *offset += 4;
            if (*offset + 11 > size) return 0;
            int entries = *(int32_t*)(data + *offset + 7);
            if (entries <= 0) return 0;
            *offset += 11 + entries * 2;
            pkt->filled = 0;
            return 1;
        }

        uint16_t sync = header & 0xFFFF;
        if (sync == 0x9999) {
            uint16_t packet_size = (header >> 16) & 0xFFFF;
            *offset += 4;

            if (packet_size == 0xFFFF) {
                if (*offset + 4 > size) return 0;
                packet_size = *(uint16_t*)(data + *offset);
                pkt->samples = *(uint16_t*)(data + *offset + 2);
                *offset += 4;
            }
            else {
                pkt->samples = 0;
            }

            if (packet_size == 0 || packet_size > pkt->buf_size) return 0;
            if (*offset + packet_size > size) return 0;
            memcpy(pkt->buf, data + *offset, packet_size);
            pkt->filled = packet_size;
            *offset += packet_size;
            return 1;
        }

        (*offset)++;
    }
    return 0;
}

int ueba_decode_frame(ueba_decoder_t* dec, uint8_t* input, uint32_t input_size, float* output) {
    if (!dec || !input || input_size == 0) return 0;
    uint32_t offset = 0;
    int total_samples = 0;

    while (offset < input_size) {
        if (!read_frame(input, &offset, input_size, &dec->pkt)) {
            break;
        }
        if (dec->pkt.filled == 0) continue;

        int samples = binka_decode(dec->handle, dec->pkt.buf, dec->pkt.filled, dec->fbuf);
        if (samples < 0) return -1;
        if (dec->pkt.samples && samples > dec->pkt.samples)
            samples = dec->pkt.samples;

        memcpy(output + total_samples * dec->channels, dec->fbuf, samples * dec->channels * sizeof(float));
        total_samples += samples;
    }

    return total_samples;
}

void ueba_decoder_reset(ueba_decoder_t* dec) {
    if (dec && dec->handle)
        binka_reset(dec->handle);
}

int ueba_get_frame_samples(ueba_decoder_t* dec) {
    return dec ? dec->frame_samples : 0;
}