using System;
using System.Collections.Generic;

namespace VoiceKill.Core;

public enum WordSource
{
    /// <summary>网页/面板上手动指定的下一轮。</summary>
    Manual = 0,

    /// <summary>人名配额。</summary>
    Name = 1,

    /// <summary>智能选词（最近说过的高频词）。</summary>
    Smart = 2,

    /// <summary>高频地狱词。</summary>
    Hell = 3,

    /// <summary>随机词库。</summary>
    Random = 4,
}

public sealed class DangerWord
{
    public string Text = "";
    public WordSource Source = WordSource.Random;

    public DangerWord() { }
    public DangerWord(string text, WordSource source) { Text = text; Source = source; }

    public override string ToString() => Text;
}

public enum EvidenceKind
{
    /// <summary>原词精确包含（证据最硬）。</summary>
    Exact = 0,

    /// <summary>句末复核文本精确包含（只对「≥2 字符且含英文字母」的词开放）。</summary>
    ReviewExact = 1,

    /// <summary>谐音 / 同音变体包含。</summary>
    Alias = 2,

    /// <summary>编辑距离（最后兜底，最容易误杀）。</summary>
    Fuzzy = 3,
}

public sealed class MatchResult
{
    public DangerWord Word;
    public EvidenceKind Kind;
    /// <summary>命中的那段文本，用来给玩家展示「你到底说了啥」。</summary>
    public string Evidence = "";

    public bool IsMatch => Word != null;

    public static readonly MatchResult None = new MatchResult();

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
}

/// <summary>
/// 匹配器。优先级：精确 → 复核精确 → 谐音 → 编辑距离。
/// 多个候选同时命中时，**排序靠前的赢**（调用方已按「人名/手动优先，其次更长的词优先」排好）。
/// </summary>
public static class WordMatcher
{
    public static MatchResult Find(
        IReadOnlyList<DangerWord> words,
        string streamingText,
        string reviewText,
        string joinedText,
        bool allowAlias = true,
        bool allowFuzzy = true)
    {
        if (words == null || words.Count == 0) return MatchResult.None;

        string stream = TextNormalizer.Normalize(streamingText);
        string review = TextNormalizer.Normalize(reviewText);
        string joined = TextNormalizer.Normalize(joinedText);

        MatchResult best = null;

        foreach (DangerWord word in words)
        {
            if (word == null || string.IsNullOrEmpty(word.Text)) continue;
            string target = TextNormalizer.Normalize(word.Text);
            if (target.Length == 0) continue;

            // 1. 原词精确
            if (Contains(stream, target) || Contains(joined, target))
            {
                best = Better(best, new MatchResult { Word = word, Kind = EvidenceKind.Exact, Evidence = streamingText });
                if (best.Kind == EvidenceKind.Exact) break; // 最硬的证据，不用再找了
                continue;
            }

            // 2. 复核精确：只放行「≥2 字符且含 ASCII 字母」的词，否则纯中文会被疯狂误杀
            if (review.Length > 0 && target.Length >= 2 && TextNormalizer.HasAsciiLetter(target) && Contains(review, target))
            {
                best = Better(best, new MatchResult { Word = word, Kind = EvidenceKind.ReviewExact, Evidence = reviewText });
                continue;
            }

            // 3. 谐音 / 同音
            if (allowAlias)
            {
                bool hit = false;
                foreach (string alias in WordAliases.Get(target))
                {
                    if (Contains(stream, alias) || Contains(review, alias) || Contains(joined, alias))
                    {
                        hit = true;
                        break;
                    }
                }
                if (hit)
                {
                    best = Better(best, new MatchResult { Word = word, Kind = EvidenceKind.Alias, Evidence = streamingText });
                    continue;
                }
            }

            // 4. 编辑距离兜底
            if (allowFuzzy && target.Length >= 4 &&
                (FuzzyMatch.WindowContains(stream, target) || FuzzyMatch.WindowContains(joined, target)))
            {
                best = Better(best, new MatchResult { Word = word, Kind = EvidenceKind.Fuzzy, Evidence = streamingText });
            }
        }

        return best ?? MatchResult.None;
    }

    /// <summary>
    /// 只做「原词精确包含」这一档。
    /// 给「边说边判」的实时通道用 —— 这一档误杀率最低，才敢在句子还没说完时下杀手。
    /// </summary>
    public static MatchResult FindExact(IReadOnlyList<DangerWord> words, string text)
    {
        if (words == null || words.Count == 0) return MatchResult.None;
        string norm = TextNormalizer.Normalize(text);
        if (norm.Length == 0) return MatchResult.None;

        foreach (DangerWord word in words)
        {
            if (word == null || string.IsNullOrEmpty(word.Text)) continue;
            string target = TextNormalizer.Normalize(word.Text);
            if (target.Length == 0) continue;
            if (norm.IndexOf(target, StringComparison.Ordinal) >= 0)
                return new MatchResult { Word = word, Kind = EvidenceKind.Exact, Evidence = text };
        }
        return MatchResult.None;
    }

    private static MatchResult Better(MatchResult a, MatchResult b)
    {
        if (a == null) return b;
        if (b == null) return a;
        // 级别更硬（数字更小）优先；同级别保留先生成的（= 排序靠前 = 优先级更高）
        return b.Kind < a.Kind ? b : a;
    }

    private static bool Contains(string haystack, string needle)
        => haystack.Length > 0 && needle.Length > 0 && haystack.IndexOf(needle, StringComparison.Ordinal) >= 0;
}
