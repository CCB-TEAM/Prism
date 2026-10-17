/* Prism - diagnostic probe for the BINKA transform pair
 *
 * Runs the exact analyse/synthesise scaling the codec uses and reports the
 * round trip gain (must be 1.0) plus the residual error:
 *
 *   c = radfft_dct(x); c[0] *= 0.5; c *= 1/sqrt(N)      (binkace.c Percept)
 *   y = transform_dct(c);          y *= 2/sqrt(N)       (binka_dec.cpp)
 *
 * usage: prism_dct_probe <in.wav>
 */

#include "rrCore.h"
#include "radfft.h"

#include "binka_transform.h"
#include "binka_data.h"

#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>

#define N 2048

static float in[N];
static float coefs[N];
static float back[N];

int main(int argc, char** argv)
{
  FILE* f;
  char tag[4];
  uint32_t riff, datalen = 0, rate = 0;
  uint16_t ch = 1, bits = 16;
  int got_fmt = 0;

  if (argc < 2)
  {
    printf("usage: %s <in.wav>\n", argv[0]);
    return 1;
  }

  f = fopen(argv[1], "rb");
  if (!f) { printf("cannot open %s\n", argv[1]); return 1; }

  if (fread(tag, 1, 4, f) != 4 || memcmp(tag, "RIFF", 4) || fread(&riff, 4, 1, f) != 1 ||
      fread(tag, 1, 4, f) != 4 || memcmp(tag, "WAVE", 4))
  {
    printf("not a wav\n");
    fclose(f);
    return 1;
  }

  while (!got_fmt)
  {
    uint32_t size;
    if (fread(tag, 1, 4, f) != 4) break;
    if (fread(&size, 4, 1, f) != 1) break;
    if (!memcmp(tag, "fmt ", 4))
    {
      uint8_t fmt[16];
      if (fread(fmt, 1, 16, f) != 16) break;
      ch = *(uint16_t*)(fmt + 2);
      rate = *(uint32_t*)(fmt + 4);
      bits = *(uint16_t*)(fmt + 14);
      printf("wav: %u Hz, %u ch, %u bit\n", rate, ch, bits);
      if (size > 16) fseek(f, size - 16, SEEK_CUR);
      got_fmt = 1;
    }
    else
    {
      fseek(f, size, SEEK_CUR);
    }
  }

  while (fread(tag, 1, 4, f) == 4)
  {
    if (fread(&datalen, 4, 1, f) != 1) break;
    if (!memcmp(tag, "data", 4)) break;
    fseek(f, datalen, SEEK_CUR);
  }

  {
    int16_t* raw = (int16_t*)malloc(datalen ? datalen : 1);
    int i, count, chans = ch ? ch : 1;
    double num = 0.0, den = 0.0, err = 0.0, maxerr = 0.0;
    float root = (float)(1.0 / sqrt((double)N));

    if (!raw) { fclose(f); return 1; }
    if (fread(raw, 1, datalen, f) != datalen) { free(raw); fclose(f); return 1; }
    fclose(f);

    count = (int)(datalen / (2 * chans));
    if (count > N) count = N;
    for (i = 0; i < count; i++)
      in[i] = (float)raw[i * chans];
    for (; i < N; i++)
      in[i] = count ? in[count - 1] : 0.0f;

    free(raw);

    radfft_init();

    radfft_dct(coefs, in, N);
    coefs[0] *= 0.5f;
    for (i = 0; i < N; i++)
      coefs[i] *= root;

    printf("first coefficients: %.2f %.2f %.2f %.2f %.2f %.2f\n",
      coefs[0], coefs[1], coefs[2], coefs[3], coefs[4], coefs[5]);

    memcpy(back, coefs, sizeof(back));
    transform_dct(back, N, N, binka_cosines);
    for (i = 0; i < N; i++)
      back[i] *= (2.0f * root);

    for (i = 0; i < N; i++)
    {
      double d = (double)back[i] - (double)in[i];
      num += (double)back[i] * (double)in[i];
      den += (double)in[i] * (double)in[i];
      err += d * d;
      if (fabs(d) > maxerr) maxerr = fabs(d);
    }

    printf("round trip: gain %.6f, rms err %.3f, max err %.3f, rms signal %.3f\n",
      den > 0.0 ? num / den : 0.0, sqrt(err / N), maxerr, sqrt(den / N));
  }

  return 0;
}
