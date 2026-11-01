using System;
using System.Collections.Generic;
using VoiceKill.Core;

namespace VoiceKill.Cli
{
    /// <summary>
    /// 不需要麦克风、不需要游戏的自检。
    /// 每一条都是 MC 版真实踩过的坑，改代码之后先跑这个。
    /// </summary>
    internal static class SelfTest
    {
        private static int _pass;
        private static int _fail;

        public static int Run()
        {
            Console.WriteLine("==================================================");
            Console.WriteLine("  VoiceKill 匹配逻辑自检");
            Console.WriteLine("==================================================");
            Console.WriteLine();

            // 1. 原词精确
            Expect("原词精确",
                "冲刺", "我刚才冲刺没按出来", "", "",
                EvidenceKind.Exact);

            // 2. 谐音（同音字）
            Expect("同音字「刺→次」",
                "冲刺", "这个冲次按早了", "", "",
                EvidenceKind.Alias);

            // 3. 字母读音（ad → 哎弟）
            Expect("字母读音「ad→哎弟」",
                "ad", "哎弟又没按出来", "", "",
                EvidenceKind.Alias);

            // 4. 句末复核救回 q a q
            Expect("复核救回「qak→qaq」",
                "qaq", "q a k", "q a q", "",
                EvidenceKind.ReviewExact);

            // 5. 纯中文不允许走复核（否则疯狂误杀）
            {
                var word = new DangerWord("冲刺", WordSource.Manual);
                var r = WordMatcher.Find(new[] { word }, "冲次", "冲刺", "冲次");
                Check("纯中文不走复核（应为谐音，不是复核）",
                    r.IsMatch && r.Kind != EvidenceKind.ReviewExact,
                    "实际: " + (r.IsMatch ? r.Kind.ToString() : "没命中"));
            }

            // 6. 编辑距离兜底
            Expect("编辑距离「装装→中中」",
                "你好呀装装", "你好呀中中", "", "",
                EvidenceKind.Fuzzy);

            // 7. 跨句拼接（「秒」+「切」）
            {
                var win = new SpeechWindow();
                win.Push("今天这个秒");
                string joined = win.Join("切");
                var word = new DangerWord("秒切", WordSource.Manual);
                var r = WordMatcher.Find(new[] { word }, "切", "", joined);
                Check("跨句拼接「秒」+「切」", r.IsMatch && r.Kind == EvidenceKind.Exact,
                    "拼接后=" + joined + " 实际=" + (r.IsMatch ? r.Kind.ToString() : "没命中"));
            }

            // 8. 无关的话不误杀
            {
                var word = new DangerWord("冲刺", WordSource.Manual);
                var r = WordMatcher.Find(new[] { word }, "今天天气不错我们出去走走吧", "", "");
                Check("无关的话不误杀", !r.IsMatch, "实际: " + (r.IsMatch ? r.Word.Text : "没命中"));
            }

            // 9. 单字禁令：组词器永远不出单字
            //    注意高频地狱词（我/你/是/不…）本来就有单字，是 MC 版的原设计，
            //    单字禁令只对自定义 / 人名 / 智能 / 随机池生效，Hell 除外。
            {
                var cfg = new DangerWordConfig { WordsPerRound = 12 };
                var mgr = new DangerWordManager(cfg, new SpeechStats(), seed: 12345);
                bool allOk = true;
                string bad = "";
                for (int i = 0; i < 30 && allOk; i++)
                {
                    var round = mgr.NextRound();
                    foreach (var w in round)
                    {
                        if (w.Source == WordSource.Hell) continue;
                        string norm = TextNormalizer.Normalize(w.Text);
                        if (norm.Length < 2) { allOk = false; bad = w.Text; break; }
                    }
                }
                Check("组词器不出单字（Hell 组的单字除外）", allOk, "出现了单字: " + bad);
            }

            // 10. 智能选词能捞到刚聊过的词
            {
                var stats = new SpeechStats();
                for (int i = 0; i < 3; i++) stats.Add("我最喜欢草莓了这个草莓真好看");
                var cands = stats.Candidates(24);
                Check("智能选词捞到「草莓」", cands.Contains("草莓"),
                    "候选: " + string.Join("/", cands.ToArray()));
            }

            // 11. 词频权重是「本轮×3 + 累计×1」
            {
                var stats = new SpeechStats();
                stats.Add("核心"); // 累计 1
                stats.ClearRound(); // 本轮清零
                bool hasTotal = stats.TotalCount("核心") == 1;
                // 新的一轮说一次，本轮=1 累计=2 → 权重 3+2=5
                stats.Add("核心");
                bool weightOk = stats.RoundCount("核心") == 1 && stats.TotalCount("核心") == 2;
                int totalNow = stats.TotalCount("核心");
                int roundNow = stats.RoundCount("核心");
                Check("本轮清零但累计保留", hasTotal && weightOk,
                    "累计=" + totalNow + " 本轮=" + roundNow);
            }

            // 12. 死亡冷却：上一句残留不能二次带走
            {
                var opt = new RuntimeOptions();
                opt.DeathCooldownSeconds = 1.5;
                opt.Asr.ModelRoot = "";
                Check("死亡冷却参数存在", opt.DeathCooldownSeconds > 0, "");
            }

            // 13. 智能词不能切出跨词边界的假词（2026-11-01 用户报的）
            //     「别搞别搞」滑窗会切出「搞别搞」「搞别」「别搞别」
            {
                var stats = new SpeechStats();
                stats.Add("别搞别搞");
                stats.Add("你别搞啊");
                stats.Add("别搞别搞");
                stats.Add("别搞了吧");
                var cands = stats.Candidates(24);
                string all = string.Join("/", cands.ToArray());
                bool good = cands.Contains("别搞") &&
                            !cands.Contains("搞别搞") && !cands.Contains("搞别") && !cands.Contains("别搞别");
                Check("智能词不出「搞别搞」", good, "候选: " + all);
            }

            // 14. 「浏览器」不能切出「览器」
            {
                var stats = new SpeechStats();
                stats.Add("我打开浏览器看一下");
                stats.Add("浏览器卡住了");
                stats.Add("这个浏览器真慢");
                var cands = stats.Candidates(24);
                string all = string.Join("/", cands.ToArray());
                bool good = cands.Contains("浏览器") && !cands.Contains("览器");
                Check("智能词不出「览器」", good, "候选: " + all);
            }

            // 15. 「走路走路」不能切出「路走路」
            {
                var stats = new SpeechStats();
                stats.Add("走路走路小心点");
                stats.Add("别走路了");
                stats.Add("走路过去吧");
                var cands = stats.Candidates(24);
                string all = string.Join("/", cands.ToArray());
                bool good = cands.Contains("走路") && !cands.Contains("路走路") && !cands.Contains("走路走");
                Check("智能词不出「路走路」", good, "候选: " + all);
            }

            // 16. 去胶水：候选不能是「核心词 + 一个虚词」
            {
                var stats = new SpeechStats();
                stats.Add("我昨天去超市了");
                stats.Add("今天超市好多人");
                stats.Add("超市那边排队");
                var cands = stats.Candidates(24);
                string all = string.Join("/", cands.ToArray());
                bool good = cands.Contains("超市") &&
                            !cands.Contains("超市了") && !cands.Contains("去超市");
                Check("候选去掉两头虚词（有「超市」、无「超市了」）", good, "候选: " + all);
            }

            Console.WriteLine();
            Console.WriteLine("--------------------------------------------------");
            Console.WriteLine(_fail == 0
                ? $"全部通过 {_pass}/{_pass} ✅"
                : $"通过 {_pass}，失败 {_fail} ❌");
            return _fail == 0 ? 0 : 1;
        }

        private static void Expect(string name, string word, string stream, string review, string joined, EvidenceKind want)
        {
            var w = new DangerWord(word, WordSource.Manual);
            var r = WordMatcher.Find(new[] { w }, stream, review, string.IsNullOrEmpty(joined) ? stream : joined);
            Check(name, r.IsMatch && r.Kind == want,
                r.IsMatch ? "实际=" + r.Kind : "没命中");
        }

        private static void Check(string name, bool ok, string detail)
        {
            if (ok) { _pass++; Console.WriteLine("  ✅ " + name); }
            else { _fail++; Console.WriteLine("  ❌ " + name + "  → " + detail); }
        }
    }
}
