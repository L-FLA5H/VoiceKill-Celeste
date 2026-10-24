using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace VoiceKill.Core;

public sealed class Verdict
{
    public DangerWord Word;
    public EvidenceKind Kind;
    public Utterance Utterance;
    public DateTime At = DateTime.Now;

    /// <summary>命中路径：partial（边说边判）/ utterance（整句）/ review（后台复核）。</summary>
    public string Via = "utterance";

    public string KindLabel
    {
        get
        {
            switch (Kind)
            {
                case EvidenceKind.Exact: return "原词";
                case EvidenceKind.ReviewExact: return "复核";
                case EvidenceKind.Alias: return "谐音";
                case EvidenceKind.Fuzzy: return "近似";
                default: return "?";
            }
        }
    }

    public override string ToString() => Word + " (" + Kind + ")";
}

public sealed class RuntimeOptions
{
    public AsrOptions Asr = new AsrOptions();
    public DangerWordConfig Words = new DangerWordConfig();

    /// <summary>多少秒换一组词。蔚蓝建议按房间换，这个当兜底。</summary>
    public double RoundSeconds = 30;

    public bool AllowAlias = true;
    public bool AllowFuzzy = true;

    /// <summary>死亡后静音多久，避免复活瞬间被上一句残留带走。</summary>
    public double DeathCooldownSeconds = 1.2;

    /// <summary>队列积压超过这么多块就丢，宁可漏判也不能让延迟越滚越大。</summary>
    public int MaxQueuedBlocks = 40;

    /// <summary>
    /// 边说边判：句子还没说完就检查原词精确命中。
    /// 这是把延迟从「等静音 0.35s」压到「一百多毫秒」的关键。
    /// 只做精确档，谐音/模糊仍然等整句结算。
    /// </summary>
    public bool MatchPartials = true;

    /// <summary>
    /// 离线同步种子：非 0 时，换词不再看「上次换词过了多久」，
    /// 而是看绝对时间片（unix 时间 ÷ 换词间隔）。
    /// 同一口令 + 同一时间片 → 所有人拿到同一组词，不需要任何网络。
    /// </summary>
    public int SyncSeed = 0;

    /// <summary>
    /// true = 词表只由外部给（房间模式的客户端就是这样）。
    /// 否则客户端自己也会按计时器换词，然后 0.8 秒后被主机的词覆盖，
    /// 中间会闪一下错的词 —— 多人同房时这就是「词不同步」的根源。
    /// </summary>
    public bool ExternalOnly = false;

    /// <summary>
    /// 同一个词要在临时文本里连续命中几次才算数。
    /// 0 = 一次就判（默认）。
    /// 实测：说话时临时文本一直在变长，一个词往往只在一两帧里存在，
    /// 门槛设 1 就基本永远等不到（2026-10-22 实测：命中全走 utterance，延迟没降）。
    /// 嫌误杀多就调到 1。
    /// </summary>
    public int PartialStableTicks = 0;
}

/// <summary>
/// 整条流水线的运行时。
///
/// 铁律（血泪）：所有识别和匹配都在**自己的后台线程**上跑。
/// 游戏里调用 Feed 的可以是麦克风回调线程，但绝不能让 Update/Render 直接干活。
/// </summary>
public sealed class VoiceKillRuntime : IDisposable
{
    private readonly RuntimeOptions _opt;
    private readonly BlockingCollection<float[]> _queue = new BlockingCollection<float[]>(256);
    private readonly SpeechStats _stats = new SpeechStats();
    private readonly SpeechWindow _window = new SpeechWindow();
    private readonly DangerWordManager _words;

    private AsrEngine _engine;
    private Thread _worker;
    private volatile bool _running;
    private volatile IReadOnlyList<DangerWord> _current = Array.Empty<DangerWord>();
    private DateTime _lastRotate = DateTime.MinValue;
    private DateTime _lastDeath = DateTime.MinValue;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private volatile bool _resetPending;
    private long _matchedId;
    private long _syncBucket = long.MinValue;
    private int _feedErrors;
    private long _lastUtteranceId;
    private string _partialWord = "";
    private int _partialHits;
    private int _rotateFlag;
    private List<KeyValuePair<string, int>> _prevTop = new List<KeyValuePair<string, int>>();

    public event Action<Utterance> OnUtterance;
    public event Action<string> OnPartial;
    public event Action<Verdict> OnDeath;
    public event Action<IReadOnlyList<DangerWord>> OnRoundChanged;
    public event Action<string> OnError;
    public event Action<string> OnLog;

    public IReadOnlyList<DangerWord> CurrentWords => _current;

    /// <summary>本周期高频词（面板显示用）。</summary>
    public List<KeyValuePair<string, int>> CurrentTop(int max = 12)
    {
        var list = new List<KeyValuePair<string, int>>();
        foreach (string w in _stats.Candidates(max))
            list.Add(new KeyValuePair<string, int>(w, Math.Max(1, _stats.RoundCount(w))));
        return list;
    }

    /// <summary>上一周期高频词（换词瞬间拍的快照）。</summary>
    public List<KeyValuePair<string, int>> PrevTop => _prevTop;

    /// <summary>往当前这一轮里加一个词（面板「往本轮加一个词」）。</summary>
    public void AddWordToRound(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        string t = text.Trim();
        var list = new List<DangerWord>(_current);
        foreach (DangerWord w in list) if (w.Text == t) return;
        list.Add(new DangerWord(t, WordSource.Manual));
        _current = list;
        OnRoundChanged?.Invoke(list);
    }

    /// <summary>从当前这一轮里删掉一个词。</summary>
    public void RemoveWordFromRound(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        string t = text.Trim();
        var list = new List<DangerWord>();
        bool removed = false;
        foreach (DangerWord w in _current)
        {
            if (w.Text == t) { removed = true; continue; }
            list.Add(w);
        }
        if (!removed) return;
        _current = list;
        OnRoundChanged?.Invoke(list);
    }

    /// <summary>
    /// 拿一句话试一下会不会死（面板的「模拟一句话」）。
    /// 用当前这一轮的词走完整匹配逻辑，但不触发死亡。
    /// </summary>
    public MatchResult Simulate(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return MatchResult.None;
        return WordMatcher.Find(_current, text, text, text, _opt.AllowAlias, _opt.AllowFuzzy);
    }

    /// <summary>还有几秒换下一组（面板显示用）。</summary>
    public int SecondsUntilRotate
    {
        get
        {
            if (_lastRotate == DateTime.MinValue) return 0;
            double left = _opt.RoundSeconds - (DateTime.Now - _lastRotate).TotalSeconds;
            return left <= 0 ? 0 : (int)Math.Ceiling(left);
        }
    }

    /// <summary>运行时改参数（面板上用，不用重启）。</summary>
    public void Reconfigure(Action<DangerWordConfig> words, double roundSeconds)
    {
        if (words != null) words(_opt.Words);
        if (roundSeconds > 0) _opt.RoundSeconds = roundSeconds;
    }

    /// <summary>
    /// 直接套用外部给的一组词（房间模式：跟主机走）。
    /// 不走组词器，也不消费手动词队列。
    /// </summary>
    public void ApplyExternalRound(IReadOnlyList<string> texts)
    {
        if (texts == null || texts.Count == 0) return;

        var list = new List<DangerWord>(texts.Count);
        foreach (string t in texts)
        {
            if (string.IsNullOrWhiteSpace(t)) continue;
            list.Add(new DangerWord(t.Trim(), WordSource.Random));
        }
        if (list.Count == 0) return;

        _current = list;
        _lastRotate = DateTime.Now;
        OnRoundChanged?.Invoke(list);
    }
    public SpeechStats Stats => _stats;
    public bool IsRunning => _running;
    public int DroppedBlocks { get; private set; }

    public VoiceKillRuntime(RuntimeOptions opt)
    {
        _opt = opt ?? new RuntimeOptions();
        _words = new DangerWordManager(_opt.Words, _stats);
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "VoiceKill.ASR" };
        _worker.Start();
    }

    /// <summary>喂音频。16kHz mono float，建议 40ms = 640 采样一块。</summary>
    public void Feed(float[] samples, int count = -1)
    {
        if (!_running || samples == null) return;
        if (count < 0 || count > samples.Length) count = samples.Length;
        if (count == 0) return;

        if (_queue.Count >= _opt.MaxQueuedBlocks)
        {
            DroppedBlocks++;
            return;
        }

        float[] copy = new float[count];
        Array.Copy(samples, copy, count);
        try { _queue.TryAdd(copy); } catch { }
    }

    /// <summary>立刻换一组词（进新房间、过图、手动点按钮都调这个）。</summary>
    public void RotateNow()
    {
        _queue.TryAdd(Array.Empty<float>());
        Interlocked.Exchange(ref _rotateFlag, 1);
    }

    /// <summary>清空「下一轮」预填的词。</summary>
    public void ClearManual() => _words.ClearManual();

    /// <summary>指定「下一轮」的词（网页上预定，或自检用）。</summary>
    public void EnqueueManual(IEnumerable<string> words)
    {
        _words.EnqueueManual(words);
        RotateNow();
    }

    /// <summary>玩家死了之后调一次，清掉残留。</summary>
    public void OnPlayerDied()
    {
        _lastDeath = DateTime.Now;
        _window.Clear();
        _partialWord = "";
        _partialHits = 0;
    }

    private void WorkerLoop()
    {
        OnLog?.Invoke("正在加载识别模型...");
        try
        {
            _engine = new AsrEngine(_opt.Asr);
        }
        catch (Exception ex)
        {
            OnError?.Invoke("模型加载失败: " + ex.Message);
            _running = false;
            return;
        }

        _engine.OnPartial += t =>
        {
            OnPartial?.Invoke(t);
            TryPartialMatch(t);
        };
        _engine.OnUtterance += HandleUtterance;
        _engine.OnReviewed += HandleReviewed;

        // 先把第一组词发出来，再宣布「就绪」
        DoRotate();
        _lastRotate = DateTime.Now;

        OnLog?.Invoke("识别引擎就绪");

        while (_running)
        {
            float[] block;
            bool got = false;
            try { got = _queue.TryTake(out block, 30); }
            catch (ObjectDisposedException) { break; }
            catch (InvalidOperationException) { break; }

            if (Interlocked.Exchange(ref _rotateFlag, 0) == 1) DoRotate();

            if (!got)
            {
                TickRotate();
                continue;
            }

            if (block == null || block.Length == 0) continue;

            try { _engine.Feed(block); }
            catch (Exception ex)
            {
                // 带完整堆栈，否则只看到 "Object reference..." 根本没法查
                _feedErrors++;
                if (_feedErrors <= 3 || _feedErrors % 100 == 0)
                    OnError?.Invoke($"识别异常（第 {_feedErrors} 次）: " + ex);
            }

            // 重置必须等 Feed 出来再做：在回调里直接 Reset native 流会重入
            if (_resetPending)
            {
                _resetPending = false;
                try { _engine.Reset(); } catch { }
            }
        }

        try { _engine?.Dispose(); } catch { }
    }

    /// <summary>空闲时决定要不要换词：离线同步用绝对时间片，否则用秒表。</summary>
    private void TickRotate()
    {
        if (_opt.SyncSeed != 0 && _opt.RoundSeconds > 0)
        {
            long bucket = (long)(DateTime.UtcNow.Subtract(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                                 .TotalSeconds / _opt.RoundSeconds);
            if (bucket != _syncBucket)
            {
                _syncBucket = bucket;
                _words.SetSeed(_opt.SyncSeed ^ (int)(bucket & 0x7fffffff));
                DoRotate();
            }
            return;
        }

        if ((DateTime.Now - _lastRotate).TotalSeconds >= _opt.RoundSeconds) DoRotate();
    }

    private void DoRotate()
    {
        if (_lastDeath != DateTime.MinValue &&
            (DateTime.Now - _lastDeath).TotalSeconds < _opt.DeathCooldownSeconds)
            return;

        // 换词前把「本周期高频词」拍下来，面板上要显示「上一周期高频词」
        var snapshot = new List<KeyValuePair<string, int>>();
        foreach (string w in _stats.Candidates(12))
            snapshot.Add(new KeyValuePair<string, int>(w, Math.Max(1, _stats.RoundCount(w))));
        _prevTop = snapshot;

        if (_opt.ExternalOnly) return;   // 客户端不自己造词，等主机给

        IReadOnlyList<DangerWord> round = _words.NextRound();
        _current = round;
        _lastRotate = DateTime.Now;
        OnRoundChanged?.Invoke(round);
    }

    /// <summary>
    /// 边说边判：只做原词精确，命中就当场判死。
    ///
    /// 稳定性判据是「**同一个词连续命中 N+1 次**」，而不是「临时文本两次相同」——
    /// 人说话时文本每 40ms 都在变长，后者永远不成立（2026-10-22 实测踩过）。
    /// </summary>
    private void TryPartialMatch(string text)
    {
        if (!_opt.MatchPartials) return;
        if ((DateTime.Now - _lastDeath).TotalSeconds < _opt.DeathCooldownSeconds) return;

        MatchResult hit = WordMatcher.FindExact(_current, text);
        if (!hit.IsMatch)
        {
            _partialWord = "";
            _partialHits = 0;
            return;
        }

        if (hit.Word.Text == _partialWord) _partialHits++;
        else { _partialWord = hit.Word.Text; _partialHits = 1; }

        if (_partialHits <= _opt.PartialStableTicks) return;

        RaiseDeath(hit, null, text, "partial");
    }

    private void HandleUtterance(Utterance utt)
    {
        _lastUtteranceId = utt.Id;

        string joined = _window.Join(utt.StreamingText);
        utt.JoinedText = joined;

        _stats.Add(utt.StreamingText);
        OnUtterance?.Invoke(utt);

        if ((DateTime.Now - _lastDeath).TotalSeconds < _opt.DeathCooldownSeconds)
        {
            _window.Push(utt.StreamingText);
            return;
        }

        MatchResult verdict = WordMatcher.Find(
            _current, utt.StreamingText, utt.ReviewText, joined,
            _opt.AllowAlias, _opt.AllowFuzzy);

        _window.Push(utt.StreamingText);

        if (verdict.IsMatch) RaiseDeath(verdict, utt, utt.StreamingText, "utterance");
    }

    /// <summary>
    /// 后台复核回来后的第二次机会。
    /// 只对「刚说完的那一句」有效，且不能重复判同一个句子。
    /// </summary>
    private void HandleReviewed(Utterance utt)
    {
        if (utt == null) return;
        if (utt.Id != _lastUtteranceId || utt.Id == _matchedId) return;
        if ((DateTime.Now - _lastDeath).TotalSeconds < _opt.DeathCooldownSeconds) return;
        if (string.IsNullOrEmpty(utt.ReviewText)) return;

        MatchResult verdict = WordMatcher.Find(
            _current, utt.StreamingText, utt.ReviewText, utt.JoinedText,
            _opt.AllowAlias, _opt.AllowFuzzy);

        if (verdict.IsMatch) RaiseDeath(verdict, utt, utt.ReviewText, "review");
    }

    private void RaiseDeath(MatchResult hit, Utterance utt, string evidence, string via)
    {
        _lastDeath = DateTime.Now;
        _matchedId = utt?.Id ?? 0;
        _partialWord = "";
        _partialHits = 0;
        _window.Clear();
        _resetPending = true;

        OnDeath?.Invoke(new Verdict
        {
            Word = hit.Word,
            Kind = hit.Kind,
            Via = via,
            Utterance = utt ?? new Utterance { StreamingText = evidence, DisplayText = evidence }
        });
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        try { _queue.TryAdd(Array.Empty<float>()); } catch { }
        try { _worker?.Join(1500); } catch { }
    }

    public void Dispose()
    {
        Stop();
        try { _queue.Dispose(); } catch { }
        try { _engine?.Dispose(); } catch { }
    }
}
