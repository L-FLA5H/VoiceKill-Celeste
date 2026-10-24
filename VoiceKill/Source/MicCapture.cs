using System;
using System.Collections.Generic;
using NAudio.Wave;
using VoiceKill.Core;

namespace Celeste.Mod.VoiceKill;

/// <summary>
/// 音频采集。两种来源：
///   1. 麦克风        —— WaveInEvent（winmm），延迟低，16k/16bit/mono
///   2. 系统声音      —— WasapiLoopbackCapture，抓 OOPZ / QQ 通话里所有人的声音
///
/// 系统回环拿到的是 48k/32bit float/立体声，必须自己降采样到 16k 单声道，
/// 而且要先做低通（按比例做块平均），否则直接抽点会有混叠、识别率明显变差。
/// </summary>
public sealed class MicCapture : IDisposable
{
    private WaveInEvent _wave;
    private WasapiLoopbackCapture _loop;
    private readonly LoopConverter _converter = new LoopConverter();

    public int DeviceIndex { get; private set; }
    public string DeviceName { get; private set; } = "";
    public bool IsCapturing => _wave != null || _loop != null;

    /// <summary>最近一块音频的 RMS（0~1），面板用来画输入电平条。</summary>
    public float LastRms { get; private set; }
    public bool IsLoopback { get; private set; }

    public event Action<float[]> OnSamples;

    /// <summary>列出所有录音设备名（面板下拉框用）。</summary>
    public static List<string> DeviceNames()
    {
        var list = new List<string>();
        int count = WaveInEvent.DeviceCount;
        for (int i = 0; i < count; i++) list.Add(SafeName(i));
        return list;
    }

    private static string SafeName(int i)
    {
        try { return WaveInEvent.GetCapabilities(i).ProductName; }
        catch { return "设备" + i; }
    }

    /// <summary>按「名字优先」找设备：编号会因插拔而变化，名字不会。</summary>
    public static int ResolveDevice(string preferredName, int fallbackIndex, out string name)
    {
        int count = WaveInEvent.DeviceCount;
        name = "";

        if (!string.IsNullOrWhiteSpace(preferredName))
        {
            for (int i = 0; i < count; i++)
            {
                string product = SafeName(i);
                if (product.IndexOf(preferredName, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    name = product;
                    return i;
                }
            }
        }

        if (fallbackIndex >= 0 && fallbackIndex < count)
        {
            name = SafeName(fallbackIndex);
            return fallbackIndex;
        }

        if (count > 0)
        {
            name = SafeName(0);
            return 0;
        }

        return -1;
    }

    public bool Start(int deviceIndex, string deviceName, bool loopback, out string error)
    {
        error = null;
        Stop();

        if (loopback) return StartLoopback(out error);

        int index = ResolveDevice(deviceName, deviceIndex, out string resolved);
        if (index < 0)
        {
            error = "找不到任何录音设备";
            return false;
        }

        try
        {
            var wave = new WaveInEvent
            {
                WaveFormat = new WaveFormat(AsrEngine.SampleRate, 16, 1),
                BufferMilliseconds = 40,
                NumberOfBuffers = 4,
                DeviceNumber = index
            };

            wave.DataAvailable += (s, e) =>
            {
                int n = e.BytesRecorded / 2;
                if (n <= 0) return;
                var samples = new float[n];
                double sum = 0;
                for (int i = 0; i < n; i++)
                {
                    samples[i] = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
                    sum += samples[i] * samples[i];
                }
                LastRms = (float)Math.Sqrt(sum / Math.Max(1, n));
                try { OnSamples?.Invoke(samples); } catch { }
            };

            wave.RecordingStopped += (s, e) =>
            {
                if (e.Exception != null)
                    Logger.Log(LogLevel.Warn, "VoiceKill", "录音中断: " + e.Exception.Message);
            };

            wave.StartRecording();
            _wave = wave;
            DeviceIndex = index;
            DeviceName = resolved;
            IsLoopback = false;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private bool StartLoopback(out string error)
    {
        error = null;
        try
        {
            var loop = new WasapiLoopbackCapture();
            _converter.Reset(loop.WaveFormat);

            loop.DataAvailable += (s, e) =>
            {
                if (e.BytesRecorded <= 0) return;
                try
                {
                    float[] samples = _converter.Convert(e.Buffer, e.BytesRecorded);
                    if (samples != null && samples.Length > 0)
                    {
                        double sum = 0;
                        for (int i = 0; i < samples.Length; i++) sum += samples[i] * samples[i];
                        LastRms = (float)Math.Sqrt(sum / samples.Length);
                        OnSamples?.Invoke(samples);
                    }
                }
                catch { }
            };

            loop.RecordingStopped += (s, e) =>
            {
                if (e.Exception != null)
                    Logger.Log(LogLevel.Warn, "VoiceKill", "回环录音中断: " + e.Exception.Message);
            };

            loop.StartRecording();
            _loop = loop;
            DeviceIndex = -1;
            DeviceName = "系统声音（回环）";
            IsLoopback = true;
            return true;
        }
        catch (Exception ex)
        {
            error = "系统声音抓取失败: " + ex.Message;
            return false;
        }
    }

    public void Stop()
    {
        WaveInEvent wave = _wave;
        _wave = null;
        if (wave != null)
        {
            try { wave.StopRecording(); } catch { }
            try { wave.Dispose(); } catch { }
        }

        WasapiLoopbackCapture loop = _loop;
        _loop = null;
        if (loop != null)
        {
            try { loop.StopRecording(); } catch { }
            try { loop.Dispose(); } catch { }
        }
    }

    public void Dispose() => Stop();

    /// <summary>把回环音频（可能是 48k/float/立体声）转成 16k 单声道 float。</summary>
    private sealed class LoopConverter
    {
        private readonly List<float> _mono = new List<float>(8192);
        private int _channels = 2;
        private int _bits = 32;
        private bool _isFloat;
        private double _ratio = 3.0;
        private double _pos;

        public void Reset(WaveFormat fmt)
        {
            _channels = Math.Max(1, fmt.Channels);
            _bits = fmt.BitsPerSample;
            _isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat;
            _ratio = fmt.SampleRate / (double)AsrEngine.SampleRate;
            _mono.Clear();
            _pos = 0;
        }

        public float[] Convert(byte[] buffer, int bytes)
        {
            int bps = Math.Max(1, _bits / 8);
            int frame = bps * _channels;
            if (frame <= 0) return null;

            int frames = bytes / frame;
            for (int i = 0; i < frames; i++)
            {
                int off = i * frame;
                double sum = 0;
                for (int c = 0; c < _channels; c++)
                {
                    int p = off + c * bps;
                    if (_isFloat && bps == 4) sum += BitConverter.ToSingle(buffer, p);
                    else if (bps == 2) sum += BitConverter.ToInt16(buffer, p) / 32768.0;
                    else if (bps == 4) sum += BitConverter.ToInt32(buffer, p) / 2147483648.0;
                    else if (bps == 3)
                    {
                        int v = buffer[p] | (buffer[p + 1] << 8) | (buffer[p + 2] << 16);
                        if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
                        sum += v / 2147483648.0;
                    }
                }
                _mono.Add((float)(sum / _channels));
            }

            // 块平均降采样（顺带低通，防混叠）
            var outList = new List<float>(_mono.Count / 3 + 8);
            while (_pos + _ratio <= _mono.Count)
            {
                int start = (int)_pos;
                int end = (int)(_pos + _ratio);
                if (end <= start) end = start + 1;

                double acc = 0;
                int cnt = 0;
                for (int i = start; i < end && i < _mono.Count; i++) { acc += _mono[i]; cnt++; }
                outList.Add(cnt > 0 ? (float)(acc / cnt) : 0f);
                _pos += _ratio;
            }

            int drop = (int)_pos;
            if (drop > 0)
            {
                _mono.RemoveRange(0, Math.Min(drop, _mono.Count));
                _pos -= drop;
            }
            if (_mono.Count > 16000) _mono.RemoveRange(0, _mono.Count - 16000);

            return outList.ToArray();
        }
    }
}
