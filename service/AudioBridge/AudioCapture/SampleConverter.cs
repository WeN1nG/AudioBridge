using System;
using AudioBridge.Utils;

namespace AudioBridge.AudioCapture;

public static class SampleConverter
{
    // Pre-attenuation factor: keep summed multi-source audio from hitting 0dBFS.
    // The system mixer sums N streams as float samples; when multiple apps play
    // simultaneously the peak can exceed 1.0f.  Rather than dynamic limiting
    // (which introduces harmonic distortion and gain pumping), apply a fixed
    // headroom.  -6dB (0.5x) covers ~2 simultaneous full-scale sources; adjust
    // via SetPreAttenuationDb() if more headroom is needed.
    private static float _preAttenuationFactor = 0.5f; // -6dB default

    public static void SetPreAttenuationDb(double db)
    {
        _preAttenuationFactor = (float)Math.Pow(10.0, db / 20.0);
    }

    public static byte[] ConvertFloatToInt(byte[] input, int targetBitsPerSample)
    {
        if (targetBitsPerSample != 16 && targetBitsPerSample != 32)
        {
            Logger.Warn($"不支持的位深: {targetBitsPerSample}，返回原始数据");
            return input;
        }

        int sampleCount = input.Length / 4;
        int outBytesPerSample = targetBitsPerSample / 8;
        byte[] output = new byte[sampleCount * outBytesPerSample];

        for (int i = 0; i < sampleCount; i++)
        {
            // Apply fixed pre-attenuation then hard-clamp as safety net
            float sample = BitConverter.ToSingle(input, i * 4) * _preAttenuationFactor;
            if (sample > 1.0f) sample = 1.0f;
            else if (sample < -1.0f) sample = -1.0f;

            if (targetBitsPerSample == 32)
            {
                int intSample = (int)(sample * 2147483647f);
                int offset = i * 4;
                output[offset]     = (byte)intSample;
                output[offset + 1] = (byte)(intSample >> 8);
                output[offset + 2] = (byte)(intSample >> 16);
                output[offset + 3] = (byte)(intSample >> 24);
            }
            else // 16-bit
            {
                short shortSample = (short)(sample * 32767f);
                int offset = i * 2;
                output[offset]     = (byte)shortSample;
                output[offset + 1] = (byte)(shortSample >> 8);
            }
        }

        return output;
    }

    /// <summary>
    /// Float → int conversion with sample rate conversion (linear interpolation).
    /// Handles upsampling and downsampling between any two sample rates.
    /// Input format: interleaved float (4 bytes per sample), e.g. [L0,R0,L1,R1,...].
    /// Output format: interleaved 16/32-bit int, same channel layout.
    /// </summary>
    public static byte[] ConvertFloatToIntWithSRC(
        byte[] input, int inputSampleRate, int outputSampleRate,
        int channels, int targetBitsPerSample)
    {
        int inputSampleCount = input.Length / 4;
        int inputFrameCount = inputSampleCount / channels;

        if (inputFrameCount == 0 || channels == 0) return Array.Empty<byte>();

        // outputFrameCount = inputFrameCount * outputSampleRate / inputSampleRate
        double ratio = (double)outputSampleRate / inputSampleRate;
        int outputFrameCount = (int)(inputFrameCount * ratio);
        if (outputFrameCount < 1) outputFrameCount = 1;

        int outBytesPerSample = targetBitsPerSample / 8;
        byte[] output = new byte[outputFrameCount * channels * outBytesPerSample];

        float[] inputFloats = new float[inputSampleCount];
        Buffer.BlockCopy(input, 0, inputFloats, 0, input.Length);

        for (int outFrame = 0; outFrame < outputFrameCount; outFrame++)
        {
            // Map output frame position back to input frame position (float)
            double inPos = (double)outFrame * inputFrameCount / outputFrameCount;
            int inFrameFloor = (int)inPos;
            int inFrameCeil = Math.Min(inFrameFloor + 1, inputFrameCount - 1);
            float frac = (float)(inPos - inFrameFloor);

            for (int ch = 0; ch < channels; ch++)
            {
                float sample0 = inputFloats[inFrameFloor * channels + ch] * _preAttenuationFactor;
                float sample1 = inputFloats[inFrameCeil * channels + ch] * _preAttenuationFactor;

                // Linear interpolation
                float sample = sample0 + (sample1 - sample0) * frac;
                if (sample > 1.0f) sample = 1.0f;
                else if (sample < -1.0f) sample = -1.0f;

                int outIdx = (outFrame * channels + ch) * outBytesPerSample;

                if (targetBitsPerSample == 32)
                {
                    int intSample = (int)(sample * 2147483647f);
                    output[outIdx]     = (byte)intSample;
                    output[outIdx + 1] = (byte)(intSample >> 8);
                    output[outIdx + 2] = (byte)(intSample >> 16);
                    output[outIdx + 3] = (byte)(intSample >> 24);
                }
                else // 16-bit
                {
                    short shortSample = (short)(sample * 32767f);
                    output[outIdx]     = (byte)shortSample;
                    output[outIdx + 1] = (byte)(shortSample >> 8);
                }
            }
        }

        return output;
    }

    /// <summary>
    /// Convert between integer PCM bit depths (e.g. 24-bit → 32-bit, 16-bit → 32-bit, etc.).
    /// Input/output are interleaved PCM in little-endian byte order.
    /// </summary>
    public static byte[] ConvertIntToInt(byte[] input, int srcBitsPerSample, int dstBitsPerSample)
    {
        if (srcBitsPerSample == dstBitsPerSample) return input;
        if (srcBitsPerSample is not (8 or 16 or 24 or 32) || dstBitsPerSample is not (8 or 16 or 24 or 32))
        {
            Logger.Warn($"不支持的位深转换: {srcBitsPerSample}→{dstBitsPerSample}，返回原始数据");
            return input;
        }

        int srcBytesPerSample = srcBitsPerSample / 8;
        int dstBytesPerSample = dstBitsPerSample / 8;
        int sampleCount = input.Length / srcBytesPerSample;
        byte[] output = new byte[sampleCount * dstBytesPerSample];

        int shift = dstBitsPerSample - srcBitsPerSample; // positive = upsample

        for (int i = 0; i < sampleCount; i++)
        {
            int srcOffset = i * srcBytesPerSample;

            // Read source sample (sign-extend from variable bit depth)
            int sample;
            if (srcBitsPerSample == 8)
            {
                // 8-bit is unsigned in PCM
                sample = (input[srcOffset] - 128) << 24;
            }
            else if (srcBitsPerSample == 16)
            {
                sample = (short)(input[srcOffset] | (input[srcOffset + 1] << 8));
            }
            else if (srcBitsPerSample == 24)
            {
                sample = input[srcOffset] | (input[srcOffset + 1] << 8) | (input[srcOffset + 2] << 16);
                if ((sample & 0x800000) != 0) sample |= unchecked((int)0xFF000000);
            }
            else // 32-bit
            {
                sample = input[srcOffset] | (input[srcOffset + 1] << 8) | (input[srcOffset + 2] << 16) | (input[srcOffset + 3] << 24);
            }

            // Shift to target bit depth (left-shift for upsampling, right-shift for downsampling)
            if (shift > 0)
                sample <<= shift;
            else if (shift < 0)
                sample >>= -shift;

            // Write to output
            int dstOffset = i * dstBytesPerSample;
            if (dstBytesPerSample >= 1) output[dstOffset] = (byte)sample;
            if (dstBytesPerSample >= 2) output[dstOffset + 1] = (byte)(sample >> 8);
            if (dstBytesPerSample >= 3) output[dstOffset + 2] = (byte)(sample >> 16);
            if (dstBytesPerSample >= 4) output[dstOffset + 3] = (byte)(sample >> 24);
        }

        return output;
    }
}
