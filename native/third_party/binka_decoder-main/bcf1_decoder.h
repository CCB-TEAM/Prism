#ifndef BCF1_DECODER_H
#define BCF1_DECODER_H

#include <stdint.h>

typedef struct bcf1_decoder_t bcf1_decoder_t;

bcf1_decoder_t* bcf1_decoder_create(int sample_rate, int channels);
void bcf1_decoder_destroy(bcf1_decoder_t* dec);
void bcf1_decoder_reset(bcf1_decoder_t* dec);
int bcf1_decode_frame(bcf1_decoder_t* dec, uint8_t* input, uint32_t input_size, float* output);
int bcf1_get_frame_samples(bcf1_decoder_t* dec);
int bcf1_parse_header(uint8_t* data, int* version, int* channels, int* sample_rate, int* num_samples, uint32_t* data_offset);

#endif