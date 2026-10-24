using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using SherpaOnnx;

namespace AsrProbe
{
    /// <summary>
    /// VoiceKill 蔚蓝版 —— 第 0 步探针。
    /// 目的：证明 C# 能加载和 MC 版同一批 ONNX 模型，并且能出字。
    /// 不接麦克风、不碰游戏，只喂 wav 文件。
    /// </summary>
    internal static class Program
    {
        private const int SampleRate = 16000;
        private static string _modelRoot;

        private static int Main(string[] args)
        {
            try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }

            _modelRoot = args.Length > 0
                ? args[0]
                : @"D:\.minecraft\versions\1.21.11-Fabric 0.18.4\voicekill\models";

            string wavDir = Path.Combine(AppContext.BaseDirectory, "test_wavs");
            if (args.Length > 1) wavDir = args[1];

            Console.WriteLine("==================================================");
            Console.WriteLine("  VoiceKill 蔚蓝版 · ASR 探针");
            Console.WriteLine("==================================================");
            Console.WriteLine("模型目录: " + _modelRoot);
            Console.WriteLine("测试音频: " + wavDir);
            Console.WriteLine();

            if (!Directory.Exists(_modelRoot))
            {
                Console.WriteLine("[FATAL] 模型目录不存在");
                return 1;
            }

            string zipformer = Path.Combine(_modelRoot, "zipformer-bilingual-small-int8");
            string sensevoice = Path.Combine(_modelRoot, "sensevoice-review-int8");
            string punct = Path.Combine(_modelRoot, "punctuation-zh-en-int8");

            if (!File.Exists(Path.Combine(zipformer, "encoder.int8.onnx")))
            {
                Console.WriteLine("[FATAL] 缺 encoder.int8.onnx");
                return 1;
            }

            string[] wavs = Directory.GetFiles(wavDir, "*.wav");
            Array.Sort(wavs, StringComparer.OrdinalIgnoreCase);
            if (wavs.Length == 0)
            {
                Console.WriteLine("[FATAL] test_wavs 里没有 wav 文件");
                return 1;
            }

            // ---------- 1. 加载流式引擎 ----------
            OnlineRecognizer recognizer = null;
            var sw = Stopwatch.StartNew();
            try
            {
                var cfg = new OnlineRecognizerConfig();
                cfg.FeatConfig.SampleRate = SampleRate;
                cfg.FeatConfig.FeatureDim = 80;
                cfg.ModelConfig.Transducer.Encoder = Path.Combine(zipformer, "encoder.int8.onnx");
                cfg.ModelConfig.Transducer.Decoder = Path.Combine(zipformer, "decoder.onnx");
                cfg.ModelConfig.Transducer.Joiner = Path.Combine(zipformer, "joiner.int8.onnx");
                cfg.ModelConfig.Tokens = Path.Combine(zipformer, "tokens.txt");
                cfg.ModelConfig.BpeVocab = Path.Combine(zipformer, "bpe.vocab");
                cfg.ModelConfig.NumThreads = 1;
                cfg.ModelConfig.Provider = "cpu";
                cfg.ModelConfig.Debug = 0;
                cfg.DecodingMethod = "greedy_search";
                cfg.MaxActivePaths = 4;

                // 内置端点规则：静音 0.6s 收尾；连续说话 8s 强制收尾
                cfg.EnableEndpoint = 1;
                cfg.Rule1MinTrailingSilence = 2.4f;
                cfg.Rule2MinTrailingSilence = 0.6f;
                cfg.Rule3MinUtteranceLength = 8.0f;

                recognizer = new OnlineRecognizer(cfg);
                sw.Stop();
                Console.WriteLine("[OK] 流式引擎加载完成 (" + sw.ElapsedMilliseconds + " ms)");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[FAIL] 流式引擎加载失败: " + ex.GetType().Name + ": " + ex.Message);
                return 1;
            }

            // ---------- 2. 加载复核引擎（失败不致命） ----------
            OfflineRecognizer review = null;
            try
            {
                var cfg = new OfflineRecognizerConfig();
                cfg.FeatConfig.SampleRate = SampleRate;
                cfg.FeatConfig.FeatureDim = 80;
                cfg.ModelConfig.SenseVoice.Model = Path.Combine(sensevoice, "model.int8.onnx");
                cfg.ModelConfig.SenseVoice.Language = "auto";
                cfg.ModelConfig.SenseVoice.UseInverseTextNormalization = 1;
                cfg.ModelConfig.Tokens = Path.Combine(sensevoice, "tokens.txt");
                cfg.ModelConfig.NumThreads = 2;
                cfg.ModelConfig.Provider = "cpu";
                cfg.DecodingMethod = "greedy_search";
                review = new OfflineRecognizer(cfg);
                Console.WriteLine("[OK] 复核引擎(SenseVoice)加载完成");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[WARN] 复核引擎不可用: " + ex.Message);
            }

            // ---------- 3. 加载标点（失败不致命） ----------
            OfflinePunctuation puncter = null;
            try
            {
                var cfg = new OfflinePunctuationConfig();
                cfg.Model.CtTransformer = Path.Combine(punct, "model.int8.onnx");
                cfg.Model.NumThreads = 1;
                cfg.Model.Provider = "cpu";
                puncter = new OfflinePunctuation(cfg);
                Console.WriteLine("[OK] 标点引擎加载完成");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[WARN] 标点引擎不可用: " + ex.Message);
            }

            Console.WriteLine();
            Console.WriteLine("--------------------------------------------------");

            // ---------- 4. 逐条识别 ----------
            int okCount = 0;
            foreach (string wav in wavs)
            {
                float[] samples;
                int rate;
                try
                {
                    samples = ReadWav(wav, out rate);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[SKIP] " + Path.GetFileName(wav) + " 读取失败: " + ex.Message);
                    continue;
                }

                if (rate != SampleRate)
                    samples = Resample(samples, rate, SampleRate);

                double dur = samples.Length / (double)SampleRate;
                Console.WriteLine();
                Console.WriteLine("### " + Path.GetFileName(wav) + "  (" + dur.ToString("0.00") + "s, " + samples.Length + " 采样)");

                // 流式（模拟 40ms 一块）
                string streamText = "";
                try
                {
                    var t = Stopwatch.StartNew();
                    OnlineStream stream = recognizer.CreateStream();
                    var sb = new StringBuilder();
                    int chunk = SampleRate * 40 / 1000;
                    for (int off = 0; off < samples.Length; off += chunk)
                    {
                        int n = Math.Min(chunk, samples.Length - off);
                        var buf = new float[n];
                        Array.Copy(samples, off, buf, 0, n);
                        stream.AcceptWaveform(SampleRate, buf);
                        while (recognizer.IsReady(stream)) recognizer.Decode(stream);

                        string partial = recognizer.GetResult(stream).Text;
                        if (!string.IsNullOrEmpty(partial) && partial != streamText)
                        {
                            streamText = partial;
                            sb.Append("   [流式] ").AppendLine(partial);
                        }
                    }
                    stream.AcceptWaveform(SampleRate, new float[SampleRate / 2]); // 补 0.5s 静音收尾
                    while (recognizer.IsReady(stream)) recognizer.Decode(stream);
                    streamText = recognizer.GetResult(stream).Text;
                    t.Stop();
                    if (sb.Length > 0) Console.Write(sb.ToString());
                    Console.WriteLine("   [最终] " + streamText + "   (" + t.ElapsedMilliseconds + " ms)");
                    if (!string.IsNullOrEmpty(streamText)) okCount++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("   [FAIL] 流式识别异常: " + ex.GetType().Name + ": " + ex.Message);
                }

                // 句末复核
                if (review != null)
                {
                    try
                    {
                        var t = Stopwatch.StartNew();
                        OfflineStream os = review.CreateStream();
                        os.AcceptWaveform(SampleRate, samples);
                        review.Decode(os);
                        string txt = os.Result.Text;
                        t.Stop();
                        Console.WriteLine("   [复核] " + txt + "   (" + t.ElapsedMilliseconds + " ms)");

                        if (puncter != null && !string.IsNullOrEmpty(streamText))
                            Console.WriteLine("   [标点] " + puncter.AddPunct(streamText));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("   [FAIL] 复核异常: " + ex.GetType().Name + ": " + ex.Message);
                    }
                }
            }

            Console.WriteLine();
            Console.WriteLine("--------------------------------------------------");
            Console.WriteLine(okCount > 0
                ? "结论：链路通，出字 " + okCount + "/" + wavs.Length + " 条。"
                : "结论：没出字，链路有问题。");
            return okCount > 0 ? 0 : 2;
        }

        // ---------------- WAV ----------------

        private static float[] ReadWav(string path, out int sampleRate)
        {
            byte[] data = File.ReadAllBytes(path);
            if (data.Length < 44 || Encoding.ASCII.GetString(data, 0, 4) != "RIFF")
                throw new InvalidDataException("不是 RIFF/WAV");

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

            if (dataOffset < 0) throw new InvalidDataException("没有 data 块");
            if (bits != 16) throw new InvalidDataException("只支持 16bit，当前 " + bits);

            int total = dataLen / 2;
            if (channels > 1) total /= channels;

            var samples = new float[total];
            for (int i = 0; i < total; i++)
            {
                int idx = dataOffset + i * 2 * channels;
                short s = BitConverter.ToInt16(data, idx);
                samples[i] = s / 32768f;
            }
            return samples;
        }

        private static float[] Resample(float[] input, int from, int to)
        {
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
}
