#include <math.h>
#include <string.h>
#include <stdlib.h>
#include <inttypes.h>
#include <stdbool.h>

#include "bitstream_lsb.h"
#include "binka_dec.h"
#include "binka_data.h"
#include "binka_transform.h"

#define MAX_DECODERS 8
#define MAX_FRAME_CHANNELS 2
#define MAX_CHANNELS 8
#define MIN_CHANNELS 1
#define MAX_SAMPLE_RATE 96000
#define MIN_SAMPLE_RATE 300
#define MAX_FRAME_SAMPLES  2048
#define MAX_FRAME_OVERLAP  128
#define MAX_BANDS 26

#define FLAGS_DCT               (1<<0)
#define FLAGS_OUTPUT_PLANAR     (1<<1)
#define FLAGS_BINKA2            (1<<2)

typedef struct {
    const float* table;
    int frame_samples;
    float scale;
    int overlap_samples;
    int overlap_bits;
    int frame_channels;
    int is_first_frame;
    int band_count;
    int transform_size;
    uint32_t flags;
    int band_thresholds[MAX_BANDS];
} binka_decoder_t;

static void open_decoder(binka_decoder_t* decoder, int sample_rate, int frame_channels, uint32_t flags) {
    int frame_samples;
    if (sample_rate < 22050)
        frame_samples = 512;
    else if (sample_rate < 44100)
        frame_samples = 1024;
    else
        frame_samples = 2048;

    if ((flags & FLAGS_DCT) == 0) {
        sample_rate *= frame_channels;
        frame_samples *= frame_channels;
        frame_channels = 1;
    }

    int frame_samples_half = frame_samples >> 1;
    int sample_rate_half = (sample_rate + 1) >> 1;

    int band_count = 0;
    while (band_count < (MAX_BANDS - 1)) {
        if (binka_cutoff_frequency[band_count] >= sample_rate_half)
            break;
        band_count++;
    }

    memset(decoder, 0, sizeof(binka_decoder_t));

    if (frame_channels == 1) {
        flags &= 0xFFFFFFFD;
    }

    decoder->table = binka_cosines;
    decoder->transform_size = 2048;
    decoder->flags = flags;
    decoder->overlap_samples = frame_samples >> 4;
    decoder->frame_channels = frame_channels;
    decoder->band_count = band_count;
    decoder->frame_samples = frame_samples;

    decoder->overlap_bits = 0;
    switch (frame_samples >> 4) {
    case 32:
        decoder->overlap_bits = 5;
        break;
    case 64:
        decoder->overlap_bits = 6;
        break;
    case 128:
        decoder->overlap_bits = 7;
        break;
    case 256:
        decoder->overlap_bits = 8;
        break;
    default:
        break;
    }

    decoder->scale = 2.0 / sqrt(frame_samples);

    for (int i = 0; i < band_count; i++) {
        int band_limit = frame_samples_half * binka_cutoff_frequency[i] / sample_rate_half;
        if (band_limit == 0)
            band_limit = 1;
        decoder->band_thresholds[i] = band_limit;
    }
    decoder->band_thresholds[band_count] = frame_samples_half;

    decoder->is_first_frame = true;
}

static void apply_overlap(float* dst, int overlap_samples, float* overlap, int overlap_bits) {
    if (!overlap_bits)
        return;

    for (int i = 0; i < overlap_samples; i++) {
        float s1 = overlap[i];
        float s2 = (i * (dst[i] - s1)) / overlap_samples;
        dst[i] = (s1 + s2);
    }
}

static void apply_scale(float* coefs, float scale, int frame_samples) {
    for (int i = 0; i < frame_samples; i++) {
        coefs[i] *= scale;
    }
}

static void output_samples(binka_decoder_t* dec, float* coefs, float* overlap) {
    apply_scale(coefs, dec->scale, dec->frame_samples * dec->frame_channels);

    int output_samples = dec->frame_samples - dec->overlap_samples;

    int current_overlap_bits = dec->overlap_bits;
    if (dec->is_first_frame) {
        dec->is_first_frame = false;
        current_overlap_bits = 0;
    }

    float* coefs_ch = coefs;
    float* overlap_ch = overlap;
    for (int ch = 0; ch < dec->frame_channels; ch++) {
        apply_overlap(coefs_ch, dec->overlap_samples, overlap_ch, current_overlap_bits);
        memcpy(overlap_ch, &coefs_ch[output_samples], dec->overlap_samples * sizeof(float));
        coefs_ch += dec->frame_samples;
        overlap_ch += dec->overlap_samples;
    }
}

static float get_float29(bitstream_t* is) {
    uint32_t code = bl_read(is, 29);
    int power = (code >> 0) & 0x1f;
    int mantissa = (code >> 5) & 0x77FFFFF;
    int sign = (code >> 28) & 0x01;

    float value = (float)(mantissa * binka_float29_power[power]);
    if (sign)
        value = -value;
    return value;
}

static bool unpack_channel(float* coefs, int frame_samples, int band_count, bitstream_t* is, const int* band_thresholds, bool is_binka2) {
    coefs[0] = get_float29(is);
    coefs[1] = get_float29(is);

    int i_bits = is_binka2 ? 7 : 8;
    float scalefactors[MAX_BANDS];
    for (int i = 0; i < band_count; i++) {
        int index = bl_read(is, i_bits);
        if (index > 95)
            index = 95;
        scalefactors[i] = binka_scalefactors[index];
    }

    float band_scalefactor = 0.0;
    int band = 0;

    int pos = 2;
    while (pos < frame_samples) {
        int end;

        int rle_flag = bl_read(is, 1);
        if (rle_flag) {
            int rle_index = bl_read(is, 4);
            end = pos + 8 * binka_rle_table[rle_index];
        }
        else {
            end = pos + 8;
        }
        if (end > frame_samples)
            end = frame_samples;

        int q_bits = bl_read(is, 4);
        if (q_bits > 0) {
            if (is_binka2) {
                for (int subpos = pos; subpos < end; subpos++) {
                    int value = bl_read(is, q_bits);
                    coefs[subpos] = value;
                }

                for (int subpos = pos; subpos < end; subpos++) {
                    if (coefs[subpos] != 0.0) {
                        int negative = bl_read(is, 1);
                        if (negative)
                            coefs[subpos] = -coefs[subpos];
                    }
                }

                while (pos < end) {
                    if (pos == band_thresholds[band] * 2) {
                        band_scalefactor = scalefactors[band];
                        band++;
                    }
                    coefs[pos] *= band_scalefactor;
                    pos++;
                }
            }
            else {
                while (pos < end) {
                    if (pos == band_thresholds[band] * 2) {
                        band_scalefactor = scalefactors[band];
                        band++;
                    }

                    int value = bl_read(is, q_bits);
                    if (value) {
                        int negative = bl_read(is, 1);
                        float coef = value * band_scalefactor;
                        if (negative)
                            coef = -coef;
                        coefs[pos] = coef;
                    }
                    else {
                        coefs[pos] = 0.0f;
                    }
                    pos++;
                }
            }
        }
        else {
            memset(coefs + pos, 0, (end - pos) * sizeof(float));
            pos = end;

            while (end > band_thresholds[band] * 2) {
                band_scalefactor = scalefactors[band];
                band++;
            }
        }
    }

    if (is->error)
        return false;
    return true;
}

static int decode_frame(binka_decoder_t* dec, unsigned char* src, int src_size, float* coefs, float* overlap) {
    bitstream_t is_tmp;
    bitstream_t* is = &is_tmp;
    bl_setup(is, src, src_size);

    bool is_dct = dec->flags & FLAGS_DCT;
    bool is_binka2 = dec->flags & FLAGS_BINKA2;

    if (is_dct) {
        bl_skip(is, 2);
    }

    float* coefs_tmp = coefs;
    for (int ch = 0; ch < dec->frame_channels; ch++) {
        bool ok = unpack_channel(coefs_tmp, dec->frame_samples, dec->band_count, is, dec->band_thresholds, is_binka2);
        if (ok) {
            if (is_dct) {
                transform_dct(coefs_tmp, dec->frame_samples, dec->transform_size, dec->table);
            }
            else {
                transform_rdft(coefs_tmp, dec->frame_samples, dec->transform_size, dec->table);
            }
        }
        else {
            return -1;
        }
        coefs_tmp += dec->frame_samples;
    }

    output_samples(dec, coefs, overlap);

    bl_align(is, 32);
    int bytes_done = is->b_off / 8;
    return bytes_done;
}

struct binka_handle_t {
    int channels;
    int sample_rate;
    int output_samples;
    binka_decoder_t decoders[MAX_DECODERS];
    float* samples;
    float* overlap;
    int decoder_count;
};

binka_handle_t* binka_init(int sample_rate, int channels, binka_mode_t mode) {
    uint32_t flags;
    int temp_channels;

    if (sample_rate < MIN_SAMPLE_RATE || sample_rate > MAX_SAMPLE_RATE)
        return NULL;
    if (channels < MIN_CHANNELS || channels > MAX_CHANNELS)
        return NULL;

    binka_handle_t* ctx = (binka_handle_t*)calloc(1, sizeof(binka_handle_t));
    if (!ctx)
        return NULL;

    ctx->samples = (float*)calloc(1, MAX_FRAME_SAMPLES * channels * sizeof(float));
    if (!ctx->samples) {
        free(ctx);
        return NULL;
    }

    ctx->overlap = (float*)calloc(1, MAX_FRAME_OVERLAP * channels * sizeof(float));
    if (!ctx->overlap) {
        free(ctx->samples);
        free(ctx);
        return NULL;
    }

    ctx->channels = channels;
    ctx->sample_rate = sample_rate;
    ctx->decoder_count = (channels + 1) / MAX_FRAME_CHANNELS;

    if (ctx->sample_rate < 22050)
        ctx->output_samples = 480;
    else if (ctx->sample_rate < 44100)
        ctx->output_samples = 960;
    else
        ctx->output_samples = 1920;

    flags = 0x00;
    switch (mode) {
    case BINKA_BFC:
        if (channels > 2)
            flags |= FLAGS_OUTPUT_PLANAR;
        flags |= FLAGS_DCT;
        break;
    case BINKA_UEBA:
    {
        bool ueba_flag1 = true;
        bool ueba_flag2 = true;
        if (!ueba_flag1)
            flags |= FLAGS_OUTPUT_PLANAR;
        if (ueba_flag2)
            flags |= FLAGS_BINKA2;
        flags |= FLAGS_DCT;
        break;
    }
    default:
        free(ctx->samples);
        free(ctx->overlap);
        free(ctx);
        return NULL;
    }

    temp_channels = channels;
    for (int i = 0; i < ctx->decoder_count; i++) {
        int frame_channels = temp_channels >= 2 ? 2 : 1;
        open_decoder(&ctx->decoders[i], sample_rate, frame_channels, flags);
        temp_channels -= 2;
    }

    binka_reset(ctx);
    return ctx;
}

static void copy_samples(float* dst, float* src, int src_samples, int dst_samples, int channels) {
    for (int ch = 0; ch < channels; ch++) {
        int dst_i = ch;
        int src_i = src_samples * ch;
        for (int s = 0; s < dst_samples; s++) {
            float val = src[src_i];
            if (val > 32767.0f) val = 32767.0f;
            if (val < -32768.0f) val = -32768.0f;
            dst[dst_i] = val / 32768.0f;
            dst_i += channels;
            src_i++;
        }
    }
}

int binka_decode(binka_handle_t* ctx, unsigned char* src, int src_size, float* dst) {
    if (!ctx)
        return -1;

    float* samples = ctx->samples;
    float* overlap = ctx->overlap;
    for (int i = 0; i < ctx->decoder_count; i++) {
        binka_decoder_t* decoder = &ctx->decoders[i];
        int bytes_done = decode_frame(decoder, src, src_size, samples, overlap);
        if (bytes_done < 0)
            return bytes_done;
        samples += decoder->frame_samples * decoder->frame_channels;
        overlap += decoder->overlap_samples * decoder->frame_channels;
        src += bytes_done;
        src_size -= bytes_done;
    }

    if (src_size > 0x04)
        return -src_size;

    copy_samples(dst, ctx->samples, ctx->decoders[0].frame_samples, ctx->output_samples, ctx->channels);
    return ctx->output_samples;
}

void binka_free(binka_handle_t* ctx) {
    if (!ctx)
        return;
    free(ctx->samples);
    free(ctx->overlap);
    free(ctx);
}

void binka_reset(binka_handle_t* ctx) {
    if (!ctx)
        return;
    for (int i = 0; i < ctx->decoder_count; i++) {
        ctx->decoders[i].is_first_frame = true;
    }
}

int binka_get_frame_samples(binka_handle_t* ctx) {
    if (!ctx)
        return 0;
    return ctx->output_samples;
}