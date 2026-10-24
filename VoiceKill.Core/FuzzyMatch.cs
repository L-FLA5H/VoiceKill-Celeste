using System;

namespace VoiceKill.Core;

/// <summary>编辑距离。只当一个兜底手段，别拿它当主力 —— 误杀就是这么来的。</summary>
public static class FuzzyMatch
{
    /// <summary>
    /// 长度 4 允许差 1 字；长度 ≥5 允许差 2 字。短词一律不算，否则「小黑」会算成「小白」。
    /// </summary>
    public static int MaxDistanceFor(int length)
    {
        if (length < 4) return 0;
        if (length == 4) return 1;
        return 2;
    }

    public static int Distance(string a, string b)
    {
        if (a == b) return 0;
        if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
        if (string.IsNullOrEmpty(b)) return a.Length;

        int la = a.Length, lb = b.Length;
        var prev = new int[lb + 1];
        var cur = new int[lb + 1];
        for (int j = 0; j <= lb; j++) prev[j] = j;

        for (int i = 1; i <= la; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= lb; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            var t = prev; prev = cur; cur = t;
        }
        return prev[lb];
    }

    /// <summary>等长滑窗 + 长度差 1 的滑窗，在长句里找「差不多」的片段。</summary>
    public static bool WindowContains(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return false;

        int maxDist = MaxDistanceFor(needle.Length);
        if (maxDist == 0) return haystack.Contains(needle, StringComparison.Ordinal);

        foreach (int win in new[] { needle.Length - 1, needle.Length, needle.Length + 1 })
        {
            if (win <= 0 || win > haystack.Length) continue;
            for (int i = 0; i + win <= haystack.Length; i++)
            {
                string slice = haystack.Substring(i, win);
                if (Distance(slice, needle) <= maxDist) return true;
            }
        }
        return false;
    }
}
