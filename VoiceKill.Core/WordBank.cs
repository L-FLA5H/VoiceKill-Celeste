using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VoiceKill.Core;

/// <summary>
/// 词库。三层结构照抄 MC 版 voicekill（SLANG / FORMAL / HIGH_FREQ）：
///
///  - SLANG      口语俗称，边跑边喊的就是这些（蔚蓝版按玩家实际用词重写）
///  - FORMAL     正式名 + 英文术语，出现频率低但说出来很正常
///  - HIGH_FREQ  日常口语里几乎每句都有的词，单独一组定额投放 —— 这才是「高频」的本意
///
/// 英文术语的选取依据 celeste.ink 的 Tech 词条（super/hyper/ultra/wavedash/demo/
/// wallbounce/bhop/spikejump/neutral/cornerboost/cpop/dream/jelly/theo/feather...），
/// 这些在社区语音里是**中文夹杂英文**说的，所以必须进词库。
/// </summary>
public static class WordBank
{
    /// <summary>口语俗称 —— 玩家真会喊出口的。</summary>
    public static List<string> Casual = new List<string>
    {
        // ---- 动作 / 技巧（中文叫法）----
        "冲刺", "抓墙", "爬墙", "上墙", "贴墙", "蹭墙", "蹬墙", "过墙", "连冲", "二段跳",
        "大跳", "小跳", "蓄力", "弹跳", "蹬墙跳", "跳冲", "空中冲", "斜冲", "回冲", "连跳",
        "落地", "起跳", "站着", "贴边", "抓边", "跳一下", "拉一下", "卡一下", "蹭一下", "偷一下",
        "稳住", "别急", "起手", "收刀", "过不去", "上不去", "打不准", "按早了", "按晚了", "差一帧",

        // ---- 关卡元素 / 物件 ----
        "弹簧", "云朵", "云", "风扇", "冰块", "岩浆", "尖刺", "传送门", "拖尾", "磁铁",
        "电梯", "泡泡", "水晶", "黑球", "钥匙", "门", "镜子", "羽毛", "果冻", "箱子",
        "鱼", "雪球", "影子", "灯", "电扇", "按钮", "滚石", "小车", "绳索",

        // ---- 资源 / 收集 ----
        "金草莓", "蓝莓", "红草莓", "草莓", "金币", "心", "血", "体力", "命", "存档",
        "读档", "重开", "重来", "无伤", "全收集", "通关", "速通",

        // ---- 状态 / 口头禅（非高频组，语义更具体）----
        "我死了", "又死了", "白给", "暴毙", "掉下去了", "掉帧", "卡帧", "锁帧", "手柄", "键盘",
        "延迟", "输入", "键位", "走位", "手感", "过图", "读图", "黑屏", "加载"
    };

    /// <summary>正式名 / 英文术语。频率低，但说出来完全正常，保证「惊喜感」。</summary>
    public static List<string> Formal = new List<string>
    {
        // ---- 地图 / 剧情 ----
        "玛瑙山", "旧城", "遗迹", "高峰", "山顶", "核心", "虚空", "光辉", "告别", "金色大厅",
        "百香果", "万神殿", "雪线", "猩红", "倒影", "自制图", "草莓酱", "第九章", "第八章",

        // ---- 英文术语（社区里就是这么说）----
        "super", "hyper", "ultra", "demo", "demodash", "wavedash", "wave", "superwave",
        "wallbounce", "wounce", "bhop", "bunnyhop", "spikejump", "spike", "corner",
        "cornerboost", "cornerjump", "neutral", "cpop", "dreamjump", "dream", "jelly",
        "theo", "feather", "spring", "bubs", "reform", "tas", "golden", "berry",
        "any%", "dash", "jump", "climb", "grab", "stamina", "oshiro", "badeline",
        "chapter", "celeste", "speedrun", "goldenberry", "cassette"
    };

    /// <summary>
    /// 高频地狱词：日常口语里几乎每句都会出现，说出来必死。
    /// **直接从 MC 版 voicekill 的 WordBank.HIGH_FREQ 整段照搬** —— 这部分跟游戏无关，
    /// 是中文口语本身的词频，蔚蓝也一样适用。
    /// 单独一组、定额投放，不混进随机池。
    /// </summary>
    public static List<string> Hell = new List<string>
    {
        "我", "你", "他", "她", "它", "我们", "你们", "他们", "她们", "咱们",
        "大家", "自己", "别人", "人家", "各位", "是", "不是", "不", "没", "没有",
        "有", "就", "这", "那", "的", "得", "了", "着", "过", "也",
        "都", "还", "又", "再", "才", "很", "太", "最", "更", "挺",
        "特别", "比较", "这个", "那个", "这些", "那些", "这里", "那里", "这边", "那边",
        "这样", "那样", "这种", "那种", "这么", "那么", "一个", "一下", "一点", "一次",
        "一遍", "一会", "一趟", "一堆", "一群", "一半", "一天", "什么", "怎么", "为什么",
        "怎么办", "怎么样", "咋办", "多少", "多久", "几个", "哪个", "谁", "哪里", "哪儿",
        "干啥", "干嘛", "咋", "但是", "就是", "因为", "所以", "然后", "而且", "不过",
        "如果", "虽然", "要是", "于是", "结果", "反正", "总之", "或者", "还有", "接着",
        "可以", "不行", "不要", "不用", "不能", "不会", "知道", "不知道", "觉得", "感觉",
        "真的", "确实", "其实", "应该", "可能", "一定", "必须", "好像", "大概", "有点",
        "肯定", "估计", "差不多", "对吧", "是吧", "是不是", "有没有", "能不能", "要不要", "好不好",
        "行不行", "对不对", "会不会", "可不可以", "喂", "诶", "哎", "嗯", "啊", "吧",
        "吗", "呀", "哦", "唉", "哈", "嘿", "噢", "哇", "嘛", "呢",
        "啦", "咯", "完了", "算了", "行了", "好了", "快走", "快来", "等一下", "等等",
        "别动", "别急", "没事", "麻烦", "帮忙", "东西", "事情", "时间", "地方", "问题",
        "意思", "现在", "已经", "刚才", "马上", "一会儿", "待会", "回头", "先", "卧槽",
        "我靠", "天哪", "我去", "离谱", "绝了", "牛", "服了", "麻了", "崩了", "寄了",
        "凉了", "完了完了", "救命", "搞什么", "什么鬼", "太过分", "受不了", "走吧", "过来", "上去",
        "下去", "这边走", "快跑", "快撤", "小心", "当心", "注意", "看看", "等一下我", "给我",
        "帮我", "别管", "先走", "跟上", "等我", "来", "去", "说", "看", "听",
        "要", "想", "用", "弄", "搞", "整", "玩", "打", "死", "活",
        "掉", "丢", "捡", "进", "出", "开", "关", "真", "假", "对",
        "错", "行", "好的", "可以啊", "是啊", "对啊", "是啊是啊", "是这样", "这样的话", "然后呢",
        "所以呢", "你说", "我说", "他说"
    };

    /// <summary>统计用的停用词（统计词频时忽略；作为危险词匹配时不受影响）。</summary>
    public static readonly HashSet<string> Stopwords = new HashSet<string>(StringComparer.Ordinal)
    {
        "我", "你", "他", "她", "它", "我们", "你们", "他们", "的", "了", "是", "不",
        "这", "那", "这个", "那个", "这里", "那里", "也", "啊", "吧", "呢", "吗", "嘛",
        "就", "都", "很", "在", "和", "有", "会", "说", "个", "一", "上", "下", "没",
        "要", "来", "去", "好", "对", "然后", "什么", "怎么", "这样", "那样", "一下",
        "一点", "一个", "时候", "现在", "知道", "觉得", "可以", "应该", "可能", "还是",
        "但是", "因为", "所以", "如果", "而且", "不过", "已经", "还有", "就是", "不是",
        "没有", "自己", "大家", "真的", "其实", "感觉", "东西", "地方",
        "the", "a", "is", "to", "of", "and", "in", "it", "you", "that", "this",
    };

    // ---------------- 词库文件 ----------------

    public const string FileName = "词库.txt";

    /// <summary>
    /// 从「词库.txt」加载；文件不存在就按当前内置词生成一份。
    /// 格式：一行一个词，# 开头是注释，[口语] / [正式] / [高频] 分节。
    /// </summary>
    public static bool LoadOrCreate(string path)
    {
        try
        {
            if (!File.Exists(path)) { SaveTemplate(path); return true; }
            return Load(path);
        }
        catch
        {
            // Core 层不依赖 Celeste，失败就静默用内置词库（模块那边会记日志）
            return false;
        }
    }

    public static bool Load(string path)
    {
        var casual = new List<string>();
        var formal = new List<string>();
        var hell = new List<string>();
        List<string> cur = casual;

        foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;

            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                string sec = line.Substring(1, line.Length - 2).Trim();
                if (sec == "正式") cur = formal;
                else if (sec == "高频") cur = hell;
                else cur = casual;
                continue;
            }

            if (cur.Contains(line)) continue;
            cur.Add(line);
        }

        // 三节全空的话说明文件写坏了，别把词库清空
        if (casual.Count == 0 && formal.Count == 0 && hell.Count == 0) return false;

        Casual = casual;
        Formal = formal;
        Hell = hell;
        RebuildKnown();
        return true;
    }

    public static void SaveTemplate(string path)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("# VoiceKill 词库 —— 一行一个词，# 开头是注释，空行会被忽略");
            sb.AppendLine("# 改完保存，重启游戏生效（面板里改更方便，改的是自定义/高频那几个分类）");
            sb.AppendLine();
            sb.AppendLine("[口语]   # 玩家边跑边喊的：动作、物件、状态");
            foreach (string w in Casual) sb.AppendLine(w);
            sb.AppendLine();
            sb.AppendLine("[正式]   # 地图名、英文术语（super/hyper/wavedash 这种社区说法）");
            foreach (string w in Formal) sb.AppendLine(w);
            sb.AppendLine();
            sb.AppendLine("[高频]   # 中文口语里几乎每句都有的词，说出来必死；含单字是有意的");
            foreach (string w in Hell) sb.AppendLine(w);

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }
        catch
        {
            // 同上，写不出来就算了
        }
    }

    /// <summary>面板里加进来的自定义词。</summary>
    public static readonly List<string> Custom = new List<string>();

    private static readonly HashSet<string> AllKnown = BuildKnown();
    private static readonly object KnownLock = new object();

    private static HashSet<string> BuildKnown()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (string w in Casual) set.Add(TextNormalizer.Normalize(w));
        foreach (string w in Formal) set.Add(TextNormalizer.Normalize(w));
        foreach (string w in Hell) set.Add(TextNormalizer.Normalize(w));
        return set;
    }

    private static void RebuildKnown()
    {
        lock (KnownLock)
        {
            AllKnown.Clear();
            foreach (string w in Casual) AllKnown.Add(TextNormalizer.Normalize(w));
            foreach (string w in Formal) AllKnown.Add(TextNormalizer.Normalize(w));
            foreach (string w in Hell) AllKnown.Add(TextNormalizer.Normalize(w));
            foreach (string w in Custom) AllKnown.Add(TextNormalizer.Normalize(w));
        }
    }

    public static void SetCustom(IEnumerable<string> words)
    {
        lock (KnownLock)
        {
            Custom.Clear();
            if (words != null)
            {
                foreach (string w in words)
                {
                    if (string.IsNullOrWhiteSpace(w)) continue;
                    string t = w.Trim();
                    if (!Custom.Contains(t)) Custom.Add(t);
                }
            }

            AllKnown.Clear();
            foreach (string w in Casual) AllKnown.Add(TextNormalizer.Normalize(w));
            foreach (string w in Formal) AllKnown.Add(TextNormalizer.Normalize(w));
            foreach (string w in Hell) AllKnown.Add(TextNormalizer.Normalize(w));
            foreach (string w in Custom) AllKnown.Add(TextNormalizer.Normalize(w));
        }
    }

    public static bool IsKnown(string word)
    {
        if (string.IsNullOrEmpty(word)) return false;
        lock (KnownLock) return AllKnown.Contains(TextNormalizer.Normalize(word));
    }
}
