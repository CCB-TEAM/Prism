/* Prism - the pieces of RAD's radfft the Bink audio encoder needs
 *
 * binkace.c uses RAD's FFT library for two things:
 *
 *  radfft_rfft()  - real forward FFT, feeds the perceptual model only. Getting
 *                   the scaling slightly wrong changes how aggressively a band
 *                   is quantized, but never makes the stream undecodable.
 *
 *  radfft_dct()   - the DCT that produces the coefficients which end up in the
 *                   bitstream, so it has to be the exact inverse of what the
 *                   decoder applies.
 *
 * The decoder (binka_transform.cpp transform_dct == RAD's radfft ddct) computes
 *
 *     x[n] = c[0] + sum_{k>0} c[k] * cos(pi * k * (2n+1) / (2N))          (DCT-III)
 *
 * i.e. the plain DCT-III without the usual 1/2 on the DC term - which is why
 * BinkAudioCompressUnlock() halves samples[0] right after the analysis
 * transform. The matching analysis transform is therefore the unnormalized
 * DCT-II
 *
 *     c[k] = sum_{n} x[n] * cos(pi * k * (2n+1) / (2N))                   (DCT-II)
 *
 * with binkace.c's 1/sqrt(N) and the decoder's 2/sqrt(N) that round trips to
 * exactly 1.0. radfft_dct() below computes it with one 2N point complex FFT of
 * the even extension of x, so no dedicated DCT code is needed.
 */

#include "rrCore.h"
#include "radfft.h"

#include <math.h>
#include <string.h>

#define PRISM_FFT_POINTS 2048
#define PRISM_PI 3.14159265358979323846

RADDEFFUNC void RADLINK radfft_init(void)
{
  // nothing to set up, everything is computed inside the transforms
}

// ---------------------------------------------------------------------------
// plain iterative radix-2 complex FFT (forward, e^-i), in place
// ---------------------------------------------------------------------------

static void prism_fft(float* re, float* im, U32 n)
{
  U32 i, j, len;

  for (i = 1, j = 0; i < n; i++)
  {
    U32 bit = n >> 1;
    for (; j & bit; bit >>= 1)
      j ^= bit;
    j ^= bit;

    if (i < j)
    {
      float t = re[i]; re[i] = re[j]; re[j] = t;
      t = im[i]; im[i] = im[j]; im[j] = t;
    }
  }

  for (len = 2; len <= n; len <<= 1)
  {
    double angle = -2.0 * PRISM_PI / (double)len;
    float wr = (float)cos(angle);
    float wi = (float)sin(angle);
    U32 half = len >> 1;

    for (i = 0; i < n; i += len)
    {
      float cr = 1.0f;
      float ci = 0.0f;
      U32 k;

      for (k = 0; k < half; k++)
      {
        U32 a = i + k;
        U32 b = a + half;
        float xr = re[b] * cr - im[b] * ci;
        float xi = re[b] * ci + im[b] * cr;
        float nr = re[a] - xr;
        float ni = im[a] - xi;
        float next;

        re[a] += xr;
        im[a] += xi;
        re[b] = nr;
        im[b] = ni;

        next = cr * wr - ci * wi;
        ci = cr * wi + ci * wr;
        cr = next;
      }
    }
  }
}

// unnormalized DCT-II through a 2N point FFT of the even extension of the input
RADDEFFUNC void RADLINK radfft_dct(F32* out, F32* temp, U32 points)
{
  float re[PRISM_FFT_POINTS * 2];
  float im[PRISM_FFT_POINTS * 2];
  U32 n = points;
  U32 i;

  if (n > PRISM_FFT_POINTS)
    n = PRISM_FFT_POINTS;

  for (i = 0; i < n; i++)
  {
    re[i] = temp[i];
    re[n * 2 - 1 - i] = temp[i];
  }
  for (i = 0; i < n * 2; i++)
    im[i] = 0.0f;

  prism_fft(re, im, n * 2);

  // C[k] = 0.5 * Re( e^(-i*pi*k/(2N)) * V[k] )
  for (i = 0; i < n; i++)
  {
    double angle = -PRISM_PI * (double)i / (double)(n * 2);
    float cr = (float)cos(angle);
    float ci = (float)sin(angle);

    out[i] = 0.5f * (re[i] * cr - im[i] * ci);
  }
}

RADDEFFUNC void RADLINK radfft_rfft(rfft_complex* out, F32* temp, U32 points)
{
  float re[PRISM_FFT_POINTS];
  float im[PRISM_FFT_POINTS];
  U32 n = points;
  U32 k;

  if (n > PRISM_FFT_POINTS)
    n = PRISM_FFT_POINTS;

  for (k = 0; k < n; k++)
  {
    re[k] = temp[k];
    im[k] = 0.0f;
  }

  prism_fft(re, im, n);

  for (k = 0; k < n / 2; k++)
  {
    out[k].re = re[k];
    // RAD's rfft keeps the positive sine sum, the perceptual model expects it
    out[k].im = -im[k];
  }
}
