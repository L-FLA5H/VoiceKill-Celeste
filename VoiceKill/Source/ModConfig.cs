using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using VoiceKill.Core;

namespace Celeste.Mod.VoiceKill;

/// <summary>
/// 真正的配置。
///
/// 为什么不放 Everest 设置菜单里：
///   1. 菜单不支持折叠/分组，只能平铺，几十项混在一起没人看得懂；
///   2. 这个版本的 Everest 连 [SettingName] 都不读，中文化只能靠写中文属性名，很脏；
///   3. 菜单里的输入框有长度限制，IP、词库这种根本填不下。
///
/// 所以：菜单只留「总开关 / 打开面板 / 备用设置」，其余全在 Mods\VoiceKill\voicekill.cfg，
/// 由网页面板读写。格式是 key=value 一行一条，手改也不容易错。
/// </summary>
public sealed class ModConfig
{
    // ---- 总开关 ----
    public bool Enabled = true;

    // ---- 玩法 ----
    public int WordsPerRound = 8;
    public int HighFreqPerRound = 3;
    public int RoundSeconds = 30;
    public int SmartPickPercent = 60;
    /// <summary>每轮最多塞几个「智能选词」（你说的词）。0 = 一开就不塞。</summary>
    public int SmartPerRound = 2;
    /// <summary>每轮塞几个人名（从「人名危险词」里抽）。0 = 不塞。</summary>
    public int NamesPerRound = 1;
    /// <summary>每轮塞几个游戏词（口语 / 正式 / 自定义）。0 = 只在补满时随机撞上。</summary>
    public int GamePerRound = 0;
    /// <summary>「游戏词个数」这项是 0.9.7 才加的，老配置迁移用。</summary>
    public bool GameQuotaMigrated = false;
    public string SpeakerName = "";
    public bool ShowInGameHud = true;
    public int HudScalePercent = 100;

    // ---- 识别 ----
    public bool MatchOnTheFly = true;
    public bool AllowFuzzy = true;
    public int InferenceThreads = 4;
    public string ModelRoot = @"D:\.minecraft\versions\1.21.11-Fabric 0.18.4\voicekill\models";

    // ---- 声音 ----
    public AudioSourceKind AudioSource = AudioSourceKind.麦克风;
    public int MicDeviceIndex = 0;
    public string MicDeviceName = "";
    public string MicDisplay = "";

    // ---- 房间 ----
    public RoomRole RoomMode = RoomRole.关闭;
    public string RoomKey = "voicekill";
    public string RoomHostUrl = "";
    public DeathScope DeathScope = DeathScope.只死自己;
    public bool OfflineSync = false;
    /// <summary>只有房主能改词；关了之后任何人都能改，改动会转发给房主统一生效。</summary>
    public bool OnlyHostCanEdit = true;


    // ---- 词库 ----
    public string CustomWords = "";
    /// <summary>高频地狱词里额外加进去的（内置之外）。</summary>
    public string HighFreqExtra = "";
    /// <summary>高频地狱词里被停用的（内置但不想用）。</summary>
    public string HighFreqDisabled = "";
    /// <summary>启用的人名（每轮随机塞 1 个）。</summary>
    public string Names = "";
    /// <summary>停用但保留的人名 —— 不删除，只是不参与抽词。</summary>
    public string DisabledNames = "";

    // ---- 面板 ----
    // ---- 网页面板 ----
    public bool WebPanel = true;
    public int WebPort = 38471;

    private static string PathFor(string modDir)
        => Path.Combine(modDir, "voicekill.cfg");

    public static ModConfig Load(string modDir)
    {
        var cfg = new ModConfig();
        try
        {
            string path = PathFor(modDir);
            if (!File.Exists(path)) { cfg.Save(modDir); return cfg; }

            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string k = line.Substring(0, eq).Trim();
                string v = line.Substring(eq + 1).Trim();
                cfg.Set(k, v);
            }
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Error, "VoiceKill", "读取配置失败，用默认值: " + ex.Message);
        }
        return cfg;
    }

    public void Save(string modDir)
    {
        try
        {
            string dir = string.IsNullOrEmpty(modDir)
                ? Path.Combine(AppContext.BaseDirectory, "Mods", "VoiceKill")
                : modDir;
            Directory.CreateDirectory(dir);

            var sb = new StringBuilder();
            sb.AppendLine("# VoiceKill 配置 —— 一般不用手改，都在面板里点");
            sb.AppendLine("# 面板打不开时：游戏设置 → VoiceKill → 备用设置 → 打开配置文件夹");
            foreach (var kv in ToPairs()) sb.AppendLine(kv.Key + "=" + kv.Value);
            File.WriteAllText(PathFor(dir), sb.ToString(), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Error, "VoiceKill", "保存配置失败: " + ex.Message);
        }
    }

    public List<KeyValuePair<string, string>> ToPairs()
    {
        var list = new List<KeyValuePair<string, string>>();
        void Add(string k, object v) => list.Add(new KeyValuePair<string, string>(k, Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""));

        Add("Enabled", Enabled ? "1" : "0");
        Add("WordsPerRound", WordsPerRound);
        Add("HighFreqPerRound", HighFreqPerRound);
        Add("RoundSeconds", RoundSeconds);
        Add("SmartPickPercent", SmartPickPercent);
        Add("SmartPerRound", SmartPerRound);
        Add("NamesPerRound", NamesPerRound);
        Add("GamePerRound", GamePerRound);
        Add("GameQuotaMigrated", GameQuotaMigrated ? "1" : "0");
        Add("SpeakerName", SpeakerName);
        Add("ShowInGameHud", ShowInGameHud ? "1" : "0");
        Add("HudScalePercent", HudScalePercent);
        Add("MatchOnTheFly", MatchOnTheFly ? "1" : "0");
        Add("AllowFuzzy", AllowFuzzy ? "1" : "0");
        Add("InferenceThreads", InferenceThreads);
        Add("ModelRoot", ModelRoot);
        Add("AudioSource", AudioSource.ToString());
        Add("MicDeviceIndex", MicDeviceIndex);
        Add("MicDeviceName", MicDeviceName);
        Add("MicDisplay", MicDisplay);
        Add("RoomMode", RoomMode.ToString());
        Add("RoomKey", RoomKey);
        Add("RoomHostUrl", RoomHostUrl);
        Add("DeathScope", DeathScope.ToString());
        Add("OfflineSync", OfflineSync ? "1" : "0");
        Add("OnlyHostCanEdit", OnlyHostCanEdit ? "1" : "0");
        Add("CustomWords", CustomWords);
        Add("HighFreqExtra", HighFreqExtra);
        Add("HighFreqDisabled", HighFreqDisabled);
        Add("Names", Names);
        Add("DisabledNames", DisabledNames);
        Add("WebPanel", WebPanel ? "1" : "0");
        Add("WebPort", WebPort);
        return list;
    }

    public void Set(string key, string v)
    {
        switch (key)
        {
            case "Enabled": Enabled = Bool(v, Enabled); break;
            case "WordsPerRound": WordsPerRound = Int(v, WordsPerRound); break;
            case "HighFreqPerRound": HighFreqPerRound = Int(v, HighFreqPerRound); break;
            case "RoundSeconds": RoundSeconds = Int(v, RoundSeconds); break;
            case "SmartPickPercent": SmartPickPercent = Int(v, SmartPickPercent); break;
            case "SmartPerRound": SmartPerRound = Int(v, SmartPerRound); break;
            case "NamesPerRound": NamesPerRound = Int(v, NamesPerRound); break;
            case "GamePerRound": GamePerRound = Int(v, GamePerRound); break;
            case "GameQuotaMigrated": GameQuotaMigrated = Bool(v, GameQuotaMigrated); break;
            case "SpeakerName": SpeakerName = v; break;
            case "ShowInGameHud": ShowInGameHud = Bool(v, ShowInGameHud); break;
            case "HudScalePercent": HudScalePercent = Int(v, HudScalePercent); break;
            case "MatchOnTheFly": MatchOnTheFly = Bool(v, MatchOnTheFly); break;
            case "AllowFuzzy": AllowFuzzy = Bool(v, AllowFuzzy); break;
            case "InferenceThreads": InferenceThreads = Int(v, InferenceThreads); break;
            case "ModelRoot": ModelRoot = v; break;
            case "AudioSource": AudioSource = Enum(v, AudioSource); break;
            case "MicDeviceIndex": MicDeviceIndex = Int(v, MicDeviceIndex); break;
            case "MicDeviceName": MicDeviceName = v; break;
            case "MicDisplay": MicDisplay = v; break;
            case "RoomMode": RoomMode = Enum(v, RoomMode); break;
            case "RoomKey": RoomKey = v; break;
            case "RoomHostUrl": RoomHostUrl = v; break;
            case "DeathScope": DeathScope = Enum(v, DeathScope); break;
            case "OfflineSync": OfflineSync = Bool(v, OfflineSync); break;
            case "OnlyHostCanEdit": OnlyHostCanEdit = Bool(v, OnlyHostCanEdit); break;
            case "CustomWords": CustomWords = v; break;
            case "HighFreqExtra": HighFreqExtra = v; break;
            case "HighFreqDisabled": HighFreqDisabled = v; break;
            case "Names": Names = v; break;
            case "DisabledNames": DisabledNames = v; break;
            case "WebPanel": WebPanel = Bool(v, WebPanel); break;
            case "WebPort": WebPort = Int(v, WebPort); break;
            // 旧键兼容（0.9.3 之前叫 OverlayXxx）
            case "OverlayEnabled": WebPanel = Bool(v, WebPanel); break;
            case "OverlayPort": WebPort = Int(v, WebPort); break;
        }
    }

    private static int Int(string v, int fallback)
        => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : fallback;

    private static bool Bool(string v, bool fallback)
        => v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);

    private static T Enum<T>(string v, T fallback) where T : struct
        => System.Enum.TryParse(v, out T parsed) ? parsed : fallback;
}

/// <summary>声音从哪来。</summary>
public enum AudioSourceKind
{
    /// <summary>自己的麦克风（默认）。</summary>
    麦克风 = 0,

    /// <summary>系统输出回环 —— 抓 OOPZ / QQ 通话里所有人的声音。</summary>
    系统声音 = 1,
}

/// <summary>房间模式里的角色。</summary>
public enum RoomRole
{
    关闭 = 0,
    主机 = 1,
    客户端 = 2,
}

/// <summary>踩雷之后谁倒霉。</summary>
public enum DeathScope
{
    只死自己 = 0,
    全员连坐 = 1,
}
