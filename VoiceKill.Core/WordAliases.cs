using System;
using System.Collections.Generic;
using System.Text;

namespace VoiceKill.Core;

/// <summary>
/// 别名生成：同一句话，不同人有不同口音，小模型也有自己的偏好。
/// 少一层这个，玩家就会骂「我明明说了你不判」。
/// </summary>
public static class WordAliases
{
    // 字母的中文读音（「ad」被听成「哎弟」就是靠这张表救回来的）
    private static readonly Dictionary<char, string[]> LetterReadings = new Dictionary<char, string[]>
    {
        { 'a', new[] { "诶", "哎", "阿", "啊", "欸" } },
        { 'b', new[] { "比", "必", "逼", "币", "毕" } },
        { 'c', new[] { "西", "希", "细", "喜", "溪" } },
        { 'd', new[] { "弟", "地", "的", "底", "迪", "帝" } },
        { 'e', new[] { "一", "衣", "依", "医" } },
        { 'f', new[] { "艾弗", "爱抚", "夫" } },
        { 'g', new[] { "记", "及", "吉", "级" } },
        { 'h', new[] { "艾尺", "爱吃", "喝" } },
        { 'i', new[] { "爱", "艾", "我" } },
        { 'j', new[] { "杰", "街", "接", "结" } },
        { 'k', new[] { "开", "凯", "磕" } },
        { 'l', new[] { "艾勒", "爱乐", "乐" } },
        { 'm', new[] { "艾姆", "爱慕", "母" } },
        { 'n', new[] { "恩", "嗯" } },
        { 'o', new[] { "哦", "欧", "噢" } },
        { 'p', new[] { "屁", "批", "皮", "劈", "披" } },
        { 'q', new[] { "丘", "秋", "求", "球" } },
        { 'r', new[] { "阿", "啊" } },
        { 's', new[] { "艾斯", "爱死", "死", "斯", "思" } },
        { 't', new[] { "提", "踢", "体", "替" } },
        { 'u', new[] { "优", "由", "油", "有" } },
        { 'v', new[] { "维", "威", "微" } },
        { 'w', new[] { "达不溜", "双u", "五" } },
        { 'x', new[] { "艾克斯", "爱克", "叉" } },
        { 'y', new[] { "歪", "外", "why" } },
        { 'z', new[] { "贼", "则", "泽" } },
    };

    // 同音字表：只换一个位置，别做全笛卡尔积（会爆炸）
    private static readonly Dictionary<char, string> Homophones = new Dictionary<char, string>
    {
        { '他', "她它塔踏" },
        { '她', "他它塔" },
        { '它', "他她" },
        { '是', "事试市世式视" },
        { '事', "是试世" },
        { '到', "倒道盗" },
        { '道', "到倒" },
        { '做', "作坐座" },
        { '在', "再载" },
        { '再', "在" },
        { '那', "哪纳拿" },
        { '这', "着者" },
        { '死', "思四寺似" },
        { '四', "死是寺" },
        { '冲', "充虫重" },
        { '刺', "次此赐" },
        { '次', "刺此" },
        { '跳', "条挑调" },
        { '抓', "爪" },
        { '爬', "趴怕" },
        { '怕', "爬趴" },
        { '糖', "躺汤烫" },
        { '莓', "妹梅霉煤" },
        { '蓝', "烂兰拦" },
        { '心', "新欣辛" },
        { '山', "衫三闪" },
        { '三', "山散" },
        { '空', "控孔恐" },
        { '星', "行心新" },
        { '金', "今进近" },
        { '慢', "漫满" },
        { '快', "块筷" },
        { '了', "乐勒" },
        { '不', "部布步" },
        { '好', "号毫" },
        { '难', "南男" },
        { '飞', "非菲" },
        { '成', "城程诚" },
        { '玩', "完丸" },
        { '完', "玩" },
        { '关', "官观" },
        { '过', "果国" },
        { '回', "会灰" },
        { '会', "回汇" },
        { '买', "卖麦" },
        { '太', "泰态" },
        { '真', "针珍" },
        { '园', "圆员元原" },
    };

    private static readonly Dictionary<string, IReadOnlyList<string>> Cache =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    private const int MaxVariants = 48;

    /// <summary>返回该词的所有可接受变体（不含原词本身）。</summary>
    public static IReadOnlyList<string> Get(string word)
    {
        string key = TextNormalizer.Normalize(word);
        if (string.IsNullOrEmpty(key)) return Array.Empty<string>();
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
            var built = Build(key);
            Cache[key] = built;
            return built;
        }
    }

    private static IReadOnlyList<string> Build(string norm)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);

        // 1) 字母读音展开（「ad」→「哎弟」），拼音/汉字混着来的也能救
        if (TextNormalizer.HasAsciiLetter(norm))
        {
            foreach (string variant in ExpandLetters(norm))
                if (variant != norm) result.Add(variant);
        }

        // 2) 同音字逐位替换（「他」→「她」）
        for (int i = 0; i < norm.Length; i++)
        {
            if (!Homophones.TryGetValue(norm[i], out string alts)) continue;
            foreach (char alt in alts)
            {
                var chars = norm.ToCharArray();
                chars[i] = alt;
                string v = new string(chars);
                if (v != norm) result.Add(v);
            }
        }

        var list = new List<string>(result);
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    private static IEnumerable<string> ExpandLetters(string norm)
    {
        var results = new List<string> { "" };

        foreach (char c in norm)
        {
            if (c >= 'a' && c <= 'z' && LetterReadings.TryGetValue(c, out string[] readings))
            {
                var next = new List<string>(results.Count * readings.Length + 8);
                foreach (string prefix in results)
                {
                    // 原字母也保留一条（流式结果可能本来就是英文）
                    next.Add(prefix + c);
                    foreach (string r in readings) next.Add(prefix + r);
                }
                results = next;
            }
            else
            {
                for (int i = 0; i < results.Count; i++) results[i] += c;
            }

            if (results.Count > MaxVariants)
                results = results.GetRange(0, MaxVariants);
        }

        return results;
    }

    public static void ClearCache()
    {
        lock (Cache) Cache.Clear();
    }
}
