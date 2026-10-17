// Copyright Epic Games, Inc. All Rights Reserved.
// Modified: Added 1FCB (BCF1) encoding support alongside ABEU encoding.
#pragma once
#include <stdint.h>

typedef void* BAUECompressAllocFnType(uintptr_t ByteCount);
typedef void BAUECompressFreeFnType(void* Ptr);

#define BINKA_COMPRESS_SUCCESS 0
#define BINKA_COMPRESS_ERROR_CHANS 1
#define BINKA_COMPRESS_ERROR_SAMPLES 2
#define BINKA_COMPRESS_ERROR_RATE 3
#define BINKA_COMPRESS_ERROR_QUALITY 4
#define BINKA_COMPRESS_ERROR_ALLOCATORS 5
#define BINKA_COMPRESS_ERROR_OUTPUT 6
#define BINKA_COMPRESS_ERROR_SEEKTABLE 7
#define BINKA_COMPRESS_ERROR_SIZE 8

#define BINKA_FORMAT_ABEU  0   // UEBA: BinkAudio2 (7-bit scalefactor, grouped signs)
#define BINKA_FORMAT_1FCB  1   // BCF1: BinkAudio1 (8-bit scalefactor, inline signs)

//
// Compresses a bink audio file in ABEU format (backward compatible).
//
uint8_t UECompressBinkAudio(
    void* PcmData,
    uint32_t PcmDataLen,
    uint32_t PcmRate,
    uint8_t PcmChannels,
    uint8_t Quality,
    uint8_t GenerateSeekTable,
    uint16_t SeekTableMaxEntries,
    BAUECompressAllocFnType* MemAlloc,
    BAUECompressFreeFnType* MemFree,
    void** OutData,
    uint32_t* OutDataLen);

//
// Compresses a bink audio file in the specified format.
// Format: BINKA_FORMAT_ABEU or BINKA_FORMAT_1FCB
//
uint8_t UECompressBinkAudioEx(
    void* PcmData,
    uint32_t PcmDataLen,
    uint32_t PcmRate,
    uint8_t PcmChannels,
    uint8_t Quality,
    uint8_t Format,
    uint8_t GenerateSeekTable,
    uint16_t SeekTableMaxEntries,
    BAUECompressAllocFnType* MemAlloc,
    BAUECompressFreeFnType* MemFree,
    void** OutData,
    uint32_t* OutDataLen);
