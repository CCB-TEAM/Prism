#include <cstdint>
#include <cstring>
#include <exception>
#include <vector>

#ifndef PRISM_BINKA_ONLY
#include "astcenc.h"
#endif
#include "bcf1_decoder.h"
#include "ueba_decoder.h"
#ifndef PRISM_BINKA_ONLY
#include "rdo_bc_encoder.h"
#endif

namespace
{
constexpr int kFormatAstc = 1;
constexpr int kFormatBc3 = 2;
constexpr int kFormatBc7 = 3;
constexpr int kWavHeaderSize = 44;

enum class BinkaContainer
{
    Unknown,
    Bcf1,
    Ueba
};

struct BinkaInfo
{
    BinkaContainer container = BinkaContainer::Unknown;
    int version = 0;
    int channels = 0;
    int sample_rate = 0;
    int sample_count = 0;
    uint32_t data_offset = 0;
};

#ifndef PRISM_BINKA_ONLY
float astc_quality(int quality)
{
    switch (quality)
    {
        case 0: return ASTCENC_PRE_FASTEST;
        case 1: return ASTCENC_PRE_FAST;
        case 3: return ASTCENC_PRE_THOROUGH;
        case 4: return ASTCENC_PRE_EXHAUSTIVE;
        case 2:
        default:
            return ASTCENC_PRE_MEDIUM;
    }
}

int encode_astc(
    const uint8_t* rgba,
    int width,
    int height,
    int block_width,
    int block_height,
    int quality,
    uint8_t* output,
    int output_length)
{
    if (block_width <= 0 || block_height <= 0)
        return 10;

    const unsigned int blocks_x = (static_cast<unsigned int>(width) + block_width - 1U) / block_width;
    const unsigned int blocks_y = (static_cast<unsigned int>(height) + block_height - 1U) / block_height;
    const size_t required = static_cast<size_t>(blocks_x) * blocks_y * 16U;
    if (required != static_cast<size_t>(output_length))
        return 11;

    astcenc_config config {};
    astcenc_error status = astcenc_config_init(
        ASTCENC_PRF_LDR,
        static_cast<unsigned int>(block_width),
        static_cast<unsigned int>(block_height),
        1,
        astc_quality(quality),
        0,
        &config);
    if (status != ASTCENC_SUCCESS)
        return 12;

    astcenc_context* context = nullptr;
    status = astcenc_context_alloc(&config, 1, &context, nullptr);
    if (status != ASTCENC_SUCCESS || context == nullptr)
        return 13;

    astcenc_image image {};
    image.dim_x = static_cast<unsigned int>(width);
    image.dim_y = static_cast<unsigned int>(height);
    image.dim_z = 1;
    image.data_type = ASTCENC_TYPE_U8;
    uint8_t* slice = const_cast<uint8_t*>(rgba);
    image.data = reinterpret_cast<void**>(&slice);

    const astcenc_swizzle swizzle {
        ASTCENC_SWZ_R,
        ASTCENC_SWZ_G,
        ASTCENC_SWZ_B,
        ASTCENC_SWZ_A
    };

    status = astcenc_compress_image(context, &image, &swizzle, output, required, 0);
    astcenc_context_free(context);
    return status == ASTCENC_SUCCESS ? 0 : 14;
}

int encode_bc(
    const uint8_t* rgba,
    int width,
    int height,
    int format,
    uint8_t* output,
    int output_length)
{
    utils::image_u8 source;
    source.init(static_cast<uint32_t>(width), static_cast<uint32_t>(height));
    for (int y = 0; y < height; y++)
    {
        for (int x = 0; x < width; x++)
        {
            const int offset = (y * width + x) * 4;
            source(static_cast<uint32_t>(x), static_cast<uint32_t>(y)).set(
                rgba[offset],
                rgba[offset + 1],
                rgba[offset + 2],
                rgba[offset + 3]);
        }
    }

    rdo_bc::rdo_bc_params params;
    params.clear();
    params.m_rdo_multithreading = false;
    params.m_status_output = false;
    params.m_rdo_lambda = 0.0f;
    params.m_dxgi_format = format == kFormatBc3 ? DXGI_FORMAT_BC3_UNORM : DXGI_FORMAT_BC7_UNORM;

    rdo_bc::rdo_bc_encoder encoder;
    if (!encoder.init(source, params))
        return 20;
    if (!encoder.encode())
        return 21;

    const uint32_t required = encoder.get_total_blocks_size_in_bytes();
    if (required != static_cast<uint32_t>(output_length))
        return 22;

    std::memcpy(output, encoder.get_blocks(), required);
    return 0;
}

#endif // PRISM_BINKA_ONLY

bool parse_binka_info(const uint8_t* input, int input_length, BinkaInfo& info)
{
    if (!input || input_length < 28)
        return false;

    auto* mutable_input = const_cast<uint8_t*>(input);
    if (input[0] == 0x31 && input[1] == 0x46 && input[2] == 0x43 && input[3] == 0x42)
    {
        if (bcf1_parse_header(
                mutable_input,
                &info.version,
                &info.channels,
                &info.sample_rate,
                &info.sample_count,
                &info.data_offset) < 0)
        {
            return false;
        }

        info.container = BinkaContainer::Bcf1;
        return info.data_offset < static_cast<uint32_t>(input_length);
    }

    if (input[0] == 0x41 && input[1] == 0x42 && input[2] == 0x45 && input[3] == 0x55)
    {
        if (ueba_parse_header(
                mutable_input,
                &info.version,
                &info.channels,
                &info.sample_rate,
                &info.sample_count,
                &info.data_offset,
                static_cast<uint32_t>(input_length)) < 0)
        {
            return false;
        }

        info.container = BinkaContainer::Ueba;
        return info.data_offset < static_cast<uint32_t>(input_length);
    }

    return false;
}

uint16_t read_u16_le(const uint8_t* data)
{
    uint16_t value = 0;
    std::memcpy(&value, data, sizeof(value));
    return value;
}

uint32_t read_u32_le(const uint8_t* data)
{
    uint32_t value = 0;
    std::memcpy(&value, data, sizeof(value));
    return value;
}

void write_u16_le(uint8_t* data, uint16_t value)
{
    std::memcpy(data, &value, sizeof(value));
}

void write_u32_le(uint8_t* data, uint32_t value)
{
    std::memcpy(data, &value, sizeof(value));
}

void write_wav_header(uint8_t* output, int pcm_bytes, int sample_rate, int channels)
{
    std::memcpy(output + 0, "RIFF", 4);
    write_u32_le(output + 4, static_cast<uint32_t>(36 + pcm_bytes));
    std::memcpy(output + 8, "WAVE", 4);
    std::memcpy(output + 12, "fmt ", 4);
    write_u32_le(output + 16, 16);
    write_u16_le(output + 20, 1);
    write_u16_le(output + 22, static_cast<uint16_t>(channels));
    write_u32_le(output + 24, static_cast<uint32_t>(sample_rate));
    write_u32_le(output + 28, static_cast<uint32_t>(sample_rate * channels * 2));
    write_u16_le(output + 32, static_cast<uint16_t>(channels * 2));
    write_u16_le(output + 34, 16);
    std::memcpy(output + 36, "data", 4);
    write_u32_le(output + 40, static_cast<uint32_t>(pcm_bytes));
}

int float_to_s16(float sample)
{
    if (sample > 1.0f)
        sample = 1.0f;
    if (sample < -1.0f)
        sample = -1.0f;

    return static_cast<int>(sample * 32767.0f);
}

int decode_binka_to_float(const uint8_t* input, int input_length, const BinkaInfo& info, std::vector<float>& pcm)
{
    if (info.channels <= 0 || info.sample_count <= 0 || info.sample_rate <= 0)
        return -1;

    const size_t sample_total = static_cast<size_t>(info.sample_count) * static_cast<size_t>(info.channels);
    if (sample_total > (static_cast<size_t>(1) << 29))
        return -2;

    pcm.assign(sample_total, 0.0f);
    int decoded_samples = 0;

    if (info.container == BinkaContainer::Ueba)
    {
        ueba_decoder_t* decoder = ueba_decoder_create(info.sample_rate, info.channels);
        if (!decoder)
            return -3;

        decoded_samples = ueba_decode_frame(
            decoder,
            const_cast<uint8_t*>(input + info.data_offset),
            static_cast<uint32_t>(input_length - info.data_offset),
            pcm.data());
        ueba_decoder_destroy(decoder);
        return decoded_samples < 0 ? -4 : decoded_samples;
    }

    if (info.container == BinkaContainer::Bcf1)
    {
        bcf1_decoder_t* decoder = bcf1_decoder_create(info.sample_rate, info.channels);
        if (!decoder)
            return -5;

        const int frame_samples = bcf1_get_frame_samples(decoder);
        if (frame_samples <= 0)
        {
            bcf1_decoder_destroy(decoder);
            return -6;
        }

        std::vector<float> frame(static_cast<size_t>(frame_samples) * static_cast<size_t>(info.channels));
        uint32_t position = info.data_offset;
        while (position < static_cast<uint32_t>(input_length) && decoded_samples < info.sample_count)
        {
            const int samples = bcf1_decode_frame(
                decoder,
                const_cast<uint8_t*>(input + position),
                static_cast<uint32_t>(input_length) - position,
                frame.data());
            if (samples < 0)
            {
                bcf1_decoder_destroy(decoder);
                return -7;
            }
            if (samples == 0)
                break;

            const int remaining = info.sample_count - decoded_samples;
            const int copy_samples = samples < remaining ? samples : remaining;
            std::memcpy(
                pcm.data() + static_cast<size_t>(decoded_samples) * static_cast<size_t>(info.channels),
                frame.data(),
                static_cast<size_t>(copy_samples) * static_cast<size_t>(info.channels) * sizeof(float));
            decoded_samples += copy_samples;

            if (position + 4 > static_cast<uint32_t>(input_length))
                break;

            const uint32_t header = read_u32_le(input + position);
            uint16_t packet_size = static_cast<uint16_t>((header >> 16) & 0xFFFF);
            uint32_t bytes_consumed = 4;
            if (packet_size == 0xFFFF)
            {
                if (position + 8 > static_cast<uint32_t>(input_length))
                    break;

                packet_size = read_u16_le(input + position + 4);
                bytes_consumed += 4;
            }

            bytes_consumed += packet_size;
            if (bytes_consumed == 0)
                break;

            position += bytes_consumed;
        }

        bcf1_decoder_destroy(decoder);
        return decoded_samples;
    }

    return -8;
}
}

extern "C"
{
#if defined(_WIN32)
__declspec(dllexport)
#else
__attribute__((visibility("default")))
#endif
#ifndef PRISM_BINKA_ONLY
int prism_encode_texture(
    const uint8_t* rgba,
    int width,
    int height,
    int format,
    int block_width,
    int block_height,
    int quality,
    uint8_t* output,
    int output_length)
{
    if (!rgba || !output || width <= 0 || height <= 0 || output_length <= 0)
        return 1;

    try
    {
        if (format == kFormatAstc)
            return encode_astc(rgba, width, height, block_width, block_height, quality, output, output_length);
        if (format == kFormatBc3 || format == kFormatBc7)
            return encode_bc(rgba, width, height, format, output, output_length);

        return 2;
    }
    catch (const std::exception&)
    {
        return 98;
    }
    catch (...)
    {
        return 99;
    }
}

#if defined(_WIN32)
__declspec(dllexport)
#else
__attribute__((visibility("default")))
#endif
#endif // PRISM_BINKA_ONLY

int prism_probe_binka_audio(
    const uint8_t* input,
    int input_length,
    int* sample_rate,
    int* channels,
    int* sample_count,
    int* wav_length)
{
    if (!input || input_length <= 0 || !sample_rate || !channels || !sample_count || !wav_length)
        return 1;

    try
    {
        BinkaInfo info {};
        if (!parse_binka_info(input, input_length, info))
            return 2;

        const int64_t pcm_length = static_cast<int64_t>(info.sample_count) * info.channels * 2;
        const int64_t total_length = pcm_length + kWavHeaderSize;
        if (info.sample_rate <= 0 || info.channels <= 0 || info.sample_count <= 0 || total_length > INT32_MAX)
            return 3;

        *sample_rate = info.sample_rate;
        *channels = info.channels;
        *sample_count = info.sample_count;
        *wav_length = static_cast<int>(total_length);
        return 0;
    }
    catch (const std::exception&)
    {
        return 98;
    }
    catch (...)
    {
        return 99;
    }
}

#if defined(_WIN32)
__declspec(dllexport)
#else
__attribute__((visibility("default")))
#endif
int prism_decode_binka_to_wav(
    const uint8_t* input,
    int input_length,
    uint8_t* output,
    int output_length,
    int* written_length)
{
    if (!input || input_length <= 0 || !output || output_length <= kWavHeaderSize || !written_length)
        return 1;

    try
    {
        BinkaInfo info {};
        if (!parse_binka_info(input, input_length, info))
            return 2;

        std::vector<float> pcm;
        const int decoded_samples = decode_binka_to_float(input, input_length, info, pcm);
        if (decoded_samples <= 0)
            return 10;

        const int64_t pcm_bytes64 = static_cast<int64_t>(decoded_samples) * info.channels * 2;
        const int64_t total_bytes64 = pcm_bytes64 + kWavHeaderSize;
        if (pcm_bytes64 <= 0 || total_bytes64 > output_length || total_bytes64 > INT32_MAX)
            return 11;

        const int pcm_bytes = static_cast<int>(pcm_bytes64);
        write_wav_header(output, pcm_bytes, info.sample_rate, info.channels);

        uint8_t* pcm_output = output + kWavHeaderSize;
        const int value_count = decoded_samples * info.channels;
        for (int i = 0; i < value_count; i++)
        {
            const int pcm16 = float_to_s16(pcm[static_cast<size_t>(i)]);
            write_u16_le(pcm_output + i * 2, static_cast<uint16_t>(static_cast<int16_t>(pcm16)));
        }

        *written_length = static_cast<int>(total_bytes64);
        return 0;
    }
    catch (const std::exception&)
    {
        return 98;
    }
    catch (...)
    {
        return 99;
    }
}
}
