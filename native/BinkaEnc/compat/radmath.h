/* Prism - replacement for RAD's radmath.h
 *
 * `binkace.c` only uses a handful of scalar helpers from RAD's math header.
 * They are all plain libm calls on float, plus two integer bit tricks, so a
 * small shim is enough to build the encoder without any RAD library.
 */

#ifndef __RADMATHH__
#define __RADMATHH__

#ifndef __RADRR_COREH__
  #include "rrCore.h"
#endif

#include <math.h>
#include <stdlib.h>

// --- scalar math ------------------------------------------------------------

#define radfabs(x)      ((F32)fabs((double)(x)))
#define radfsqrt(x)     ((F32)sqrt((double)(x)))
#define radfloor(x)     ((F32)floor((double)(x)))
#define radatan2(y, x)  ((F64)atan2((double)(y), (double)(x)))
#define radlog10(x)     ((F64)log10((double)(x)))
#define radexp(x)       ((F32)exp((double)(x)))

static RADINLINE F64 radpow(F32 base, F32 exponent)
{
  return (F64)pow((double)base, (double)exponent);
}

static RADINLINE S32 radabs(S32 v)
{
  return v < 0 ? -v : v;
}

static RADINLINE U32 U32Abs(U32 v)
{
  return v;
}

// (a * b) / c in 64-bit, used to map a bin index onto a frequency
static RADINLINE U32 mult64anddiv(U32 a, U32 b, U32 c)
{
  return (U32)(((U64)a * (U64)b) / (U64)c);
}

// number of significant bits of v (0 for v == 0); 32767 -> 15, 1 -> 1
static RADINLINE U32 getbitlevelvar(U32 v)
{
  U32 bits = 0;
  while (v)
  {
    ++bits;
    v >>= 1;
  }
  return bits;
}

// log10 over the 0.05 .. 0.5 range the tonality model works in
static RADINLINE F32 ranged_log_0p05_to_0p5(F32 v)
{
  if (v <= 0.0f)
    return -1.30103f;   // log10(0.05)

  return (F32)log10((double)v);
}

#endif
