using System;
using System.IO;
using System.Text;

namespace VoiceKill.Core;

/// <summary>最小 WAV 读取。只用来自检，别拿它当播放器。</summary>
public static class WavReader
{
    public static float[] Read(string path, out int sampleRate)
    {
        byte[] data = File.ReadAllBytes(path);
        if (data.Length < 44 || Encoding.ASCII.GetString(data, 0, 4) != "RIFF")
            throw new InvalidDataException("不是 RIFF/WAV: " + path);

        short channels = 1;
        short bits = 16;
        sampleRate = 16000;
        int dataOffset = -1, dataLen = 0;

        int p = 12;
        while (p + 8 <= data.Length)
        {
            string id = Encoding.ASCII.GetString(data, p, 4);
            int size = BitConverter.ToInt32(data, p + 4);
            int body = p + 8;

            if (id == "fmt ")
            {
                channels = BitConverter.ToInt16(data, body + 2);
                sampleRate = BitConverter.ToInt32(data, body + 4);
                bits = BitConverter.ToInt16(data, body + 14);
            }
            else if (id == "data")
            {
                dataOffset = body;
                dataLen = Math.Min(size, data.Length - body);
                break;
            }

            p = body + size + (size & 1);
        }

        if (dataOffset < 0) throw new InvalidDataException("没有 data 块: " + path);
        if (bits != 16) throw new InvalidDataException("只支持 16bit，当前 " + bits);

        int total = dataLen / 2;
        if (channels > 1) total /= channels;

        var samples = new float[total];
        for (int i = 0; i < total; i++)
            samples[i] = BitConverter.ToInt16(data, dataOffset + i * 2 * channels) / 32768f;

        return samples;
    }

    public static float[] Resample(float[] input, int from, int to)
    {
        if (from == to) return input;
        int n = (int)Math.Round(input.Length * (double)to / from);
        var outBuf = new float[n];
        double step = (double)from / to;
        for (int i = 0; i < n; i++)
        {
            double src = i * step;
            int i0 = (int)src;
            int i1 = Math.Min(i0 + 1, input.Length - 1);
            double frac = src - i0;
            outBuf[i] = (float)(input[i0] * (1 - frac) + input[i1] * frac);
        }
        return outBuf;
    }
}
