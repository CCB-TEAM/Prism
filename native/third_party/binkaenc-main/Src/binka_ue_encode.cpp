// Copyright Epic Games, Inc. All Rights Reserved.
// Modified: Added 1FCB (BCF1) encoding support alongside ABEU encoding.
//
// Both formats share the same DCT transform and block framing (0x9999 sync word).
// The only differences are the encoder flags and file header format.
//   ABEU: BINKAC20 (binka2: 7-bit scalefactor, grouped sign bits)
//   1FCB: BINKACNEWFORMAT (binka1: 8-bit scalefactor, inline sign bits)

#include "rrCore.h"

#include "binkace.h"

#include "binka_ue_encode.h"
#include "binka_ue_file_header.h"

#include <stdio.h>
#include <string.h>

#define MAX_STREAMS 8

// ============================================================
// Memory buffer
// ============================================================
struct MemBufferEntry
{
    U32 bytecount;
    MemBufferEntry* next;
    char bytes[1];
};

struct MemBuffer
{
    void* (*memalloc)(uintptr_t bytes);
    void (*memfree)(void* ptr);
    MemBufferEntry* head;
    MemBufferEntry* tail;
    U32 total_bytes;
};

static void MemBufferAdd(MemBuffer* mem, void* data, U32 data_len)
{
    MemBufferEntry* entry = (MemBufferEntry*)mem->memalloc(sizeof(MemBufferEntry) + data_len);
    entry->bytecount = data_len;
    entry->next = 0;
    memcpy(entry->bytes, data, data_len);

    mem->total_bytes += data_len;
    if (mem->head == 0)
    {
        mem->tail = entry;
        mem->head = entry;
    }
    else
    {
        mem->tail->next = entry;
        mem->tail = entry;
    }
}

static void MemBufferFree(MemBuffer* mem)
{
    MemBufferEntry* entry = mem->head;
    while (entry)
    {
        MemBufferEntry* next = entry->next;
        mem->memfree(entry);
        entry = next;
    }
    mem->total_bytes = 0;
    mem->head = 0;
    mem->tail = 0;
}

static void MemBufferWriteBuffer(MemBuffer* mem, void* buffer)
{
    char* write_cursor = (char*)buffer;
    MemBufferEntry* entry = mem->head;
    while (entry)
    {
        MemBufferEntry* next = entry->next;
        memcpy(write_cursor, entry->bytes, entry->bytecount);
        write_cursor += entry->bytecount;
        entry = next;
    }
}

// ============================================================
// Seek table buffer
// ============================================================
#define SEEK_TABLE_BUFFER_CACHE 16

struct SeekTableBuffer
{
    MemBuffer buffer;
    U16 current_stack[SEEK_TABLE_BUFFER_CACHE];
    U32 current_index;
    U32 total_count;
    void* collapsed;
};

static void SeekTableBufferAdd(SeekTableBuffer* seek, U16 entry)
{
    seek->current_stack[seek->current_index] = entry;
    seek->current_index++;
    if (seek->current_index >= SEEK_TABLE_BUFFER_CACHE)
    {
        MemBufferAdd(&seek->buffer, seek->current_stack, sizeof(seek->current_stack));
        seek->current_index = 0;
    }
    seek->total_count++;
}

static void SeekTableBufferFree(SeekTableBuffer* seek)
{
    seek->buffer.memfree(seek->collapsed);
    MemBufferFree(&seek->buffer);
    seek->current_index = 0;
    seek->total_count = 0;
}

static U32 SeekTableBufferTrim(SeekTableBuffer* seek, U16 max_entry_count)
{
    U16* table = (U16*)seek->buffer.memalloc(seek->total_count * sizeof(U16));
    MemBufferWriteBuffer(&seek->buffer, table);
    memcpy((char*)table + seek->buffer.total_bytes, seek->current_stack, sizeof(U16) * seek->current_index);

    U32 frames_per_entry = 1;
    while (seek->total_count > (U32)max_entry_count)
    {
        frames_per_entry <<= 1;

        U32 read_index = 0;
        U32 write_index = 0;
        while (read_index < seek->total_count)
        {
            U32 total = table[read_index];
            if (read_index + 1 < seek->total_count)
                total += table[read_index + 1];

            if (total > 65535)
                return ~0U;

            table[write_index] = (U16)total;
            write_index++;
            read_index += 2;
        }

        seek->total_count = write_index;
    }

    seek->collapsed = table;
    return frames_per_entry;
}

// ============================================================
// FP state scope guard
// ============================================================
struct FPStateScope
{
    U32 saved_state;
    FPStateScope();
    ~FPStateScope();
};

#if defined(__RADSSE2__)
    #include <xmmintrin.h>
    FPStateScope::FPStateScope()
    {
        saved_state = _mm_getcsr();
        _mm_setcsr(_MM_MASK_MASK | _MM_ROUND_NEAREST | _MM_FLUSH_ZERO_OFF);
    }
    FPStateScope::~FPStateScope() { _mm_setcsr(saved_state); }
#elif defined(__RADARM__) && defined(__RAD64__)
    #ifdef _MSC_VER
        #include <intrin.h>
        static U32 read_fpcr() { return (U32)_ReadStatusReg(ARM64_FPCR); }
        static void write_fpcr(U32 state) { _WriteStatusReg(ARM64_FPCR, state); }
    #elif defined(__clang__) || defined(__GNUC__)
        static U32 read_fpcr() { U64 value; __asm__ volatile("mrs %0, fpcr" : "=r"(value)); return (U32)value; }
        static void write_fpcr(U32 state) { U64 s = state; __asm__ volatile("msr fpcr, %0" : : "r"(s)); }
    #else
        #error compiler? Not clang or msvc
    #endif
    FPStateScope::FPStateScope() { saved_state = read_fpcr(); write_fpcr(0); }
    FPStateScope::~FPStateScope() { write_fpcr(saved_state); }
#else
    FPStateScope::FPStateScope() : saved_state(0) {}
    FPStateScope::~FPStateScope() {}
#endif

// ============================================================
// Shared compression core
// ============================================================
struct CompressResult
{
    MemBuffer DataBuffer;
    SeekTableBuffer SeekTable;
    S32 MaxBlockSize;
    U8 NumBinkStreams;
    char* SourceStreams[MAX_STREAMS];
    HBINKAUDIOCOMP hBink[MAX_STREAMS];
};

static uint8_t CompressBlocks(
    void* WavData, uint32_t WavDataLen, uint32_t WavRate, uint8_t WavChannels,
    uint8_t Quality, U32 EncoderFlags,
    uint8_t GenerateSeekTable, uint16_t SeekTableMaxEntries,
    BAUECompressAllocFnType* MemAlloc, BAUECompressFreeFnType* MemFree,
    CompressResult* Result, U32* OutSamplesPerChannel)
{
    if (WavChannels == 0)
        return BINKA_COMPRESS_ERROR_CHANS;
    if (WavRate > 256000 || WavRate < 2000)
        return BINKA_COMPRESS_ERROR_RATE;
    if (Quality > 9)
        return BINKA_COMPRESS_ERROR_QUALITY;
    if (!MemAlloc || !MemFree)
        return BINKA_COMPRESS_ERROR_ALLOCATORS;
    if (GenerateSeekTable && SeekTableMaxEntries < 2)
        return BINKA_COMPRESS_ERROR_SEEKTABLE;

    U32 SamplesPerChannel = WavDataLen / (sizeof(S16) * WavChannels);
    U8 NumBinkStreams = (WavChannels / 2) + (WavChannels & 1);
    if (SamplesPerChannel == 0)
        return BINKA_COMPRESS_ERROR_SAMPLES;
    if (NumBinkStreams > MAX_STREAMS)
        return BINKA_COMPRESS_ERROR_CHANS;

    *OutSamplesPerChannel = SamplesPerChannel;

    // Deinterlace input
    S32 ChannelsPerStream[MAX_STREAMS] = {};
    S32 BytesPerStream[MAX_STREAMS] = {};
    S32 CurrentDeintChannel = 0;

    for (S32 i = 0; i < NumBinkStreams; i++)
    {
        ChannelsPerStream[i] = (WavChannels - CurrentDeintChannel) > 1 ? 2 : 1;
        BytesPerStream[i] = ChannelsPerStream[i] * sizeof(S16) * SamplesPerChannel;
        Result->SourceStreams[i] = (char*)MemAlloc(BytesPerStream[i]);

        S32 InputStride = sizeof(S16) * WavChannels;
        char* pInput = (char*)WavData + CurrentDeintChannel * sizeof(S16);
        char* pOutput = Result->SourceStreams[i];
        char* pInputEnd = pInput + SamplesPerChannel * InputStride;

        if (ChannelsPerStream[i] == 2)
        {
            while (pInput < pInputEnd)
            {
                *(S32*)pOutput = *(S32*)pInput;
                pInput += InputStride;
                pOutput += 2 * sizeof(S16);
            }
        }
        else
        {
            while (pInput < pInputEnd)
            {
                *(S16*)pOutput = *(S16*)pInput;
                pInput += InputStride;
                pOutput += sizeof(S16);
            }
        }
        CurrentDeintChannel += ChannelsPerStream[i];
    }

    // Open encoders with the specified flags
    char* StreamCursors[MAX_STREAMS];
    for (int i = 0; i < MAX_STREAMS; i++)
        StreamCursors[i] = Result->SourceStreams[i];

    S32 StreamBytesGenerated[MAX_STREAMS] = {0};

    HBINKAUDIOCOMP hBink[MAX_STREAMS] = {};
    for (S32 i = 0; i < NumBinkStreams; i++)
    {
        hBink[i] = BinkAudioCompressOpen(WavRate, ChannelsPerStream[i],
            EncoderFlags, (BinkAudioCompressAllocFnType*)MemAlloc, MemFree);
        Result->hBink[i] = hBink[i];
    }

    Result->DataBuffer = {};
    Result->DataBuffer.memalloc = MemAlloc;
    Result->DataBuffer.memfree = MemFree;
    Result->SeekTable = {};
    Result->SeekTable.buffer.memalloc = MemAlloc;
    Result->SeekTable.buffer.memfree = MemFree;
    Result->MaxBlockSize = 0;
    Result->NumBinkStreams = NumBinkStreams;

    U32 LastFrameLocation = 0;

    // Compression loop
    for (;;)
    {
        void* InputBuffers[MAX_STREAMS];
        U32 InputLens[MAX_STREAMS];
        U32 OutputLens[MAX_STREAMS];
        void* OutputBuffers[MAX_STREAMS];
        U32 InputUseds[MAX_STREAMS];

        U32 LimitedToSamples = ~0U;
        for (S32 StreamIndex = 0; StreamIndex < NumBinkStreams; StreamIndex++)
        {
            BinkAudioCompressLock(hBink[StreamIndex], InputBuffers + StreamIndex, InputLens + StreamIndex);

            U32 RemainingBytesInStream = (U32)(BytesPerStream[StreamIndex] - (StreamCursors[StreamIndex] - Result->SourceStreams[StreamIndex]));
            U32 CopyAmount = InputLens[StreamIndex];
            if (RemainingBytesInStream < CopyAmount)
                CopyAmount = RemainingBytesInStream;

            memcpy(InputBuffers[StreamIndex], StreamCursors[StreamIndex], CopyAmount);
            memset((char*)InputBuffers[StreamIndex] + CopyAmount, 0, InputLens[StreamIndex] - CopyAmount);

            BinkAudioCompressUnlock(hBink[StreamIndex], Quality, InputLens[StreamIndex],
                OutputBuffers + StreamIndex, OutputLens + StreamIndex, InputUseds + StreamIndex);
        }

        S32 AllDone = 1;
        for (S32 i = 0; i < NumBinkStreams; i++)
        {
            StreamCursors[i] += InputLens[i];
            if (StreamCursors[i] > Result->SourceStreams[i] + BytesPerStream[i])
                StreamCursors[i] = Result->SourceStreams[i] + BytesPerStream[i];

            StreamBytesGenerated[i] += InputUseds[i];
            if (StreamBytesGenerated[i] < BytesPerStream[i])
                AllDone = 0;
            else if (StreamBytesGenerated[i] > BytesPerStream[i])
            {
                LimitedToSamples = (BytesPerStream[i] - (StreamBytesGenerated[i] - InputUseds[i])) >> 1;
                if (ChannelsPerStream[i] == 2)
                    LimitedToSamples >>= 1;
            }
        }

        S32 TotalBytesUsedForBlock = 0;
        for (S32 i = 0; i < NumBinkStreams; i++)
            TotalBytesUsedForBlock += OutputLens[i];

        // Write block header (0x9999 sync, same for both formats)
        if (LimitedToSamples == ~0U)
        {
            U32 BlockHeader = (TotalBytesUsedForBlock << 16) | BLOCK_HEADER_MAGIC;
            MemBufferAdd(&Result->DataBuffer, &BlockHeader, 4);
        }
        else
        {
            U32 BlockHeader = 0xffff0000 | BLOCK_HEADER_MAGIC;
            MemBufferAdd(&Result->DataBuffer, &BlockHeader, 4);
            U32 LimitHeader = (LimitedToSamples << 16) | TotalBytesUsedForBlock;
            MemBufferAdd(&Result->DataBuffer, &LimitHeader, 4);
        }

        for (S32 i = 0; i < NumBinkStreams; i++)
            MemBufferAdd(&Result->DataBuffer, OutputBuffers[i], OutputLens[i]);

        if (TotalBytesUsedForBlock > Result->MaxBlockSize)
            Result->MaxBlockSize = TotalBytesUsedForBlock;

        if (GenerateSeekTable)
        {
            SeekTableBufferAdd(&Result->SeekTable, (U16)(Result->DataBuffer.total_bytes - LastFrameLocation));
            LastFrameLocation = Result->DataBuffer.total_bytes;
        }

        if (AllDone)
            break;
    }

    return BINKA_COMPRESS_SUCCESS;
}

static void CleanupCompress(CompressResult* Result, BAUECompressFreeFnType* MemFree)
{
    MemBufferFree(&Result->DataBuffer);
    SeekTableBufferFree(&Result->SeekTable);
    for (S32 i = 0; i < Result->NumBinkStreams; i++)
    {
        MemFree(Result->SourceStreams[i]);
        BinkAudioCompressClose(Result->hBink[i]);
    }
}

// ============================================================
// ABEU header writer
// ============================================================
static uint8_t WriteABEUHeader(
    CompressResult* Result, U32 SamplesPerChannel, uint32_t WavRate, uint8_t WavChannels,
    uint8_t GenerateSeekTable, uint16_t SeekTableMaxEntries,
    BAUECompressAllocFnType* MemAlloc, BAUECompressFreeFnType* MemFree,
    void** OutData, uint32_t* OutDataLen)
{
    U32 FramesPerEntry = 1;
    if (GenerateSeekTable)
    {
        FramesPerEntry = SeekTableBufferTrim(&Result->SeekTable, SeekTableMaxEntries);
        if (FramesPerEntry == ~0U)
        {
            CleanupCompress(Result, MemFree);
            return BINKA_COMPRESS_ERROR_SIZE;
        }
    }

    BinkAudioFileHeader Header;
    // 'UEBA' stored as LE uint32 = 0x41424555
    Header.tag = 'UEBA';
    Header.PADDING = 0;
    Header.channels = (U8)WavChannels;
    Header.rate = WavRate;
    Header.sample_count = SamplesPerChannel;
    Header.max_comp_space_needed = (U16)Result->MaxBlockSize;
    Header.flags = 1;
    Header.version = 1;
    Header.output_file_size = Result->DataBuffer.total_bytes + sizeof(BinkAudioFileHeader) + Result->SeekTable.total_count * sizeof(U16);
    Header.blocks_per_seek_table_entry = (U16)FramesPerEntry;
    Header.seek_table_entry_count = (U16)Result->SeekTable.total_count;

    char* Output = (char*)MemAlloc(Header.output_file_size);
    memcpy(Output, &Header, sizeof(BinkAudioFileHeader));
    if (Result->SeekTable.total_count > 0 && Result->SeekTable.collapsed)
        memcpy(Output + sizeof(BinkAudioFileHeader), Result->SeekTable.collapsed, Result->SeekTable.total_count * sizeof(U16));
    MemBufferWriteBuffer(&Result->DataBuffer, Output + sizeof(BinkAudioFileHeader) + Result->SeekTable.total_count * sizeof(U16));

    *OutData = Output;
    *OutDataLen = Header.output_file_size;

    CleanupCompress(Result, MemFree);
    return BINKA_COMPRESS_SUCCESS;
}

// ============================================================
// 1FCB header writer
// ============================================================
static uint8_t Write1FCBHeader(
    CompressResult* Result, U32 SamplesPerChannel, uint32_t WavRate, uint8_t WavChannels,
    uint8_t GenerateSeekTable, uint16_t SeekTableMaxEntries,
    BAUECompressAllocFnType* MemAlloc, BAUECompressFreeFnType* MemFree,
    void** OutData, uint32_t* OutDataLen)
{
    U32 FramesPerEntry = 1;
    if (GenerateSeekTable)
    {
        FramesPerEntry = SeekTableBufferTrim(&Result->SeekTable, SeekTableMaxEntries);
        if (FramesPerEntry == ~0U)
        {
            CleanupCompress(Result, MemFree);
            return BINKA_COMPRESS_ERROR_SIZE;
        }
    }

    BCF1FileHeader Header;
    memset(&Header, 0, sizeof(Header));
    memcpy(Header.magic, "1FCB", 4);
    Header.version = 2;
    Header.channels = WavChannels;
    Header.sample_rate = (uint16_t)WavRate;
    Header.num_samples = SamplesPerChannel;
    Header.max_block_size = (uint32_t)Result->MaxBlockSize;
    Header.seek_entries = (uint16_t)Result->SeekTable.total_count;
    Header.seek_granularity = (uint16_t)FramesPerEntry;

    uint32_t header_size = (uint32_t)sizeof(BCF1FileHeader);
    uint32_t seek_table_size = Result->SeekTable.total_count * sizeof(U16);
    uint32_t total_file_size = header_size + seek_table_size + Result->DataBuffer.total_bytes;
    Header.file_size = total_file_size;

    char* Output = (char*)MemAlloc(total_file_size);
    memcpy(Output, &Header, header_size);
    if (Result->SeekTable.total_count > 0 && Result->SeekTable.collapsed)
        memcpy(Output + header_size, Result->SeekTable.collapsed, seek_table_size);
    MemBufferWriteBuffer(&Result->DataBuffer, Output + header_size + seek_table_size);

    *OutData = Output;
    *OutDataLen = total_file_size;

    CleanupCompress(Result, MemFree);
    return BINKA_COMPRESS_SUCCESS;
}

// ============================================================
// Public API: UECompressBinkAudio (ABEU format, backward compatible)
// ============================================================
uint8_t UECompressBinkAudio(
    void* WavData, uint32_t WavDataLen, uint32_t WavRate, uint8_t WavChannels,
    uint8_t Quality, uint8_t GenerateSeekTable, uint16_t SeekTableMaxEntries,
    BAUECompressAllocFnType* MemAlloc, BAUECompressFreeFnType* MemFree,
    void** OutData, uint32_t* OutDataLen)
{
    return UECompressBinkAudioEx(WavData, WavDataLen, WavRate, WavChannels,
        Quality, BINKA_FORMAT_ABEU, GenerateSeekTable, SeekTableMaxEntries,
        MemAlloc, MemFree, OutData, OutDataLen);
}

// ============================================================
// Public API: UECompressBinkAudioEx (format selectable)
// ============================================================
uint8_t UECompressBinkAudioEx(
    void* WavData, uint32_t WavDataLen, uint32_t WavRate, uint8_t WavChannels,
    uint8_t Quality, uint8_t Format, uint8_t GenerateSeekTable, uint16_t SeekTableMaxEntries,
    BAUECompressAllocFnType* MemAlloc, BAUECompressFreeFnType* MemFree,
    void** OutData, uint32_t* OutDataLen)
{
    FPStateScope FixFloatingPoint;

    if (!OutData || !OutDataLen)
        return BINKA_COMPRESS_ERROR_OUTPUT;

    U32 EncoderFlags;
    if (Format == BINKA_FORMAT_1FCB)
        EncoderFlags = BINKACNEWFORMAT;   // 1: binka1 (8-bit scalefactor, inline signs)
    else
        EncoderFlags = BINKAC20;          // 4: binka2 (7-bit scalefactor, grouped signs)

    CompressResult Result = {};
    U32 SamplesPerChannel = 0;
    uint8_t err = CompressBlocks(WavData, WavDataLen, WavRate, WavChannels,
        Quality, EncoderFlags, GenerateSeekTable, SeekTableMaxEntries,
        MemAlloc, MemFree, &Result, &SamplesPerChannel);

    if (err != BINKA_COMPRESS_SUCCESS)
        return err;

    if (Format == BINKA_FORMAT_1FCB)
        return Write1FCBHeader(&Result, SamplesPerChannel, WavRate, WavChannels,
            GenerateSeekTable, SeekTableMaxEntries, MemAlloc, MemFree, OutData, OutDataLen);
    else
        return WriteABEUHeader(&Result, SamplesPerChannel, WavRate, WavChannels,
            GenerateSeekTable, SeekTableMaxEntries, MemAlloc, MemFree, OutData, OutDataLen);
}
