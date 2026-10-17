#include "bcf1_decoder.h"
#include "binka_dec.h"
#include <stdlib.h>
#include <string.h>

typedef struct {
    uint8_t* buf;
    uint32_t buf_size;
    uint32_t filled;
    int samples;
} packet_t;

struct bcf1_decoder_t {
    float* fbuf;
    packet_t pkt;
    binka_handle_t* handle;
    int channels;
    int frame_samples;
};

#define MAX_FRAME_SIZE_CHANNEL 0x800

bcf1_decoder_t* bcf1_decoder_create(int sample_rate, int channels) {
    bcf1_decoder_t* dec = (bcf1_decoder_t*)calloc(1, sizeof(bcf1_decoder_t));
    if (!dec) return NULL;
    dec->channels = channels;
    dec->handle = binka_init(sample_rate, channels, BINKA_BFC);
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

void bcf1_decoder_destroy(bcf1_decoder_t* dec) {
    if (!dec) return;
    if (dec->handle) binka_free(dec->handle);
    free(dec->pkt.buf);
    free(dec->fbuf);
    free(dec);
}

int bcf1_parse_header(uint8_t* data, int* version, int* channels, int* sample_rate, int* num_samples, uint32_t* data_offset) {
    if (data[0] != 0x31 || data[1] != 0x46 || data[2] != 0x43 || data[3] != 0x42)
        return -1;
    *version = data[4];
    *channels = data[5];
    *sample_rate = *(uint16_t*)(data + 6);
    *num_samples = *(int32_t*)(data + 8);
    int seek_entries;
    if (*version == 1) {
        seek_entries = *(int32_t*)(data + 20);
        *data_offset = 24;
    }
    else if (*version == 2) {
        seek_entries = *(uint16_t*)(data + 20);
        *data_offset = 24 + seek_entries * 2;
    }
    else {
        return -1;
    }
    return 0;
}

static int read_frame(uint8_t* data, uint32_t* offset, uint32_t size, packet_t* pkt) {
    if (*offset + 4 > size) return 0;
    uint32_t header = *(uint32_t*)(data + *offset);
    uint16_t sync = header & 0xFFFF;
    uint16_t packet_size = (header >> 16) & 0xFFFF;
    *offset += 4;
    if (sync != 0x9999) return 0;
    if (packet_size == 0xFFFF) {
        if (*offset + 4 > size) return 0;
        packet_size = *(uint16_t*)(data + *offset);
        pkt->samples = *(uint16_t*)(data + *offset + 2);
        *offset += 4;
    }
    else {
        pkt->samples = 0;
    }
    if (packet_size > pkt->buf_size) return 0;
    if (*offset + packet_size > size) return 0;
    memcpy(pkt->buf, data + *offset, packet_size);
    pkt->filled = packet_size;
    *offset += packet_size;
    return 1;
}

int bcf1_decode_frame(bcf1_decoder_t* dec, uint8_t* input, uint32_t input_size, float* output) {
    if (!dec || !input || input_size == 0) return 0;
    uint32_t offset = 0;
    if (!read_frame(input, &offset, input_size, &dec->pkt)) return -1;
    if (dec->pkt.filled == 0) return 0;
    int samples = binka_decode(dec->handle, dec->pkt.buf, dec->pkt.filled, dec->fbuf);
    if (samples < 0) return -1;
    if (dec->pkt.samples && samples > dec->pkt.samples)
        samples = dec->pkt.samples;
    for (int i = 0; i < samples * dec->channels; i++) {
        output[i] = dec->fbuf[i];
    }
    return samples;
}

void bcf1_decoder_reset(bcf1_decoder_t* dec) {
    if (dec && dec->handle)
        binka_reset(dec->handle);
}

int bcf1_get_frame_samples(bcf1_decoder_t* dec) {
    return dec ? dec->frame_samples : 0;
}