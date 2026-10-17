#ifndef UEBA_DECODER_H
#define UEBA_DECODER_H

#include <stdint.h>

typedef struct ueba_decoder_t ueba_decoder_t;

ueba_decoder_t* ueba_decoder_create(int sample_rate, int channels);
void ueba_decoder_destroy(ueba_decoder_t* dec);
void ueba_decoder_reset(ueba_decoder_t* dec);
int ueba_decode_frame(ueba_decoder_t* dec, uint8_t* input, uint32_t input_size, float* output);
int ueba_get_frame_samples(ueba_decoder_t* dec);
int ueba_parse_header(uint8_t* data, int* version, int* channels, int* sample_rate, int* num_samples, uint32_t* data_offset, uint32_t file_size);

#endif