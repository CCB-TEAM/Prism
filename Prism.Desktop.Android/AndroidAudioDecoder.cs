using System;
using System.Diagnostics;
using System.IO;
using Android.Media;
using UAssetAudio.Core;

namespace Prism.Desktop.Android;

/// <summary>
/// 手机上解 m4a / aac / flac / opus 这类"系统支持但 .NET 没有托管解码器"的音频。
///
/// 为什么需要：Android 上跑不了 tools/ffmpeg.exe，而手机里的素材大多是 M4A，
/// 没有这一层时音频替换在手机上只能吃 WAV / MP3 / OGG。
/// 这里用系统自带的 MediaExtractor + MediaCodec 解成 16 位 PCM，交给
/// <see cref="AudioSourceDecoder.PlatformDecoder"/>。
/// </summary>
internal static class AndroidAudioDecoder
{
    // MediaCodec 的返回值常量（避免依赖绑定里的成员命名）
    private const int InfoTryAgainLater = -1;
    private const int InfoOutputFormatChanged = -2;
    private const int InfoOutputBuffersChanged = -3;

    private const int EncodingPcmFloat = 4;
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(5);

    public static PcmAudio? TryDecode(string path)
    {
        MediaExtractor? extractor = null;
        MediaCodec? codec = null;
        try
        {
            extractor = new MediaExtractor();
            extractor.SetDataSource(path);

            int trackIndex = -1;
            MediaFormat? trackFormat = null;
            for (int i = 0; i < extractor.TrackCount; i++)
            {
                MediaFormat candidate = extractor.GetTrackFormat(i);
                string? mime = candidate.GetString(MediaFormat.KeyMime);
                if (mime is not null && mime.StartsWith("audio/", StringComparison.Ordinal))
                {
                    trackIndex = i;
                    trackFormat = candidate;
                    break;
                }
            }

            if (trackIndex < 0 || trackFormat is null)
                return null;

            string mimeType = trackFormat.GetString(MediaFormat.KeyMime) ?? throw new InvalidDataException("音轨缺少 mime。");
            int sampleRate = ReadInt(trackFormat, MediaFormat.KeySampleRate, 0);
            int channels = ReadInt(trackFormat, MediaFormat.KeyChannelCount, 0);

            extractor.SelectTrack(trackIndex);

            codec = MediaCodec.CreateDecoderByType(mimeType)
                    ?? throw new InvalidDataException($"系统没有 {mimeType} 的解码器。");
            codec.Configure(trackFormat, null, null, MediaCodecConfigFlags.None);
            codec.Start();

            using var pcm = new MemoryStream();
            var info = new MediaCodec.BufferInfo();
            var clock = Stopwatch.StartNew();
            bool inputDone = false;
            bool outputDone = false;
            int pcmEncoding = 2; // ENCODING_PCM_16BIT

            while (!outputDone)
            {
                if (clock.Elapsed > Budget)
                    throw new TimeoutException("系统解码器超时。");

                if (!inputDone)
                {
                    int inputIndex = codec.DequeueInputBuffer(10_000);
                    if (inputIndex >= 0)
                    {
                        var inputBuffer = codec.GetInputBuffer(inputIndex);
                        int size = inputBuffer is null ? -1 : extractor.ReadSampleData(inputBuffer, 0);
                        if (size < 0)
                        {
                            codec.QueueInputBuffer(inputIndex, 0, 0, 0, MediaCodecBufferFlags.EndOfStream);
                            inputDone = true;
                        }
                        else
                        {
                            codec.QueueInputBuffer(inputIndex, 0, size, extractor.SampleTime, MediaCodecBufferFlags.None);
                            extractor.Advance();
                        }
                    }
                }

                int outputIndex = codec.DequeueOutputBuffer(info, 10_000);
                if (outputIndex == InfoOutputFormatChanged)
                {
                    MediaFormat outputFormat = codec.OutputFormat;
                    int rate = ReadInt(outputFormat, MediaFormat.KeySampleRate, sampleRate);
                    int count = ReadInt(outputFormat, MediaFormat.KeyChannelCount, channels);
                    if (rate > 0)
                        sampleRate = rate;
                    if (count > 0)
                        channels = count;
                    if (outputFormat.ContainsKey(MediaFormat.KeyPcmEncoding))
                        pcmEncoding = ReadInt(outputFormat, MediaFormat.KeyPcmEncoding, pcmEncoding);
                }
                else if (outputIndex == InfoOutputBuffersChanged)
                {
                    // API 21+ 不需要重新取 buffer 数组
                }
                else if (outputIndex >= 0)
                {
                    var outputBuffer = codec.GetOutputBuffer(outputIndex);
                    if (outputBuffer is not null && info.Size > 0)
                    {
                        var chunk = new byte[info.Size];
                        outputBuffer.Position(info.Offset);
                        outputBuffer.Get(chunk, 0, info.Size);
                        pcm.Write(chunk, 0, chunk.Length);
                    }

                    codec.ReleaseOutputBuffer(outputIndex, false);
                    if ((info.Flags & MediaCodecBufferFlags.EndOfStream) != 0)
                        outputDone = true;
                }
                else if (outputIndex != InfoTryAgainLater)
                {
                    // 未知返回值：别死循环
                    throw new InvalidDataException($"系统解码器返回了 {outputIndex}。");
                }
            }

            if (pcm.Length == 0 || sampleRate <= 0 || channels <= 0)
                return null;

            byte[] data = pcm.ToArray();
            if (pcmEncoding == EncodingPcmFloat)
                data = FloatBufferToPcm16(data);
            if (data.Length < channels * 2)
                return null;

            // 截掉不足一帧的尾巴，避免下游按帧算长度时多出半帧
            int frameBytes = channels * 2;
            int aligned = data.Length - data.Length % frameBytes;
            if (aligned != data.Length)
                Array.Resize(ref data, aligned);

            return new PcmAudio(data, sampleRate, channels);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debug.WriteLine($"[audio] MediaCodec 解码失败：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
        finally
        {
            try
            {
                codec?.Stop();
            }
            catch
            {
                // 没 Start 成功时 Stop 会抛，忽略
            }

            codec?.Release();
            codec?.Dispose();
            extractor?.Release();
            extractor?.Dispose();
        }
    }

    private static int ReadInt(MediaFormat format, string key, int fallback)
    {
        try
        {
            return format.ContainsKey(key) ? format.GetInteger(key) : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static byte[] FloatBufferToPcm16(byte[] data)
    {
        int count = data.Length / 4;
        var converted = new byte[count * 2];
        for (int i = 0; i < count; i++)
        {
            float sample = BitConverter.ToSingle(data, i * 4);
            short value = (short)Math.Clamp(Math.Round(sample * short.MaxValue), short.MinValue, short.MaxValue);
            converted[i * 2] = (byte)value;
            converted[i * 2 + 1] = (byte)(value >> 8);
        }

        return converted;
    }
}
