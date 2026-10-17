/* Prism - in-process BINKA encoder entry point
 *
 * Turns 16 bit PCM into the ABEU container UE expects, using the encoder that
 * native/BinkaEnc builds from source (no RAD static library, no external exe).
 * The same source compiles into libprism_codecs.so on Android, which is what
 * lets the phone replace audio too.
 *
 * Two call form: call with output == NULL to learn the size, then call again.
 */

#include "rrCore.h"

#include "binka_ue_encode.h"

#include <stdlib.h>
#include <string.h>

// prism_encode_binka() 的返回码
#define PRISM_ENC_OK            0
#define PRISM_ENC_BAD_ARG       1
#define PRISM_ENC_FAILED        2   // 编码器报错（码 = 100 + 编码器状态）
#define PRISM_ENC_NEED_SPACE    3   // 缓冲区不够，*output_length 已填需求
#define PRISM_ENC_HEADER        4   // 产出的数据没有 ABEU 头

static void* prism_enc_alloc(uintptr_t bytes)
{
  return malloc(bytes);
}

static void prism_enc_free(void* ptr)
{
  free(ptr);
}

extern "C"
{
#if defined(_WIN32)
__declspec(dllexport)
#else
__attribute__((visibility("default")))
#endif
int prism_encode_binka(
  const uint8_t* pcm,
  int pcm_length,
  int sample_rate,
  int channels,
  int quality,
  int generate_seek_table,
  uint8_t* output,
  int output_capacity,
  int* output_length)
{
  void* encoded = 0;
  uint32_t encoded_length = 0;
  uint8_t status;

  if (!output_length)
    return PRISM_ENC_BAD_ARG;

  *output_length = 0;

  if (!pcm || pcm_length <= 0 || sample_rate <= 0 || channels <= 0 || channels > 8)
    return PRISM_ENC_BAD_ARG;

  if (quality < 0)
    quality = 0;
  if (quality > 9)
    quality = 9;

  status = UECompressBinkAudioEx(
    (void*)pcm,
    (uint32_t)pcm_length,
    (uint32_t)sample_rate,
    (uint8_t)channels,
    (uint8_t)quality,
    BINKA_FORMAT_ABEU,
    (uint8_t)(generate_seek_table ? 1 : 0),
    4096,
    prism_enc_alloc,
    prism_enc_free,
    &encoded,
    &encoded_length);

  if (status != BINKA_COMPRESS_SUCCESS || !encoded || encoded_length < 32)
  {
    prism_enc_free(encoded);
    return 100 + (int)status;
  }

  *output_length = (int)encoded_length;

  if (!output)
  {
    // 只报需求，不搬数据
    prism_enc_free(encoded);
    return PRISM_ENC_OK;
  }

  if (output_capacity < (int)encoded_length)
  {
    prism_enc_free(encoded);
    return PRISM_ENC_NEED_SPACE;
  }

  memcpy(output, encoded, encoded_length);
  prism_enc_free(encoded);

  // ABEU 头是 4 字节 tag，检查一下别把坏数据交出去
  if (output[0] != 'A' || output[1] != 'B' || output[2] != 'E' || output[3] != 'U')
    return PRISM_ENC_HEADER;

  return PRISM_ENC_OK;
}
}
