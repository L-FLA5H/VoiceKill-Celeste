using System;
using System.Collections.Generic;

namespace VoiceKill.Core;

/// <summary>中文 n-gram 词频统计。分「本轮」和「整局累计」两本账。</summary>
public sealed class SpeechStats
{
    private readonly Dictionary<string, int> _round = new Dictionary<string, int>(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _total = new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>统计表既被识别线程写、又被面板线程读，读写都走这把锁。</summary>
    private readonly object _sync = new object();

    /// <summary>每个片段左右两边各贴过几种字（最多记 2 种就够判断了）。</summary>
    private readonly Dictionary<string, Neigh> _neigh = new Dictionary<string, Neigh>(StringComparer.Ordinal);

    private const int MinGram = 2;
    private const int MaxGram = 4;

    /// <summary>词库里认识的词，排名时给一点加成（真词优先于碎片）。</summary>
    private const double KnownBoost = 1.6;

    /// <summary>句子开头 / 结尾，当成两种「邻字」看待。</summary>
    private const char BoundaryStart = '\u0001';
    private const char BoundaryEnd = '\u0002';

    private struct Cand
    {
        public int Round;
        public int Total;
        public bool Known;
    }

    private struct Neigh
    {
        public char L0, L1, R0, R1;
        public byte Ln, Rn;
    }

    public void Add(string text)
    {
        string norm = TextNormalizer.Normalize(text);
        if (norm.Length == 0) return;
        lock (_sync) AddCore(norm);
    }

    // 加锁的原因：Add 在识别线程上跑，Candidates 在面板/叠层线程上读，
    // Dictionary 一边写一边 foreach 是有概率直接抛异常的（以前就有这个隐患）。
    private void AddCore(string norm)
    {
        // 注意：不要把「整句」也 Bump 一次 —— 那会和下面的 n-gram 重复计数，
        // 一句 2 字的话会被记 2 次，权重直接翻倍（自检第 11 条抓出来的）。

        // 连续汉字段切 n-gram
        //
        // 切之前先「去重复」：同一个人连说两遍才会有这种输入 ——
        // 「别搞别搞」滑窗会切出「搞别搞」，「走路走路」切出「路走路」，
        // 都是跨在两个重复单元接缝上的假词，拿去当危险词玩家根本说不出来。
        // 先把「单元重复 ≥2 次」收成一次（别搞别搞 → 别搞），假词就生不出来了。
        int i = 0;
        while (i < norm.Length)
        {
            if (!IsCjk(norm[i])) { i++; continue; }
            int start = i;
            while (i < norm.Length && IsCjk(norm[i])) i++;
            string run = CollapseRepeats(norm.Substring(start, i - start));

            for (int len = MinGram; len <= MaxGram && len <= run.Length; len++)
            {
                for (int s = 0; s + len <= run.Length; s++)
                {
                    string gram = run.Substring(s, len);
                    if (WordBank.Stopwords.Contains(gram)) continue;
                    Bump(_round, gram);
                    Bump(_total, gram);
                    AddNeigh(gram,
                        s == 0 ? BoundaryStart : run[s - 1],
                        s + len == run.Length ? BoundaryEnd : run[s + len]);
                }
            }
        }

        // 英文单词
        foreach (string token in SplitAscii(norm))
        {
            if (token.Length < 2 || WordBank.Stopwords.Contains(token)) continue;
            Bump(_round, token);
            Bump(_total, token);
        }
    }

    /// <summary>
    /// 智能选词候选。权重 = 本轮 ×3 + 整局 ×1。
    /// 注意是**加权随机**，不是取 Top1 —— 每轮都一样观众两分钟就腻。
    ///
    /// 2026-11-01 修：n-gram 是滑窗切的，一定会切出跨词边界的假词 ——
    /// 「别搞别搞」切出「搞别搞」，「浏览器」切出「览器」，「走路走路」切出「路走路」。
    /// 这些词拿去当危险词，玩家根本说不出来，纯废词。三道闸：
    ///   ① 切之前先去重复：同一个人连说两遍才会这样（见 Add / CollapseRepeats），
    ///      「别搞别搞」在切 n-gram 之前就被收成「别搞」，假词生不出来；
    ///   ② 去胶水：「超市了」「去超市」砍成「超市」—— 两头挂着「了/去/我」这种
    ///      单字虚词、而且次数和核心词完全一样的，就是虚词粘上去的；
    ///   ③ 邻字校验：一边永远只贴着同一个字，就是切歪的碎片（「览器」前面永远
    ///      是「浏」）。真词两边的邻居一定是乱的。
    /// 另外两道小闸：整串全是虚字（「了吧」）不算词；「词库里的词 + 一个尾巴」
    /// （「弹簧」+「弹」→「弹簧弹」）也不算词。
    ///
    /// 词库里认识的词全程免检 —— 免得把「死亡」（只在「死亡冲刺」里出现）
    /// 这种真词误伤，顺带把真词排到碎片前面。
    /// </summary>
    public List<string> Candidates(int max = 24)
    {
        lock (_sync) return CandidatesCore(max);
    }

    private List<string> CandidatesCore(int max)
    {
        // ① 原始候选
        var pool = new Dictionary<string, Cand>(StringComparer.Ordinal);
        foreach (var kv in _total)
        {
            string gram = kv.Key;
            if (gram.Length < MinGram) continue;
            if (WordBank.Stopwords.Contains(gram)) continue;

            bool known = WordBank.IsKnown(gram);
            // 词库不认识的碎片，必须重复出现 ≥2 次才有资格
            if (!known && kv.Value < 2) continue;
            // 整串都是虚字（「了吧」「这就」）—— 不是词
            if (!known && AllGlue(gram)) continue;

            pool[gram] = new Cand
            {
                Round = _round.TryGetValue(gram, out int r) ? r : 0,
                Total = kv.Value,
                Known = known,
            };
        }
        if (pool.Count == 0) return new List<string>();

        // ② 去胶水：「超市了」砍成「超市」
        var keep = new Dictionary<string, Cand>(StringComparer.Ordinal);
        foreach (var kv in pool)
        {
            Cand c = kv.Value;
            if (c.Known) { keep[kv.Key] = c; continue; }

            string core = StripGlue(kv.Key);
            if (core != kv.Key &&
                pool.TryGetValue(core, out Cand coreCand) && coreCand.Total >= c.Total)
                continue;   // 核心词自己已经在候选里了，这个只是它加了个虚词

            keep[kv.Key] = c;
        }

        // ③ 叠字兜底：「走路走路」→「走路」（正常情况下 Add 已经处理过）
        var collapsed = new Dictionary<string, Cand>(StringComparer.Ordinal);
        foreach (var kv in keep)
        {
            string baseGram = CollapseRepeat(kv.Key);
            if (collapsed.TryGetValue(baseGram, out Cand old))
            {
                collapsed[baseGram] = new Cand
                {
                    Round = old.Round + kv.Value.Round,
                    Total = old.Total + kv.Value.Total,
                    Known = old.Known || kv.Value.Known,
                };
            }
            else collapsed[baseGram] = kv.Value;
        }

        // ④ 左右邻字校验：一边永远贴着同一个字 → 这个词根本不是独立的
        //
        // 真词两边贴什么字是乱的（「超市」前面有 去/进/逛，后面有 了/吧/那边）；
        // 切歪的碎片一定有一边被钉死 —— 「览器」前面永远是「浏」，
        // 「路走路」前面永远是「走」，「今天去超」前后各只有一种。
        // 这条是「新词发现」里的标准判据（邻接熵），比数次数靠谱得多。
        var scored = new List<KeyValuePair<string, double>>();
        foreach (var kv in collapsed)
        {
            Cand c = kv.Value;

            if (!c.Known)
            {
                if (!_neigh.TryGetValue(kv.Key, out Neigh n)) continue;
                if (n.Ln < 2 || n.Rn < 2) continue;
                // 「已知词 + 一个字」= 词后面拖了个尾巴：「弹簧」+「弹」→「弹簧弹」
                if (HasKnownStem(kv.Key, c.Total, pool)) continue;
            }

            double score = c.Round * 3.0 + c.Total * 1.0;
            if (c.Known) score *= KnownBoost;
            if (score <= 0) continue;
            scored.Add(new KeyValuePair<string, double>(kv.Key, score));
        }

        scored.Sort((a, b) => b.Value.CompareTo(a.Value));

        var result = new List<string>();
        for (int i = 0; i < scored.Count && result.Count < max; i++) result.Add(scored[i].Key);
        return result;
    }

    /// <summary>
    /// 砍掉两头的单字虚词（我/你/了/的/就/去…），至少留两个字。
    /// 「超市了」→「超市」，「去超市」→「超市」，「我靠」→「我靠」（砍完只剩一个字，不动）。
    /// </summary>
    private static string StripGlue(string s)
    {
        int a = 0, b = s.Length;
        while (b - a > MinGram && WordBank.Stopwords.Contains(s[a].ToString())) a++;
        while (b - a > MinGram && WordBank.Stopwords.Contains(s[b - 1].ToString())) b--;
        if (a == 0 && b == s.Length) return s;
        return s.Substring(a, b - a);
    }

    /// <summary>整串都是单字虚词（了吧 / 这就 / 啊呀）。</summary>
    private static bool AllGlue(string s)
    {
        for (int i = 0; i < s.Length; i++)
            if (!WordBank.Stopwords.Contains(s[i].ToString())) return false;
        return true;
    }

    /// <summary>
    /// 开头或结尾是不是一个词库里认识的词，而且次数不比本串少 ——
    /// 是的话本串就是那个词拖了个尾巴（「弹簧」+「弹」），不是独立词。
    /// </summary>
    private static bool HasKnownStem(string gram, int total, Dictionary<string, Cand> pool)
    {
        for (int len = MinGram; len < gram.Length; len++)
        {
            if (pool.TryGetValue(gram.Substring(0, len), out Cand head) && head.Known && head.Total >= total) return true;
            if (pool.TryGetValue(gram.Substring(gram.Length - len), out Cand tail) && tail.Known && tail.Total >= total) return true;
        }
        return false;
    }

    /// <summary>
    /// 「别搞别搞」→「别搞」，「走路走路」→「走路」。
    /// 只收长度 ≥2 的周期，单字重复（哈哈 / 走走走）保持原样 ——
    /// 单字不在统计里，收下去就什么都没了。
    /// </summary>
    private static string CollapseRepeat(string s)
    {
        if (s.Length < MinGram * 2) return s;

        for (int p = MinGram; p <= s.Length / 2; p++)
        {
            if (s.Length % p != 0) continue;
            bool periodic = true;
            for (int i = p; i < s.Length; i++)
            {
                if (s[i] != s[i % p]) { periodic = false; break; }
            }
            if (periodic) return s.Substring(0, p);
        }
        return s;
    }

    /// <summary>
    /// 把「单元连续重复 ≥2 次」收成一次：别搞别搞 → 别搞，走路走路走路 → 走路。
    /// 单元长度限制在 2~8，单字重复（不不不 / 哈哈哈）原样留着 ——
    /// 那是口吃和笑声，不是重复句，收掉会把「哈哈」这类词一起收了。
    /// </summary>
    private static string CollapseRepeats(string run)
    {
        if (run.Length < 4) return run;

        var sb = new System.Text.StringBuilder(run.Length);
        int i = 0;
        while (i < run.Length)
        {
            bool hit = false;
            for (int p = 2; p <= MaxRepeatUnit && i + p * 2 <= run.Length; p++)
            {
                int reps = 1;
                while (i + (reps + 1) * p <= run.Length &&
                       string.CompareOrdinal(run, i, run, i + reps * p, p) == 0)
                    reps++;
                if (reps >= 2)
                {
                    sb.Append(run, i, p);
                    i += reps * p;
                    hit = true;
                    break;
                }
            }
            if (!hit) sb.Append(run[i++]);
        }
        return sb.ToString();
    }

    private const int MaxRepeatUnit = 8;

    public int RoundCount(string gram) { lock (_sync) return _round.TryGetValue(gram, out int c) ? c : 0; }

    public int TotalCount(string gram) { lock (_sync) return _total.TryGetValue(gram, out int c) ? c : 0; }

    /// <summary>换一轮：本轮清零，整局累计不清。</summary>
    public void ClearRound() { lock (_sync) _round.Clear(); }

    public void ClearAll()
    {
        lock (_sync)
        {
            _round.Clear();
            _total.Clear();
            _neigh.Clear();
        }
    }

    /// <summary>记一下这个片段左右两边各贴过什么字（每种最多记 2 个）。</summary>
    private void AddNeigh(string gram, char left, char right)
    {
        if (!_neigh.TryGetValue(gram, out Neigh n)) n = default;
        Side(ref n.L0, ref n.L1, ref n.Ln, left);
        Side(ref n.R0, ref n.R1, ref n.Rn, right);
        _neigh[gram] = n;
    }

    private static void Side(ref char a, ref char b, ref byte n, char c)
    {
        if (n >= 1 && a == c) return;
        if (n >= 2 && b == c) return;
        if (n == 0) { a = c; n = 1; }
        else if (n == 1) { b = c; n = 2; }
    }

    private static void Bump(Dictionary<string, int> dict, string key)
    {
        dict.TryGetValue(key, out int c);
        dict[key] = c + 1;
    }

    private static bool IsCjk(char c) => c >= 0x4E00 && c <= 0x9FFF;

    private static IEnumerable<string> SplitAscii(string s)
    {
        int i = 0;
        while (i < s.Length)
        {
            int start = i;
            while (i < s.Length && ((s[i] >= 'a' && s[i] <= 'z') || (s[i] >= '0' && s[i] <= '9'))) i++;
            if (i > start) yield return s.Substring(i - (i - start), i - start);
            else i++;
        }
    }
}
