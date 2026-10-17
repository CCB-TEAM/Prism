// Copyright Epic Games, Inc. All Rights Reserved.
// Modified: Added 1FCB (BCF1) file header definition.

#pragma once
#include <stdint.h>

typedef uint32_t uint32;
typedef uint16_t uint16;
typedef uint8_t uint8;

// ============================================================
// ABEU (UEBA) file header - Unreal Engine modern format
// ============================================================
struct BinkAudioFileHeader
{
    uint32 tag;                     // 'UEBA' (stored as "ABEU" in file)
    uint8 version;                  // 1
    uint8 channels;
    uint16 PADDING;
    uint32 rate;
    uint32 sample_count;
    uint16 max_comp_space_needed;
    uint16 flags;                   // 1 = bink_audio_2
    uint32 output_file_size;
    uint16 seek_table_entry_count;
    uint16 blocks_per_seek_table_entry;
};

// ============================================================
// 1FCB (BCF1) file header - Original Bink Audio format (v2)
// ============================================================
#pragma pack(push, 1)
struct BCF1FileHeader
{
    char     magic[4];          // "1FCB"
    uint8_t  version;           // 2
    uint8_t  channels;
    uint16_t sample_rate;       // LE
    uint32_t num_samples;       // total samples per channel, LE
    uint32_t max_block_size;    // max compressed block size, LE
    uint32_t file_size;         // total file size, LE
    uint16_t seek_entries;      // seek table entry count, LE
    uint16_t seek_granularity;  // blocks per seek entry
};
#pragma pack(pop)

#define BLOCK_HEADER_MAGIC 0x9999
