using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using SherpaOnnx;

namespace VoiceKill.Core;

public sealed class AsrOptions
{
    /// <summary>models 目录（含 zipformer-bilingual-small-int8 等子目录）。</summary>
    public string ModelRoot = "";

    /// <summary>
    /// 放着 onnxruntime.dll / sherpa-onnx-c-api.dll 的目录。
    /// 非空时会先强制按绝对路径加载原生库（游戏内必须设，否则找不到依赖）。
    /// </summary>
    public string NativeLibraryDir = "";

    public int NumThreads = 2;

    /// <summary>静音多久算一句结束（秒）。蔚蓝节奏快，默认 0.35。</summary>
    public float EndpointSilence = 0.35f;

    /// <summary>完全不说话多久算一句（秒）。</summary>
    public float Rule1Silence = 2.0f;

    /// <summary>一句话最长多少秒后强制结算，防止玩家一直说个不停永远不判。</summary>
    public float MaxUtteranceSeconds = 4.0f;

    /// <summary>句末复核。⚠️ 走后台线程，不会拖慢主链路。</summary>
    public bool EnableReview = true;

    /// <summary>标点。只用于显示，同样在后台线程。</summary>
    public bool EnablePunctuation = true;

    /// <summary>低于这个音量就当成静音，直接丢掉，避免底噪被识别成「啊」。</summary>
    public float SilenceRms = 0.004f;
}

/// <summary>
/// 识别引擎封装。
///
/// 线程模型（这是延迟的关键）：
///   - Feed / 流式解码 / 端点判定 —— 调用方线程（自己的后台线程）
///   - SenseVoice 复核 + 标点      —— 独立的 review 线程，**永远不挡主链路**
///
/// 以前复核是同步跑的，一句要 1.4 秒，把流水线堵死，越说越积压。
/// </summary>
public sealed class AsrEngine : IDisposable
{
    public const int SampleRate = 16000;

    private readonly AsrOptions _opt;
    private readonly OnlineRecognizer _recognizer;
    private readonly OfflineRecognizer _review;
    private readonly OfflinePunctuation _punct;

    private readonly List<float> _segment = new List<float>(SampleRate * 8);
    private readonly int _maxSegmentSamples;

    private OnlineStream _stream;
    private string _lastPartial = "";
    private float _segmentPeakRms;
    private bool _disposed;

    private readonly BlockingCollection<ReviewJob> _reviewQueue =
        new BlockingCollection<ReviewJob>(new ConcurrentQueue<ReviewJob>(), 3);
    private Thread _reviewThread;
    private long _utteranceSeq;

    /// <summary>一句说完立刻触发（不等复核，这是低延迟的关键）。</summary>
    public event Action<Utterance> OnUtterance;

    /// <summary>后台复核/标点完成后再补一次。</summary>
    public event Action<Utterance> OnReviewed;

    /// <summary>说话过程中的临时文本。</summary>
    public event Action<string> OnPartial;

    private sealed class ReviewJob
    {
        public Utterance Utterance;
        public float[] Audio;
    }

    public AsrEngine(AsrOptions opt)
    {
        _opt = opt ?? throw new ArgumentNullException(nameof(opt));

        // 必须在碰任何 SherpaOnnx 类型之前把原生库准备好。
        // 否则会在终结器线程上抛 DllNotFoundException —— 那是整个进程级别的崩，
        // 表现就是「游戏卡死」（2026-10-22 真事故）。
        if (!string.IsNullOrEmpty(opt.NativeLibraryDir) &&
            !NativeBootstrap.EnsureLoaded(opt.NativeLibraryDir, out string nativeError))
        {
            throw new InvalidOperationException("原生识别库加载失败: " + nativeError);
        }

        if (string.IsNullOrEmpty(opt.ModelRoot) || !Directory.Exists(opt.ModelRoot))
            throw new DirectoryNotFoundException("模型目录不存在: " + opt.ModelRoot);

        string zipformer = Path.Combine(opt.ModelRoot, "zipformer-bilingual-small-int8");
        string sensevoice = Path.Combine(opt.ModelRoot, "sensevoice-review-int8");
        string punctDir = Path.Combine(opt.ModelRoot, "punctuation-zh-en-int8");

        var cfg = new OnlineRecognizerConfig();
        cfg.FeatConfig.SampleRate = SampleRate;
        cfg.FeatConfig.FeatureDim = 80;
        cfg.ModelConfig.Transducer.Encoder = Path.Combine(zipformer, "encoder.int8.onnx");
        cfg.ModelConfig.Transducer.Decoder = Path.Combine(zipformer, "decoder.onnx");
        cfg.ModelConfig.Transducer.Joiner = Path.Combine(zipformer, "joiner.int8.onnx");
        cfg.ModelConfig.Tokens = Path.Combine(zipformer, "tokens.txt");
        cfg.ModelConfig.BpeVocab = Path.Combine(zipformer, "bpe.vocab");
        cfg.ModelConfig.NumThreads = Math.Max(1, opt.NumThreads);
        cfg.ModelConfig.Provider = "cpu";
        cfg.DecodingMethod = "greedy_search";
        cfg.MaxActivePaths = 4;

        // 端点规则：一句话说完静音 0.35s 就结算；一直说不停 4s 也结算
        cfg.EnableEndpoint = 1;
        cfg.Rule1MinTrailingSilence = opt.Rule1Silence;
        cfg.Rule2MinTrailingSilence = opt.EndpointSilence;
        cfg.Rule3MinUtteranceLength = opt.MaxUtteranceSeconds;

        _recognizer = new OnlineRecognizer(cfg);
        _maxSegmentSamples = (int)(opt.MaxUtteranceSeconds * SampleRate) + SampleRate;

        bool wantReview = opt.EnableReview && File.Exists(Path.Combine(sensevoice, "model.int8.onnx"));
        if (wantReview)
        {
            try
            {
                var rc = new OfflineRecognizerConfig();
                rc.FeatConfig.SampleRate = SampleRate;
                rc.FeatConfig.FeatureDim = 80;
                rc.ModelConfig.SenseVoice.Model = Path.Combine(sensevoice, "model.int8.onnx");
                rc.ModelConfig.SenseVoice.Language = "auto";
                rc.ModelConfig.SenseVoice.UseInverseTextNormalization = 1;
                rc.ModelConfig.Tokens = Path.Combine(sensevoice, "tokens.txt");
                rc.ModelConfig.NumThreads = Math.Max(1, opt.NumThreads);
                rc.ModelConfig.Provider = "cpu";
                rc.DecodingMethod = "greedy_search";
                _review = new OfflineRecognizer(rc);
            }
            catch { _review = null; }
        }

        if (opt.EnablePunctuation && File.Exists(Path.Combine(punctDir, "model.int8.onnx")))
        {
            try
            {
                var pc = new OfflinePunctuationConfig();
                pc.Model.CtTransformer = Path.Combine(punctDir, "model.int8.onnx");
                pc.Model.NumThreads = 1;
                pc.Model.Provider = "cpu";
                _punct = new OfflinePunctuation(pc);
            }
            catch { _punct = null; }
        }

        if (_review != null || _punct != null)
        {
            _reviewThread = new Thread(ReviewLoop) { IsBackground = true, Name = "VoiceKill.Review" };
            _reviewThread.Start();
        }
    }

    /// <summary>喂一块 16kHz mono float 音频（建议 40ms = 640 采样）。</summary>
    public void Feed(float[] samples, int count = -1)
    {
        if (_disposed || samples == null) return;
        if (count < 0 || count > samples.Length) count = samples.Length;
        if (count == 0) return;

        float[] chunk = count == samples.Length ? samples : Slice(samples, count);

        float rms = Rms(chunk);
        if (rms > _segmentPeakRms) _segmentPeakRms = rms;

        _segment.AddRange(chunk);
        if (_segment.Count > _maxSegmentSamples)
            _segment.RemoveRange(0, _segment.Count - _maxSegmentSamples);

        // ⚠️ 静音也必须喂给识别器 —— 端点判定靠的就是尾部静音。
        // 以前这里直接 return 丢掉静音，结果「说完了也不结算」，
        // 只能等 MaxUtteranceSeconds 强制截断，延迟直接爆炸（2026-10-22 实测）。
        // 安静时要不要判，交给 FinalizeSegment 里的「峰值音量」闸门决定。
        OnlineStream stream = Stream;
        stream.AcceptWaveform(SampleRate, chunk);
        while (_recognizer.IsReady(stream)) _recognizer.Decode(stream);

        // GetResult 在异常状态下可能返回 null，直接 .Text 会 NRE（用户 2026-10-24 就撞上了）
        string partial = "";
        try { partial = _recognizer.GetResult(stream)?.Text ?? ""; }
        catch { partial = ""; }
        if (!string.IsNullOrEmpty(partial) && partial != _lastPartial)
        {
            _lastPartial = partial;
            OnPartial?.Invoke(partial);
        }

        if (_recognizer.IsEndpoint(stream)) FinalizeSegment();
        else if (_segment.Count >= (int)(_opt.MaxUtteranceSeconds * SampleRate)) FinalizeSegment();
    }

    private OnlineStream Stream => _stream ??= _recognizer.CreateStream();

    /// <summary>强制把当前这段结算掉。</summary>
    public void Flush()
    {
        if (_disposed) return;
        FinalizeSegment();
    }

    /// <summary>清空所有缓冲（死亡后调用，防止上一句残留把复活瞬间的人再带走）。</summary>
    public void Reset()
    {
        if (_disposed) return;
        try { _recognizer.Reset(Stream); } catch { }
        _segment.Clear();
        _lastPartial = "";
        _segmentPeakRms = 0f;
    }

    private void FinalizeSegment()
    {
        string text = "";
        try { text = _recognizer.GetResult(Stream)?.Text ?? ""; } catch { }
        text = text.Trim();

        // 一段里最大音量都没超过阈值 → 认为是底噪，不判
        bool tooQuiet = _segmentPeakRms < _opt.SilenceRms;

        // 先留一份音频给后台复核，再把流清干净 —— 顺序不能反
        float[] audio = _segment.Count >= SampleRate / 4 ? _segment.ToArray() : null;
        Reset();

        if (string.IsNullOrEmpty(text) || tooQuiet) return;

        var utt = new Utterance
        {
            Id = Interlocked.Increment(ref _utteranceSeq),
            StreamingText = text,
            DisplayText = text
        };

        OnUtterance?.Invoke(utt);

        if (audio != null && (_review != null || _punct != null))
        {
            // 队列满了就丢最老的，宁可少一次复核，也不让积压拖慢当前这一句
            var job = new ReviewJob { Utterance = utt, Audio = audio };
            if (!_reviewQueue.TryAdd(job))
            {
                try { _reviewQueue.TryTake(out _); } catch { }
                try { _reviewQueue.TryAdd(job); } catch { }
            }
        }
    }

    private void ReviewLoop()
    {
        foreach (ReviewJob job in _reviewQueue.GetConsumingEnumerable())
        {
            if (_disposed) break;
            Utterance utt = job.Utterance;

            if (_review != null)
            {
                try
                {
                    OfflineStream os = _review.CreateStream();
                    os.AcceptWaveform(SampleRate, job.Audio);
                    _review.Decode(os);
                    utt.ReviewText = (os.Result.Text ?? "").Trim();
                }
                catch { }
            }

            if (_punct != null && !string.IsNullOrEmpty(utt.StreamingText))
            {
                try { utt.DisplayText = _punct.AddPunct(utt.StreamingText); }
                catch { utt.DisplayText = utt.StreamingText; }
            }

            try { OnReviewed?.Invoke(utt); } catch { }
        }
    }

    private static float[] Slice(float[] src, int count)
    {
        var dst = new float[count];
        Array.Copy(src, dst, count);
        return dst;
    }

    private static float Rms(float[] data)
    {
        double sum = 0;
        for (int i = 0; i < data.Length; i++) sum += data[i] * data[i];
        return (float)Math.Sqrt(sum / Math.Max(1, data.Length));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _reviewQueue.CompleteAdding(); } catch { }
        try { _reviewThread?.Join(1200); } catch { }

        try { _stream?.Dispose(); } catch { }
        try { _review?.Dispose(); } catch { }
        try { _punct?.Dispose(); } catch { }
        try { _recognizer?.Dispose(); } catch { }
    }
}
