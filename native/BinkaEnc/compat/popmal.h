/* Prism - replacement for RAD's popmal.h
 *
 * BinkAudioCompressOpen() uses RAD's "push malloc" helper to allocate one block
 * for all of the encoder's buffers. popmal.c ships with the encoder sources, it
 * only needs this header back. The struct layout below has to match popmal.c.
 */

#ifndef __POPMALH__
#define __POPMALH__

#ifndef __RADRR_COREH__
  #include "rrCore.h"
#endif

#define Round32(x)   (((U64)(x) + 31u) & ~(U64)31u)

// size of the popmal control block plus room for `num` pointer/size slots
#define PushMallocBytesForXPtrs(num) \
  Round32(sizeof(U64) * 4 + sizeof(U32) * 2 + sizeof(void*) * 2 * (num))

RADDEFFUNC void RADLINK pushmallocinit(void* base, U32 num_ptrs);
RADDEFFUNC void RADLINK pushmalloc(void* base, void* ptr, U64 amt);
RADDEFFUNC void RADLINK pushmalloco(void* base, void* ptr, U64 amt);
RADDEFFUNC U64 RADLINK popmalloctotal(void* base);
RADDEFFUNC void* RADLINK popmalloc(void* base, U64 amt, void* (*allocator)(UINTa bytes));

#define popfree(p, f)   ((f)(p))

#endif
