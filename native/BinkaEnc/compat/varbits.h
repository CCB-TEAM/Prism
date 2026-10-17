/* Prism - replacement for RAD's varbits.h
 *
 * `binkace.c` writes its bitstream through the VarBits* macros. The Bink audio
 * bitstream is packed "Vorbis style": least significant bit first, values split
 * little-endian across bytes. That is exactly what the public decoder in
 * reference/binka_decoder-main/bitstream_lsb.h reads back, so this header is a
 * trimmed down version of that same bit writer (no bounds checks: the caller
 * knows the block cannot grow past buffer_size + buffer_size/2).
 */

#ifndef __VARBITSH__
#define __VARBITSH__

#ifndef __RADRR_COREH__
  #include "rrCore.h"
#endif

typedef struct VARBITS
{
  U8* ptr;    // output buffer
  U32 pos;    // current offset, in bits
} VARBITS;

static RADINLINE void VarBitsPut_(VARBITS* vb, U32 value, U32 bits)
{
  U32 pos = vb->pos;
  U32 shift = pos & 7;
  U32 byte = pos >> 3;
  U8* dst = vb->ptr + byte;

  dst[0] = (U8)((dst[0] & (U8)((1u << shift) - 1u)) | (U8)(value << shift));
  if (bits + shift > 8)
  {
    dst[1] = (U8)(value >> (8 - shift));
    if (bits + shift > 16)
    {
      dst[2] = (U8)(value >> (16 - shift));
      if (bits + shift > 24)
      {
        dst[3] = (U8)(value >> (24 - shift));
        if (bits + shift > 32)
          dst[4] = (U8)(value >> (32 - shift));
      }
    }
  }

  vb->pos = pos + bits;
}

#define VarBitsOpen(vb, buf)      do { (vb).ptr = (U8*)(buf); (vb).pos = 0; } while (0)
#define VarBitsPut(vb, val, bits) VarBitsPut_(&(vb), (U32)(val), (U32)(bits))
#define VarBitsPut1(vb, onebit)   VarBitsPut_(&(vb), (U32)((onebit) ? 1u : 0u), 1)
#define VarBitsPuta0(vb)          VarBitsPut_(&(vb), 0u, 1)
#define VarBitsPuta1(vb)          VarBitsPut_(&(vb), 1u, 1)
#define VarBitsSize(vb)           ((vb).pos)
#define VarBitsPutAlign(vb)       do { while ((vb).pos & 31u) VarBitsPut_(&(vb), 0u, 1); } while (0)

#endif
