using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceKill.Core;

public sealed class DangerWordConfig
{
    /// <summary>一轮凑几个词。实测 5~20，太少没压迫感，太多全是废词。</summary>
    public int WordsPerRound = 8;

    /// <summary>每轮固定塞几个人名。</summary>
    public int NamesPerRound = 1;

    /// <summary>每轮固定塞几个高频地狱词。实测 6~12 才够刺激，起步给 3。</summary>
    public int HighFreqPerRound = 3;

    /// <summary>从「最近说过的高频词」里选词的概率。</summary>
    public double SmartPickChance = 0.6;

    /// <summary>每轮最多塞几个智能选词。</summary>
    public int SmartPerRound = 2;

    /// <summary>高频地狱词：额外加的 / 停用的。</summary>
    public List<string> HighFreqExtra = new List<string>();
    public List<string> HighFreqDisabled = new List<string>();

    /// <summary>最短词长。单字在蔚蓝里误杀率太高，默认禁止。</summary>
    public int MinWordLength = 2;

    public bool AllowSingleChar = false;

    /// <summary>联机玩家昵称 / 主播 ID。</summary>
    public List<string> Names = new List<string>();
}

/// <summary>
/// 组词大脑。一轮怎么凑出来，顺序就是优先级：
/// 手动指定 → 人名配额 → 智能选词 → 高频地狱词 → 随机词库补满。
/// </summary>
public sealed class DangerWordManager
{
    private readonly DangerWordConfig _cfg;
    private readonly SpeechStats _stats;
    private Random _rng;
    private readonly Queue<string> _manual = new Queue<string>();
    private readonly object _manualLock = new object();

    public IReadOnlyList<DangerWord> Current { get; private set; } = Array.Empty<DangerWord>();

    public DangerWordManager(DangerWordConfig cfg, SpeechStats stats, int? seed = null)
    {
        _cfg = cfg ?? new DangerWordConfig();
        _stats = stats ?? new SpeechStats();
        _rng = seed.HasValue ? new Random(seed.Value) : new Random();
    }

    /// <summary>
    /// 换一个随机种子。离线同步模式靠这个：同一口令 + 同一个时间片
    /// → 所有人算出完全一样的词，不需要联网。
    /// </summary>
    public void SetSeed(int seed) => _rng = new Random(seed);

    /// <summary>手动指定下一轮的词（网页上点的那几个）。</summary>
    public void EnqueueManual(IEnumerable<string> words)
    {
        if (words == null) return;
        lock (_manualLock)
        {
            foreach (string w in words)
            {
                if (string.IsNullOrWhiteSpace(w)) continue;
                _manual.Enqueue(w.Trim());
            }
        }
    }

    /// <summary>换一组词。</summary>
    public IReadOnlyList<DangerWord> NextRound()
    {
        int target = Math.Max(1, _cfg.WordsPerRound);
        var picked = new List<DangerWord>(target);
        var used = new HashSet<string>(StringComparer.Ordinal);

        void TryAdd(string text, WordSource source)
        {
            if (picked.Count >= target) return;
            string norm = TextNormalizer.Normalize(text);
            if (norm.Length == 0) return;
            // 高频地狱词里本来就有单字（我/你/是/不…），MC 版就是这么设计的，
            // 所以单字禁令只对随机池和手动词生效，不对 Hell 生效。
            if (source != WordSource.Hell &&
                !_cfg.AllowSingleChar && norm.Length < Math.Max(1, _cfg.MinWordLength)) return;
            if (!used.Add(norm)) return;
            picked.Add(new DangerWord(text.Trim(), source));
        }

        // 1) 手动指定：原样优先
        List<string> manual;
        lock (_manualLock)
        {
            manual = new List<string>(_manual);
            _manual.Clear();
        }
        foreach (string m in manual)
        {
            if (picked.Count >= target) break;
            TryAdd(m, WordSource.Manual);
        }

        // 2) 人名配额
        for (int i = 0; i < _cfg.NamesPerRound && picked.Count < target; i++)
        {
            var pool = _cfg.Names.Where(n => !used.Contains(TextNormalizer.Normalize(n))).ToList();
            if (pool.Count == 0) break;
            TryAdd(pool[_rng.Next(pool.Count)], WordSource.Name);
        }

        // 3) 智能选词：按权重加权随机，不是取 Top1
        if (picked.Count < target && _rng.NextDouble() < _cfg.SmartPickChance)
        {
            List<string> candidates = _stats.Candidates(24)
                .Where(c => !used.Contains(c) && WordBank.Stopwords.Contains(c) == false)
                .Where(c => _cfg.AllowSingleChar || c.Length >= Math.Max(1, _cfg.MinWordLength))
                .ToList();

            int want = Math.Min(candidates.Count, Math.Max(0, _cfg.SmartPerRound));
            for (int i = 0; i < want && picked.Count < target; i++)
            {
                string pick = WeightedPick(candidates);
                if (pick == null) break;
                candidates.Remove(pick);
                TryAdd(pick, WordSource.Smart);
            }
        }

        // 4) 高频地狱词：定额，躲不掉
        var hellPool = HighFreqPool()
            .Where(w => !used.Contains(TextNormalizer.Normalize(w)))
            .OrderBy(_ => _rng.Next())
            .ToList();

        int hellCount = 0;
        foreach (string h in hellPool)
        {
            if (hellCount >= _cfg.HighFreqPerRound || picked.Count >= target) break;
            int before = picked.Count;
            TryAdd(h, WordSource.Hell);
            if (picked.Count > before) hellCount++;
        }

        // 5) 随机词库补满
        var filler = WordBank.Custom.Concat(WordBank.Casual).Concat(WordBank.Formal)
            .Where(w => !used.Contains(TextNormalizer.Normalize(w)))
            .OrderBy(_ => _rng.Next())
            .ToList();

        foreach (string f in filler)
        {
            if (picked.Count >= target) break;
            TryAdd(f, WordSource.Random);
        }

        // 排序：优先级高的在前，同级别更长的词在前（长词命中更可信）
        picked = picked
            .OrderBy(w => (int)w.Source)
            .ThenByDescending(w => TextNormalizer.Normalize(w.Text).Length)
            .ToList();

        Current = picked;
        _stats.ClearRound();
        return Current;
    }

    /// <summary>高频地狱词池 = 内置 − 停用 + 额外加。</summary>
    private List<string> HighFreqPool()
    {
        var dis = new HashSet<string>(StringComparer.Ordinal);
        foreach (string w in _cfg.HighFreqDisabled) dis.Add(TextNormalizer.Normalize(w));

        var list = new List<string>();
        foreach (string w in WordBank.Hell)
        {
            if (dis.Contains(TextNormalizer.Normalize(w))) continue;
            list.Add(w);
        }
        foreach (string w in _cfg.HighFreqExtra)
        {
            if (string.IsNullOrWhiteSpace(w)) continue;
            if (!list.Contains(w)) list.Add(w.Trim());
        }
        return list;
    }

    /// <summary>加权随机：权重来自「本轮×3 + 累计×1」。</summary>
    private string WeightedPick(List<string> candidates)
    {
        if (candidates.Count == 0) return null;

        var weights = new double[candidates.Count];
        double total = 0;
        for (int i = 0; i < candidates.Count; i++)
        {
            double w = _stats.RoundCount(candidates[i]) * 3.0 + _stats.TotalCount(candidates[i]) * 1.0;
            if (w <= 0) w = 0.5;
            weights[i] = w;
            total += w;
        }

        double roll = _rng.NextDouble() * total;
        for (int i = 0; i < candidates.Count; i++)
        {
            roll -= weights[i];
            if (roll <= 0) return candidates[i];
        }
        return candidates[candidates.Count - 1];
    }

    public void ClearManual()
    {
        lock (_manualLock) _manual.Clear();
    }
}
