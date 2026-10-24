using System;
using System.Collections.Generic;

namespace VoiceKill.Core;

/// <summary>中文 n-gram 词频统计。分「本轮」和「整局累计」两本账。</summary>
public sealed class SpeechStats
{
    private readonly Dictionary<string, int> _round = new Dictionary<string, int>(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _total = new Dictionary<string, int>(StringComparer.Ordinal);

    private const int MinGram = 2;
    private const int MaxGram = 4;

    public void Add(string text)
    {
        string norm = TextNormalizer.Normalize(text);
        if (norm.Length == 0) return;

        // 注意：不要把「整句」也 Bump 一次 —— 那会和下面的 n-gram 重复计数，
        // 一句 2 字的话会被记 2 次，权重直接翻倍（自检第 11 条抓出来的）。

        // 连续汉字段切 n-gram
        int i = 0;
        while (i < norm.Length)
        {
            if (!IsCjk(norm[i])) { i++; continue; }
            int start = i;
            while (i < norm.Length && IsCjk(norm[i])) i++;
            int runLen = i - start;

            for (int len = MinGram; len <= MaxGram && len <= runLen; len++)
            {
                for (int s = start; s + len <= start + runLen; s++)
                {
                    string gram = norm.Substring(s, len);
                    if (WordBank.Stopwords.Contains(gram)) continue;
                    Bump(_round, gram);
                    Bump(_total, gram);
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
    /// </summary>
    public List<string> Candidates(int max = 24)
    {
        var scored = new List<KeyValuePair<string, double>>();

        foreach (var kv in _total)
        {
            string gram = kv.Key;
            if (WordBank.Stopwords.Contains(gram)) continue;

            int roundCount = _round.TryGetValue(gram, out int r) ? r : 0;
            int totalCount = kv.Value;

            // 词库不认识的碎片，必须重复出现 ≥2 次才有资格
            if (!WordBank.IsKnown(gram) && totalCount < 2) continue;

            double score = roundCount * 3.0 + totalCount * 1.0;
            if (score <= 0) continue;
            scored.Add(new KeyValuePair<string, double>(gram, score));
        }

        scored.Sort((a, b) => b.Value.CompareTo(a.Value));

        var result = new List<string>();
        for (int i = 0; i < scored.Count && result.Count < max; i++) result.Add(scored[i].Key);
        return result;
    }

    public int RoundCount(string gram) => _round.TryGetValue(gram, out int c) ? c : 0;

    public int TotalCount(string gram) => _total.TryGetValue(gram, out int c) ? c : 0;

    /// <summary>换一轮：本轮清零，整局累计不清。</summary>
    public void ClearRound() => _round.Clear();

    public void ClearAll()
    {
        _round.Clear();
        _total.Clear();
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
