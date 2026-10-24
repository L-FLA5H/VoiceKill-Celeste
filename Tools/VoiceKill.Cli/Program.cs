using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using NAudio.Wave;
using VoiceKill.Core;

namespace VoiceKill.Cli
{
    internal static class Program
    {
        private const string DefaultModels =
            @"D:\.minecraft\versions\1.21.11-Fabric 0.18.4\voicekill\models";

        private static int Main(string[] args)
        {
            try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }

            string mode = "run";
            string models = DefaultModels;
            string wav = "";
            string force = "";
            string nativeDir = "";
            bool noPartial = false;
            int device = -1;
            int words = 8;
            double seconds = 0;
            double roundSeconds = 30;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--devices": mode = "devices"; break;
                    case "--selftest": mode = "selftest"; break;
                    case "--hud-preview": mode = "hud"; wav = Next(args, ref i); break;
                    case "--run": mode = "run"; break;
                    case "--wav": mode = "wav"; wav = Next(args, ref i); break;
                    case "--forcewords": force = Next(args, ref i); break;
                    case "--models": models = Next(args, ref i); break;
                    case "--native-dir": nativeDir = Next(args, ref i); break;
                    case "--no-partial": noPartial = true; break;
                    case "--device": int.TryParse(Next(args, ref i), out device); break;
                    case "--words": int.TryParse(Next(args, ref i), out words); break;
                    case "--seconds": double.TryParse(Next(args, ref i), out seconds); break;
                    case "--round": double.TryParse(Next(args, ref i), out roundSeconds); break;
                }
            }

            if (mode == "devices") { ListDevices(); return 0; }
            if (mode == "selftest") return SelfTest.Run();
            if (mode == "hud") return HudPreview.Run(string.IsNullOrEmpty(wav) ? "hud-preview.png" : wav);
            if (mode == "wav") return RunWav(models, wav, force, nativeDir, noPartial);
            return RunLive(models, device, words, seconds, roundSeconds, nativeDir);
        }

        private static string Next(string[] args, ref int i)
            => i + 1 < args.Length ? args[++i] : "";

        private static void ListDevices()
        {
            Console.WriteLine("=== 录音设备 ===");
            int count = WaveInEvent.DeviceCount;
            if (count == 0) { Console.WriteLine("(一个都没有)"); return; }
            for (int i = 0; i < count; i++)
            {
                var caps = WaveInEvent.GetCapabilities(i);
                Console.WriteLine($"  [{i}] {caps.ProductName}  ({caps.Channels} ch)");
            }
            Console.WriteLine();
            Console.WriteLine("用法: --run --device <编号>");
        }

        private static RuntimeOptions BaseOptions(string models, int words, double roundSeconds, string nativeDir = "")
        {
            var opt = new RuntimeOptions();
            opt.Asr.ModelRoot = models;
            opt.Asr.NativeLibraryDir = nativeDir ?? "";
            opt.Asr.NumThreads = 2;
            opt.Asr.EndpointSilence = 0.35f;
            opt.Asr.MaxUtteranceSeconds = 4f;
            opt.Words.WordsPerRound = words;
            opt.Words.NamesPerRound = 0;
            opt.Words.HighFreqPerRound = Math.Max(1, words / 3);
            opt.Words.SmartPickChance = 0.6;
            opt.RoundSeconds = roundSeconds;
            return opt;
        }

        private static void Wire(VoiceKillRuntime rt)
        {
            rt.OnLog += m => Console.WriteLine("  " + m);
            rt.OnError += m => Console.WriteLine("  ⚠ " + m);
            rt.OnRoundChanged += round =>
            {
                Console.WriteLine();
                Console.WriteLine("──────── 新一组危险词 ────────");
                var sb = new StringBuilder("  ");
                foreach (var w in round) sb.Append(w.Text).Append("  ");
                Console.WriteLine(sb.ToString());
                Console.WriteLine("──────────────────────────────");
            };
            rt.OnPartial += t => Console.Write("\r  🎤 " + t.PadRight(40));
            rt.OnUtterance += u =>
            {
                Console.Write("\r" + new string(' ', 46) + "\r");
                if (!string.IsNullOrEmpty(u.StreamingText))
                    Console.WriteLine("  📝 听到: " + u.DisplayText);
            };
            rt.OnDeath += v =>
            {
                Console.WriteLine();
                Console.WriteLine("  💀💀💀 你说了「" + v.Word.Text + "」（" + v.KindLabel + " / " + v.Via + "）——你死了");
                Console.WriteLine("       原话: " + (v.Utterance.StreamingText ?? ""));
                Console.WriteLine();
            };
        }

        /// <summary>把 wav 灌进完整流水线（引擎 + 判定），用来验证「说完就死」。</summary>
        private static int RunWav(string models, string wavPath, string forceWords, string nativeDir, bool noPartial = false)
        {
            Console.WriteLine("==================================================");
            Console.WriteLine("  VoiceKill 蔚蓝版 · wav 端到端验证");
            Console.WriteLine("==================================================");

            if (!System.IO.Directory.Exists(models)) { Console.WriteLine("❌ 模型目录不存在"); return 1; }
            if (!System.IO.File.Exists(wavPath)) { Console.WriteLine("❌ wav 不存在: " + wavPath); return 1; }

            float[] audio;
            int rate;
            try { audio = WavReader.Read(wavPath, out rate); }
            catch (Exception ex) { Console.WriteLine("❌ " + ex.Message); return 1; }
            if (rate != AsrEngine.SampleRate) audio = WavReader.Resample(audio, rate, AsrEngine.SampleRate);

            var opt = BaseOptions(models, 6, 999, nativeDir);
            if (noPartial) opt.MatchPartials = false;
            using var rt = new VoiceKillRuntime(opt);
            Wire(rt);

            var died = new ManualResetEventSlim(false);
            int fed = 0;
            double killedAt = -1;
            rt.OnDeath += v => { killedAt = fed / (double)AsrEngine.SampleRate; died.Set(); };

            var ready = new ManualResetEventSlim(false);
            rt.OnLog += m => { if (m.Contains("就绪")) ready.Set(); };

            rt.Start();
            if (!ready.Wait(20000)) { Console.WriteLine("❌ 引擎没起来"); return 1; }

            if (!string.IsNullOrWhiteSpace(forceWords))
            {
                var list = new List<string>();
                foreach (string s in forceWords.Split(',')) if (s.Trim().Length > 0) list.Add(s.Trim());
                rt.EnqueueManual(list);
                Console.WriteLine("  （已强制指定危险词: " + string.Join(" / ", list.ToArray()) + "）");
            }

            Console.WriteLine();
            Console.WriteLine("  ▶ 开始灌音频（" + (audio.Length / (double)AsrEngine.SampleRate).ToString("0.0") + "s，按实际速度播放）");

            int chunk = AsrEngine.SampleRate * 40 / 1000;
            for (int off = 0; off < audio.Length; off += chunk)
            {
                int n = Math.Min(chunk, audio.Length - off);
                var buf = new float[n];
                Array.Copy(audio, off, buf, 0, n);
                rt.Feed(buf);
                fed += n;
                Thread.Sleep(40);
                if (died.IsSet) break;
            }

            // 补一段静音，逼出最后一句话的端点
            if (!died.IsSet)
            {
                var silence = new float[AsrEngine.SampleRate / 10];
                for (int i = 0; i < 14 && !died.IsSet; i++)
                {
                    rt.Feed(silence);
                    Thread.Sleep(100);
                }
            }

            // 留时间让最后一句结算
            for (int i = 0; i < 20 && !died.IsSet; i++) Thread.Sleep(100);

            rt.Stop();
            Console.WriteLine();
            if (died.IsSet)
                Console.WriteLine($"判定发生在音频第 {killedAt:0.00}s（说完到判死看这个数落点）");
            Console.WriteLine(died.IsSet
                ? "结论：判定链路通 —— 说话内容命中危险词，成功触发死亡。"
                : "结论：这条音频没有命中危险词（如果 forcewords 里给的词确实在音频里，那就是漏判）。");
            return died.IsSet ? 0 : 2;
        }

        private static int RunLive(string models, int device, int words, double seconds, double roundSeconds, string nativeDir = "")
        {
            Console.WriteLine("==================================================");
            Console.WriteLine("  VoiceKill 蔚蓝版 · 命令行试玩");
            Console.WriteLine("==================================================");

            if (!System.IO.Directory.Exists(models))
            {
                Console.WriteLine("❌ 模型目录不存在: " + models);
                return 1;
            }

            var opt = BaseOptions(models, words, roundSeconds, nativeDir);
            using var rt = new VoiceKillRuntime(opt);
            Wire(rt);

            rt.Start();

            var ready = new ManualResetEventSlim(false);
            rt.OnLog += m => { if (m.Contains("就绪")) ready.Set(); };
            ready.Wait(20000);

            var waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(AsrEngine.SampleRate, 16, 1),
                BufferMilliseconds = 40,
                NumberOfBuffers = 4,
                DeviceNumber = device < 0 ? 0 : device
            };

            waveIn.DataAvailable += (s, e) =>
            {
                int n = e.BytesRecorded / 2;
                if (n <= 0) return;
                var samples = new float[n];
                for (int i = 0; i < n; i++)
                    samples[i] = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
                rt.Feed(samples);
            };

            waveIn.RecordingStopped += (s, e) =>
            {
                if (e.Exception != null) Console.WriteLine("  ⚠ 录音中断: " + e.Exception.Message);
            };

            try { waveIn.StartRecording(); }
            catch (Exception ex)
            {
                Console.WriteLine("❌ 打不开麦克风: " + ex.Message);
                Console.WriteLine("   先跑 --devices 看设备编号，再用 --device <编号>");
                return 1;
            }

            Console.WriteLine();
            Console.WriteLine("  🎙 正在听（设备 #" + waveIn.DeviceNumber + "）");
            Console.WriteLine("     说出屏幕上的词就会被判死。Ctrl+C 退出。");
            Console.WriteLine();

            var start = DateTime.Now;
            try
            {
                while (true)
                {
                    Thread.Sleep(200);
                    if (seconds > 0 && (DateTime.Now - start).TotalSeconds >= seconds) break;
                }
            }
            catch (Exception) { }

            try { waveIn.StopRecording(); } catch { }
            rt.Stop();
            Console.WriteLine();
            Console.WriteLine("结束。丢块数: " + rt.DroppedBlocks);
            return 0;
        }
    }
}
