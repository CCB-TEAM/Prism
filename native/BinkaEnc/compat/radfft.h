/* Prism - replacement for RAD's radfft.h
 *
 * `binkace.c` needs three things from RAD's FFT library: a one time init, a real
 * forward FFT (perceptual model only) and the DCT that produces the coefficients
 * the decoder inverts. radfft_prism.cpp implements them on top of our own FFT
 * and the DCT that already ships in reference/binka_decoder-main.
 */

#ifndef __RADFFTH__
#define __RADFFTH__

#ifndef __RADRR_COREH__
  #include "rrCore.h"
#endif

#define RADFFT_ALIGN 16

typedef struct rfft_complex
{
  F32 re;
  F32 im;
} rfft_complex;

RADDEFFUNC void RADLINK radfft_init(void);

// forward real FFT of `points` samples; writes points/2 complex bins
RADDEFFUNC void RADLINK radfft_rfft(rfft_complex* out, F32* temp, U32 points);

// forward DCT of `points` samples; writes `points` coefficients
RADDEFFUNC void RADLINK radfft_dct(F32* out, F32* temp, U32 points);

#endif
