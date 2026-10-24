using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Celeste;
using Celeste.Mod;
using VoiceKill.Core;

namespace Celeste.Mod.VoiceKill;

public class VoiceKillModule : EverestModule
{
    public static VoiceKillModule Instance { get; private set; }

    public override Type SettingsType => typeof(VoiceKillSettings);
    public static VoiceKillSettings Settings => (VoiceKillSettings)Instance._Settings;

    private const string Tag = "VoiceKill";

    private static string _binDir = "";
    private static string _modDir = "";
    private static ModConfig _cfg = new ModConfig();

    /// <summary>当前生效的配置（面板读写的就是它）。</summary>
    public static ModConfig Cfg => _cfg;

    private VoiceKillRuntime _runtime;
    private MicCapture _mic;
    private PanelServer _server;
    private RoomSync _room;

    private volatile bool _engineReady;
    private volatile bool _startFailed;
    private volatile Verdict _pending;
    private volatile bool _remoteKill;
    private volatile bool _rotateRequested;
    private volatile bool _configDirty;
    private string _micProblem = "";
    private int _actualPort;
    private string _fatal = "";
    private static bool _zipInstall;

    private readonly object _lock = new object();
    private List<DangerWord> _round = new List<DangerWord>();
    private readonly List<LogItem> _log = new List<LogItem>();
    private readonly List<RoundRecord> _history = new List<RoundRecord>();
    private List<string> _nextWords = new List<string>();
    private List<string> _pendingRemoteWords;
    private string _lastText = "";
    private string _lastWord = "";
    private string _lastKind = "";
    private int _deaths;
    private int _deathSeq;
    private DateTime _hitAt = DateTime.MinValue;
    private string _hitWord = "";
    private string _hitSpeaker = "";

    private sealed class LogItem
    {
        public string T;
        public string Text;
        public bool Kill;
    }

    private sealed class RoundRecord
    {
        public string T;
        public string Text;
    }

    public VoiceKillModule()
    {
        Instance = this;
        Logger.SetLogLevel(Tag, LogLevel.Info);
    }

    // ---------------- 依赖与原生库 ----------------

    /// <summary>
    /// 找 mod 的 bin 目录。
    ///
    /// 踩过的坑：2026-10-24 有用户反馈面板打不开，日志里是
    /// `bin 目录: ''` → 原生库准备失败 → 整个功能停用 → HTTP 服务也没起来。
    /// 原因是我用 `Mods\VoiceKill\bin` 猜路径，而他按安装说明把文件夹解压成了
    /// `VoiceKill-蔚蓝版-v0.7.1`，猜不中。
    ///
    /// 所以这里按「从最可靠到最兜底」依次尝试，最后一步直接扫 Mods 目录找我们的 dll。
    /// </summary>
    private static string ResolveBinDir()
    {
        // 1) Everest 给的 mod 目录（最可靠，但加载方式不同时可能是空）
        try
        {
            string pathDir = Instance?.Metadata?.PathDirectory;
            if (!string.IsNullOrEmpty(pathDir))
            {
                string bin = Path.Combine(pathDir, "bin");
                if (Directory.Exists(bin)) return bin;
            }
        }
        catch { }

        // 2) 程序集实际位置（正常加载时就是 ...\Mods\<名字>\bin\VoiceKill.dll）
        try
        {
            string loc = typeof(VoiceKillModule).Assembly.Location;
            if (!string.IsNullOrEmpty(loc))
            {
                string dir = Path.GetDirectoryName(loc);
                if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "VoiceKill.dll"))) return dir;
            }
        }
        catch { }

        // 3) 扫 Mods：不管文件夹叫什么名字，只要里面有 bin\VoiceKill.dll 就是它
        foreach (string root in CandidateModRoots())
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (string dir in Directory.GetDirectories(root))
                {
                    string bin = Path.Combine(dir, "bin");
                    if (File.Exists(Path.Combine(bin, "VoiceKill.dll"))) return bin;
                }
            }
            catch { }
        }

        string arch = "";
        try { arch = Instance?.Metadata?.PathArchive ?? ""; } catch { }

        if (arch.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            Logger.Log(LogLevel.Error, Tag,
                "这个 Mod 是以 **zip 方式** 安装的（" + arch + "）。" +
                "原生识别库必须从真实文件加载，zip 里读不到 —— 请把 zip 解压成文件夹 Mods\\VoiceKill\\ 再启动。");
            _zipInstall = true;
            return "";
        }

        Logger.Log(LogLevel.Error, Tag,
            "找不到 bin 目录。试过：PathDirectory='" + (Instance?.Metadata?.PathDirectory ?? "") +
            "'，Archive='" + arch + "'，Assembly='" + SafeLocation() + "'，以及 Mods 目录扫描。");
        return "";
    }

    private static string SafeLocation()
    {
        try { return typeof(VoiceKillModule).Assembly.Location ?? ""; } catch { return ""; }
    }

    /// <summary>可能放着 Mods 目录的若干位置（游戏根目录优先，然后是上两级）。</summary>
    private static IEnumerable<string> CandidateModRoots()
    {
        var list = new List<string>();
        try
        {
            string baseDir = AppContext.BaseDirectory;
            list.Add(Path.Combine(baseDir, "Mods"));

            string loc = SafeLocation();
            if (!string.IsNullOrEmpty(loc))
            {
                DirectoryInfo ld = new DirectoryInfo(Path.GetDirectoryName(loc) ?? "");
                for (int i = 0; i < 4 && ld != null; i++, ld = ld.Parent)
                    list.Add(Path.Combine(ld.FullName, "Mods"));
            }
            DirectoryInfo up = Directory.GetParent(baseDir.TrimEnd(Path.DirectorySeparatorChar));
            if (up != null)
            {
                list.Add(Path.Combine(up.FullName, "Mods"));
                if (up.Parent != null) list.Add(Path.Combine(up.Parent.FullName, "Mods"));
            }
        }
        catch { }
        return list;
    }

    private void PrepareNative()
    {
        _binDir = ResolveBinDir();
        Logger.Log(LogLevel.Info, Tag, "bin 目录: '" + _binDir + "'");

        if (!NativeBootstrap.EnsureLoaded(_binDir, out string error))
        {
            _startFailed = true;
            _fatal = _zipInstall
                ? "识别库加载失败：看起来是直接把 zip 丢进 Mods 了。请把 zip 解压成文件夹 Mods\\VoiceKill\\，再重启游戏。"
                : "原生库加载失败：" + error;
            Logger.Log(LogLevel.Error, Tag,
                "原生库准备失败: " + error + " —— 本次不启用，游戏不会崩。");
            return;
        }

        try
        {
            NativeLibrary.SetDllImportResolver(typeof(SherpaOnnx.OnlineRecognizer).Assembly,
                (name, asm, searchPath) =>
                {
                    if (name == "sherpa-onnx-c-api" && NativeBootstrap.ApiHandle != IntPtr.Zero)
                        return NativeBootstrap.ApiHandle;
                    return IntPtr.Zero;
                });
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warn, Tag, "DllImport 解析器注册失败: " + ex.Message);
        }

        Logger.Log(LogLevel.Info, Tag, "原生库就绪");
    }

    // ---------------- 生命周期 ----------------

    public override void Load()
    {
        PrepareNative();

        _modDir = string.IsNullOrEmpty(_binDir) ? "" : Path.GetDirectoryName(_binDir);
        _cfg = ModConfig.Load(_modDir);
        if (!string.IsNullOrEmpty(_modDir))
        {
            string dict = Path.Combine(_modDir, WordBank.FileName);
            bool ok = WordBank.LoadOrCreate(dict);
            Logger.Log(LogLevel.Info, Tag, (ok ? "词库: " : "词库读取失败，用内置: ") + dict);
        }

        WordBank.SetCustom(SplitWords(Cfg.CustomWords));

        On.Celeste.Level.Update += LevelUpdate;
        On.Celeste.Level.Render += LevelRender;

        Logger.Log(LogLevel.Info, Tag, "VoiceKill 已加载");
        Logger.Log(LogLevel.Info, Tag, $"本机局域网 IP: {string.Join(" / ", LocalIPs())}");

        // HTTP 服务单独起：模型坏了也能打开面板看报错，不至于「点了没反应」
        if (Cfg.WebPanel) StartWebPanel();
    }

    public override void Unload()
    {
        On.Celeste.Level.Update -= LevelUpdate;
        On.Celeste.Level.Render -= LevelRender;
        StopAll();
        VoiceKillHud.Clear();
        Logger.Log(LogLevel.Info, Tag, "VoiceKill 已卸载");
    }

    /// <summary>
    /// 自己搭设置菜单。
    /// Everest 的自动菜单只能生成「开关 / 数字 / 文本 / 下拉」，
    /// 想要按钮就得override这个（MiaoNet、CelesteTAS 等 mod 都是这么做的）。
    /// </summary>
    public override void CreateModMenuSection(TextMenu menu, bool inGame, FMOD.Studio.EventInstance snapshot)
    {
        Logger.Log(LogLevel.Info, Tag, "正在生成自定义设置菜单");
        menu.Add(new TextMenu.SubHeader("VoiceKill"));

        menu.Add(new TextMenu.Button("打开控制面板（推荐用这个设置）").Pressed(OpenPanelInBrowser));

        TextMenu.OnOff enabled = new TextMenu.OnOff("总开关", Cfg.Enabled);
        enabled.Change(v => { Cfg.Enabled = v; SaveCfgPublic(); });
        menu.Add(enabled);

        // 其余全部收进「备用设置」子菜单（面板打不开时的兜底）
        var sub = new TextMenuExt.SubMenu("备用设置（面板打不开时用）", false);

        sub.Add(new TextMenu.SubHeader("画面上的禁词"));

        TextMenu.OnOff hud = new TextMenu.OnOff("显示禁词", Cfg.ShowInGameHud);
        hud.Change(v => { Cfg.ShowInGameHud = v; SaveCfgPublic(); });
        sub.Add(hud);

        TextMenu.Slider hudSize = new TextMenu.Slider("禁词大小", i => i + "%", 40, 100, Cfg.HudScalePercent);
        hudSize.Change(i => { Cfg.HudScalePercent = i; SaveCfgPublic(); });
        sub.Add(hudSize);

        sub.Add(new TextMenu.SubHeader("出词方式"));

        TextMenu.Slider words = new TextMenu.Slider("每轮词数", i => i + " 个", 1, 20, Cfg.WordsPerRound);
        words.Change(i => { Cfg.WordsPerRound = i; SaveCfgPublic(); });
        sub.Add(words);

        TextMenu.Slider hell = new TextMenu.Slider("其中高频地狱词", i => i + " 个", 0, 10, Cfg.HighFreqPerRound);
        hell.Change(i => { Cfg.HighFreqPerRound = i; SaveCfgPublic(); });
        sub.Add(hell);

        TextMenu.Slider interval = new TextMenu.Slider("换词间隔", i => i + " 秒", 5, 120, Cfg.RoundSeconds);
        interval.Change(i => { Cfg.RoundSeconds = i; SaveCfgPublic(); });
        sub.Add(interval);

        TextMenu.Slider smart = new TextMenu.Slider("智能概率", i => i + "%", 0, 100, Cfg.SmartPickPercent);
        smart.Change(i => { Cfg.SmartPickPercent = i; SaveCfgPublic(); });
        sub.Add(smart);

        TextMenu.Slider smartCount = new TextMenu.Slider("智能词个数", i => i + " 个", 0, 8, Cfg.SmartPerRound);
        smartCount.Change(i => { Cfg.SmartPerRound = i; SaveCfgPublic(); });
        sub.Add(smartCount);

        sub.Add(new TextMenu.SubHeader("声音"));

        TextMenu.OnOff desktop = new TextMenu.OnOff("抓桌面音频（OOPZ/QQ 通话）", Cfg.AudioSource == AudioSourceKind.系统声音);
        desktop.Change(v =>
        {
            Cfg.AudioSource = v ? AudioSourceKind.系统声音 : AudioSourceKind.麦克风;
            SaveCfgPublic();
            RestartAudio();
        });
        sub.Add(desktop);

        sub.Add(new TextMenu.Button("换下一个麦克风").Pressed(CycleMic));

        sub.Add(new TextMenu.SubHeader("房间"));

        string[] roleNames = { "关闭（单人）", "主机（我开房）", "客户端（加入）" };
        TextMenu.Slider role = new TextMenu.Slider("房间模式", i => roleNames[i], 0, 2, (int)Cfg.RoomMode);
        role.Change(i => { Cfg.RoomMode = (RoomRole)i; SaveCfgPublic(); ApplyRoomMode(); });
        sub.Add(role);

        TextMenu.OnOff allDie = new TextMenu.OnOff("踩雷全员连坐", Cfg.DeathScope == DeathScope.全员连坐);
        allDie.Change(v =>
        {
            Cfg.DeathScope = v ? DeathScope.全员连坐 : DeathScope.只死自己;
            SaveCfgPublic();
            if (_room != null) _room.Scope = Cfg.DeathScope;
        });
        sub.Add(allDie);

        TextMenu.OnOff offline = new TextMenu.OnOff("离线同步（不联网）", Cfg.OfflineSync);
        offline.Change(v => { Cfg.OfflineSync = v; SaveCfgPublic(); });
        sub.Add(offline);

        TextMenu.OnOff onlyHost = new TextMenu.OnOff("仅房主可改词", Cfg.OnlyHostCanEdit);
        onlyHost.Change(v => { Cfg.OnlyHostCanEdit = v; SaveCfgPublic(); });
        sub.Add(onlyHost);

        sub.Add(new TextMenu.SubHeader("其它"));

        sub.Add(new TextMenu.Button("打开配置文件夹").Pressed(OpenConfigFolder));
        sub.Add(new TextMenu.Button("恢复默认配置").Pressed(ResetConfig));

        menu.Add(sub);
    }

    /// <summary>改了声音来源之后重连音频设备。</summary>
    private void RestartAudio()
    {
        try { _mic?.Dispose(); } catch { }
        _mic = null;

        VoiceKillRuntime runtime = _runtime;
        if (runtime != null)
        {
            var mic = new MicCapture();
            mic.OnSamples += samples => runtime.Feed(samples);
            _mic = mic;
        }
        if (_engineReady) StartMic();
    }

    /// <summary>设置菜单里那个按钮：打开浏览器控制面板。</summary>
    public void OpenPanelInBrowser()
    {
        // 万一面板服务没起来（端口被占 / 启动失败），点按钮时再试一次
        if (_server == null && Cfg.WebPanel) StartWebPanel();

        int port = _actualPort > 0 ? _actualPort : Cfg.WebPort;
        string url = $"http://localhost:{port}/panel";
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            Logger.Log(LogLevel.Info, Tag, "已打开面板: " + url);
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Error, Tag, "打开面板失败: " + ex.Message);
        }
    }

    private static void LevelUpdate(On.Celeste.Level.orig_Update orig, Level self)
    {
        orig(self);
        try { Instance?.Tick(self); }
        catch (Exception ex) { Logger.Log(LogLevel.Error, Tag, "Tick 异常: " + ex.Message); }
    }

    private static void LevelRender(On.Celeste.Level.orig_Render orig, Level self)
    {
        orig(self);
        try { Instance?.DrawHud(); }
        catch (Exception ex) { Logger.Log(LogLevel.Error, Tag, "HUD 渲染异常: " + ex.Message); }
    }

    private void DrawHud()
    {
        if (!Cfg.ShowInGameHud) return;
        if (_runtime == null || _startFailed) return;

        DangerWord[] words;
        lock (_lock) words = _round.ToArray();
        if (words.Length == 0) return;

        // 注意：这里传的是 0~100 的百分比，Render 内部再除以 100。
        // v0.2.0 就是这里多除了一次，72 变成 0.0072，整行只剩 17 像素看不见。
        VoiceKillHud.Render(words, Cfg.HudScalePercent);

        if (_hitAt != DateTime.MinValue)
        {
            float since = (float)(DateTime.Now - _hitAt).TotalSeconds;
            if (since > 3.0f) _hitAt = DateTime.MinValue;
            else VoiceKillHud.RenderHit(_hitWord, _hitSpeaker, since);
        }
    }

    private void Tick(Level level)
    {
        if (!Cfg.Enabled)
        {
            if (_runtime != null) StopAll();
            return;
        }

        StartAll();
        if (_runtime == null) return;

        if (_configDirty)
        {
            _configDirty = false;
            SaveCfg();
        }

        List<string> remote = null;
        lock (_lock) { if (_pendingRemoteWords != null) { remote = _pendingRemoteWords; _pendingRemoteWords = null; } }
        if (remote != null) _runtime.ApplyExternalRound(remote);

        if (_rotateRequested)
        {
            _rotateRequested = false;
            _runtime.RotateNow();
        }

        if (_remoteKill)
        {
            if (level.Transitioning || level.FrozenOrPaused) return;
            _remoteKill = false;
            Execute(level, new Verdict
            {
                Word = new DangerWord("队友踩雷", WordSource.Random),
                Kind = EvidenceKind.Exact,
                Via = "room"
            });
        }

        Verdict pending = _pending;
        if (pending == null) return;

        // 过场 / 暂停 / 死亡演出期间不处决（XaphanHelper 在切换房间时空引用，崩过两次）
        if (level.Transitioning || level.FrozenOrPaused)
        {
            if ((DateTime.Now - pending.At).TotalSeconds > 4) _pending = null;
            return;
        }

        _pending = null;
        Execute(level, pending);
    }

    private void Execute(Level level, Verdict verdict)
    {
        Player player = null;
        try { player = level.Entities?.FindFirst<Player>(); } catch { }
        if (player == null || player.Dead) return;

        try
        {
            player.Die(Microsoft.Xna.Framework.Vector2.Zero, false, true);
            Logger.Log(LogLevel.Info, Tag,
                $"处决: 「{verdict.Word?.Text}」({verdict.KindLabel}/{verdict.Via})");
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Error, Tag, "player.Die() 失败: " + ex.Message);
        }
    }

    // ---------------- 启动 / 停止 ----------------

    private void StartAll()
    {
        if (_runtime != null || _startFailed) return;

        // 模型目录支持相对路径：空 = 用 mod 目录下的 models，这样整包发出去也能直接跑
        string modelRoot = (Cfg.ModelRoot ?? "").Trim();
        if (modelRoot.Length == 0) modelRoot = Path.Combine(_modDir ?? "", "models");
        else if (!Path.IsPathRooted(modelRoot)) modelRoot = Path.Combine(_modDir ?? "", modelRoot);

        if (!Directory.Exists(modelRoot))
        {
            _startFailed = true;
            _fatal = "模型目录不存在：" + modelRoot;
            Logger.Log(LogLevel.Error, Tag, _fatal + " —— 去面板或「备用设置」里改「模型目录」");
            return;
        }

        var opt = new RuntimeOptions();
        opt.Asr.ModelRoot = modelRoot;
        opt.Asr.NativeLibraryDir = _binDir;
        opt.Asr.NumThreads = Math.Max(2, Cfg.InferenceThreads);
        opt.Asr.EndpointSilence = 0.35f;
        opt.Asr.MaxUtteranceSeconds = 4f;
        opt.MatchPartials = Cfg.MatchOnTheFly;
        opt.Words.WordsPerRound = Math.Max(1, Cfg.WordsPerRound);
        opt.Words.HighFreqPerRound = Math.Max(0, Cfg.HighFreqPerRound);
        opt.Words.SmartPerRound = Math.Max(0, Cfg.SmartPerRound);
        opt.Words.HighFreqExtra = SplitWords(Cfg.HighFreqExtra);
        opt.Words.HighFreqDisabled = SplitWords(Cfg.HighFreqDisabled);
        opt.Words.NamesPerRound = 0;   // 默认不塞人名，要用就自己填「玩家名」
        opt.Words.SmartPickChance = Cfg.OfflineSync
            ? 0.0   // 离线同步必须关掉智能选词，否则各自听到的话不同，词就不同步了
            : Math.Clamp(Cfg.SmartPickPercent, 0, 100) / 100.0;
        opt.SyncSeed = Cfg.OfflineSync ? StableHash(Cfg.RoomKey) : 0;
        // 客户端只认主机给的词，自己不再按计时器换（否则会闪一下错词）
        opt.ExternalOnly = Cfg.RoomMode == RoomRole.客户端;
        opt.Words.Names = SplitWords(Cfg.Names);
        if (opt.Words.Names.Count > 0) opt.Words.NamesPerRound = 1;
        opt.RoundSeconds = Cfg.RoundSeconds > 0 ? Cfg.RoundSeconds : 30;
        opt.AllowFuzzy = Cfg.AllowFuzzy;

        var runtime = new VoiceKillRuntime(opt);

        runtime.OnLog += msg =>
        {
            Logger.Log(LogLevel.Info, Tag, msg);
            if (msg.Contains("就绪")) { _engineReady = true; StartMic(); }
        };
        runtime.OnError += msg =>
        {
            // 只有「引擎没起来」才算致命；识别过程中的偶发异常记日志就好，
            // 别把整个功能判死（否则一次 NRE 就永久停摆，用户只看到红条）
            if (msg.StartsWith("模型加载失败"))
            {
                _startFailed = true;
                _fatal = msg;
            }
            Logger.Log(LogLevel.Error, Tag, msg);
        };

        runtime.OnRoundChanged += round =>
        {
            var copy = new List<DangerWord>(round);
            lock (_lock)
            {
                _round = copy;
                _history.Insert(0, new RoundRecord
                {
                    T = DateTime.Now.ToString("HH:mm:ss"),
                    Text = string.Join(" / ", copy.Select(w => w.Text))
                });
                while (_history.Count > 12) _history.RemoveAt(_history.Count - 1);
            }

            Logger.Log(LogLevel.Info, Tag, "新一组危险词: " + string.Join(" / ", copy));
            LogEvent("新一组（" + copy.Count + " 个）", false);

            RoomSync room = _room;
            if (room != null && room.IsHost) room.HostSetRound(Cfg.RoomKey, copy);
        };

        runtime.OnUtterance += utt =>
        {
            if (string.IsNullOrEmpty(utt.StreamingText)) return;
            lock (_lock) { _lastText = utt.DisplayText; }
            LogEvent("听到: " + utt.StreamingText, false);
        };

        runtime.OnDeath += verdict =>
        {
            lock (_lock)
            {
                _lastText = verdict.Utterance?.DisplayText ?? "";
                _lastWord = verdict.Word?.Text ?? "";
                _lastKind = verdict.KindLabel;
                _deaths++;
                _deathSeq++;
            }

            LogEvent("💀 " + (verdict.Word?.Text ?? "?") + "（" + verdict.KindLabel + "）" +
                     (verdict.Utterance?.StreamingText ?? ""), true);

            _hitAt = DateTime.Now;
            _hitWord = verdict.Word?.Text ?? "";
            _hitSpeaker = Speaker();

            runtime.OnPlayerDied();
            _pending = verdict;

            RoomSync room = _room;
            if (room != null)
            {
                string who = FirstName();
                string word = verdict.Word?.Text ?? "";
                if (room.IsHost) room.HostAddDeath(who, word);
                else if (room.IsClient) Task.Run(() => room.ReportDeath(who, word));
            }
        };

        _mic = new MicCapture();
        _mic.OnSamples += samples => runtime.Feed(samples);

        _runtime = runtime;
        runtime.Start();

        ApplyRoomMode();
    }

    private void ApplyRoomMode()
    {
        try { _room?.Dispose(); } catch { }
        _room = null;

        if (Cfg.RoomMode == RoomRole.关闭) return;

        string key = string.IsNullOrWhiteSpace(Cfg.RoomKey) ? "voicekill" : Cfg.RoomKey.Trim();

        var room = new RoomSync
        {
            IsHost = Cfg.RoomMode == RoomRole.主机,
            IsClient = Cfg.RoomMode == RoomRole.客户端,
            HostUrl = NormalizeHost(Cfg.RoomHostUrl),
            Scope = Cfg.DeathScope,
            Key = key,
            LocalRoomKey = key
        };
        room.OnRemoteRound += words => { lock (_lock) _pendingRemoteWords = new List<string>(words); };
        room.OnRemoteDeath += _ => { _remoteKill = true; };
        room.Start();

        _room = room;
        RebindServerSoon();
        Logger.Log(LogLevel.Info, Tag,
            Cfg.RoomMode == RoomRole.主机
                ? $"房间模式: 主机（口令 {key}），别人填 http://{FirstLocalIP()}:{Cfg.WebPort}"
                : "房间模式: 客户端 → " + Cfg.RoomHostUrl);
    }

    /// <summary>把 "192.168.1.10" / "192.168.1.10:38471" 补成完整 URL。</summary>
    private string NormalizeHost(string raw)
    {
        string s = (raw ?? "").Trim();
        if (s.Length == 0) return "";
        if (!s.StartsWith("http", StringComparison.OrdinalIgnoreCase)) s = "http://" + s;
        int schemeEnd = s.IndexOf("://", StringComparison.Ordinal) + 3;
        if (s.IndexOf(':', schemeEnd) < 0) s += ":" + Cfg.WebPort;
        return s.TrimEnd('/');
    }

    private void StartMic()
    {
        if (_mic == null || _mic.IsCapturing) return;

        bool loopback = Cfg.AudioSource == AudioSourceKind.系统声音;
        if (_mic.Start(Cfg.MicDeviceIndex, Cfg.MicDeviceName, loopback, out string error))
        {
            _micProblem = "";

            if (Cfg.MicDeviceName != _mic.DeviceName)
            {
                Cfg.MicDeviceName = _mic.DeviceName;
                Cfg.MicDeviceIndex = _mic.DeviceIndex;
                SaveCfg();
            }

            Cfg.MicDisplay = _mic.DeviceName;
            Logger.Log(LogLevel.Info, Tag, $"麦克风已开启: [{_mic.DeviceIndex}] {_mic.DeviceName}");
        }
        else
        {
            _micProblem = error;
            Logger.Log(LogLevel.Error, Tag, "麦克风启动失败: " + error);
        }
    }

    /// <summary>设置里那个「换下一个麦克风」按钮。</summary>
    public void CycleMic()
    {
        if (Cfg.AudioSource == AudioSourceKind.系统声音)
        {
            Logger.Log(LogLevel.Info, Tag, "当前声音来源是「系统声音」，换麦克风不影响它");
            return;
        }

        List<string> names = MicCapture.DeviceNames();
        if (names.Count == 0)
        {
            Logger.Log(LogLevel.Warn, Tag, "找不到任何录音设备");
            return;
        }

        int next = (Cfg.MicDeviceIndex + 1) % names.Count;
        SwitchMic(next);
    }

    /// <summary>切到指定编号的麦克风（面板下拉框用）。</summary>
    public void SwitchMic(int index)
    {
        List<string> names = MicCapture.DeviceNames();
        if (names.Count == 0) return;
        if (index < 0 || index >= names.Count) index = 0;

        Cfg.MicDeviceIndex = index;
        Cfg.MicDeviceName = names[index];
        Cfg.MicDisplay = names[index];

        try { _mic?.Dispose(); } catch { }
        _mic = null;

        VoiceKillRuntime runtime = _runtime;
        if (runtime != null)
        {
            var mic = new MicCapture();
            mic.OnSamples += samples => runtime.Feed(samples);
            _mic = mic;
        }

        if (_engineReady) StartMic();
        _configDirty = true;

        Logger.Log(LogLevel.Info, Tag, $"切换麦克风 → [{index}] {names[index]}");
    }

    private void StartWebPanel()
    {
        if (_server != null) return;

        // 只有开房当主机时才对外监听；其它情况只听 127.0.0.1，别人扫不到端口
        bool lan = Cfg.RoomMode == RoomRole.主机;

        // 端口被占用（上次没退干净、或别的程序占了）就往后试几个，别直接放弃
        int port = Cfg.WebPort;
        for (int i = 0; i < 8; i++, port++)
        {
            var candidate = new PanelServer(port, HandleRequest);
            if (!candidate.Start(lan, out string err))
            {
                Logger.Log(LogLevel.Warn, Tag, $"端口 {port} 启动失败（{err}），试下一个");
                candidate.Dispose();
                continue;
            }

            _server = candidate;
            _actualPort = port;
            if (port != Cfg.WebPort)
                Logger.Log(LogLevel.Warn, Tag, $"注意：{Cfg.WebPort} 被占用，面板改到 {port}");

            Logger.Log(LogLevel.Info, Tag,
                $"面板: http://localhost:{port}/panel" +
                (lan ? $"  （已对外开放，局域网可连 {FirstLocalIP()}:{port}）" : "  （仅本机）"));
            return;
        }

        Logger.Log(LogLevel.Error, Tag, $"38471~{port} 全都占用，HTTP 服务没起来");
    }

    /// <summary>房间模式变了要换监听地址，但又不能把当前的请求掐断，所以延后几百毫秒。</summary>
    private void RebindServerSoon()
    {
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500);
                try { _server?.Dispose(); } catch { }
                _server = null;
                if (Cfg.WebPanel) StartWebPanel();
            }
            catch { }
        });
    }

    private void StopAll()
    {
        try { _mic?.Dispose(); } catch { }
        _mic = null;

        try { _room?.Dispose(); } catch { }
        _room = null;

        try { _server?.Dispose(); } catch { }
        _server = null;

        try { _runtime?.Dispose(); } catch { }
        _runtime = null;

        _engineReady = false;
    }

    // ---------------- HTTP 面板接口 ----------------

    private string HandleRequest(string method, string path, string query, string body, bool isLocal)
    {
        if (isLocal) return HandleRequestInner(path, query, body, false);

        // 来自局域网的请求：只放行房间同步这几个接口，而且必须带对口令。
        // 其它（读状态、改设置、加词）一律拒绝 —— 开房时你的端口是开着的，
        // 不加这层的话同网段任何人都能读到你说了什么、甚至改你的设置。
        bool allowed = path == "/api/room"
                    || path == "/api/room/deaths"
                    || path == "/api/room/death"
                    || path == "/api/remote/edit";
        if (!allowed) return "{\"ok\":false,\"error\":\"该接口只允许本机访问\"}";

        string key = FormValue(body, "key");
        if (key.Length == 0) key = FormValue(query, "key");
        if (!string.Equals(key, Cfg.RoomKey, StringComparison.Ordinal))
            return "{\"ok\":false,\"error\":\"口令不对\"}";

        return HandleRequestInner(path, query, body, true);
    }

    /// <summary>
    /// 客户端改词的统一入口。
    /// 返回 null = 本机直接改；返回字符串 = 直接把这段 JSON 返回给面板。
    /// </summary>
    private string ClientEdit(string path, string body)
    {
        if (Cfg.RoomMode != RoomRole.客户端) return null;

        if (Cfg.OnlyHostCanEdit)
            return "{\"ok\":false,\"error\":\"房间设成了只有房主能改词\"}";

        RoomSync room = _room;
        if (room == null) return "{\"ok\":false,\"error\":\"没连上主机\"}";

        string action = path.StartsWith("/api/") ? path.Substring(5) : path;
        string payload = "action=" + Uri.EscapeDataString(action)
                         + "&key=" + Uri.EscapeDataString(Cfg.RoomKey)
                         + (body.Length > 0 ? "&" + body : "");
        return room.SendEdit(payload)
            ? "{\"ok\":true}"
            : "{\"ok\":false,\"error\":\"转发给房主失败\"}";
    }

    private string HandleRequestInner(string path, string query, string body, bool asHost)
    {
        // 会改词的接口：客户端要么被拒，要么转发给房主
        switch (path)
        {
            case "/api/round/add":
            case "/api/round/remove":
            case "/api/next":
            case "/api/next/clear":
            case "/api/next/hot":
            case "/api/rotate":
            case "/api/custom":
            case "/api/highfreq/add":
            case "/api/highfreq/toggle":
            case "/api/nameword/add":
            case "/api/nameword/toggle":
            case "/api/nameword/remove":
                if (!asHost)
                {
                    string forward = ClientEdit(path, body ?? "");
                    if (forward != null) return forward;
                }
                break;
        }

        switch (path)
        {
            case "/state":
                return BuildStateJson();

            case "/api/rotate":
                _rotateRequested = true;
                return "{\"ok\":true}";

            case "/api/round/add":
                _runtime?.AddWordToRound(FormValue(body, "text"));
                return "{\"ok\":true}";

            case "/api/round/remove":
                _runtime?.RemoveWordFromRound(FormValue(body, "text"));
                return "{\"ok\":true}";

            case "/api/next":
            {
                List<string> words = SplitWords(FormValue(body, "words"));
                _runtime?.ClearManual();
                if (words.Count > 0) _runtime?.EnqueueManual(words);
                lock (_lock) _nextWords = words;
                return "{\"ok\":true}";
            }

            case "/api/next/clear":
                _runtime?.ClearManual();
                lock (_lock) _nextWords = new List<string>();
                return "{\"ok\":true}";

            case "/api/next/hot":
            {
                // 「用最热的几个词当下一轮」
                List<string> hot = new List<string>();
                if (_runtime != null)
                {
                    foreach (var kv in _runtime.CurrentTop(5)) hot.Add(kv.Key);
                }
                _runtime?.ClearManual();
                if (hot.Count > 0) _runtime?.EnqueueManual(hot);
                lock (_lock) _nextWords = hot;
                return "{\"ok\":true,\"words\":\"" + RoomSync.Escape(string.Join(",", hot)) + "\"}";
            }

            case "/api/simulate":
            {
                string text = FormValue(body, "text");
                MatchResult r = _runtime?.Simulate(text) ?? MatchResult.None;
                return "{\"ok\":true,\"hit\":" + (r.IsMatch ? "true" : "false") +
                       ",\"word\":\"" + (r.IsMatch ? RoomSync.Escape(r.Word.Text) : "") +
                       "\",\"kind\":\"" + (r.IsMatch ? r.KindLabel : "") + "\"}";
            }

            case "/api/custom":
            {
                List<string> words = SplitWords(FormValue(body, "words"));
                WordBank.SetCustom(words);
                Cfg.CustomWords = string.Join(",", words);
                _configDirty = true;
                return "{\"ok\":true}";
            }

            case "/api/config":
                ApplyConfig(body);
                return "{\"ok\":true}";

            case "/api/remote/edit":
            {
                string key = FormValue(body, "key");
                if (!string.Equals(key, Cfg.RoomKey, StringComparison.Ordinal))
                    return "{\"ok\":false,\"error\":\"口令不对\"}";

                string action = FormValue(body, "action");
                if (action.Length == 0) return "{\"ok\":false,\"error\":\"没有 action\"}";
                return HandleRequestInner("/api/" + action, "", body, true);
            }

            case "/api/mode":
            {
                string m = FormValue(body, "mode");
                Cfg.RoomMode = m == "1" ? RoomRole.主机 : (m == "2" ? RoomRole.客户端 : RoomRole.关闭);
                ApplyRoomMode();
                SaveCfg();
                return "{\"ok\":true}";
            }

            case "/api/deathscope":
            {
                Cfg.DeathScope = FormValue(body, "scope") == "1" ? DeathScope.全员连坐 : DeathScope.只死自己;
                if (_room != null) _room.Scope = Cfg.DeathScope;
                SaveCfg();
                return "{\"ok\":true}";
            }

            case "/api/highfreq/add":
            {
                string w = FormValue(body, "word").Trim();
                if (w.Length == 0) return "{\"ok\":false}";
                List<string> extra = SplitWords(Cfg.HighFreqExtra);
                List<string> dis = SplitWords(Cfg.HighFreqDisabled);
                dis.Remove(w);
                if (!WordBank.Hell.Contains(w) && !extra.Contains(w)) extra.Add(w);
                Cfg.HighFreqExtra = string.Join(",", extra);
                Cfg.HighFreqDisabled = string.Join(",", dis);
                SaveCfg();
                return "{\"ok\":true}";
            }

            case "/api/highfreq/toggle":
            {
                string w = FormValue(body, "word").Trim();
                bool on = FormValue(body, "on") == "1";
                List<string> extra = SplitWords(Cfg.HighFreqExtra);
                List<string> dis = SplitWords(Cfg.HighFreqDisabled);
                bool builtin = WordBank.Hell.Contains(w);
                if (on)
                {
                    dis.Remove(w);
                    if (!builtin && !extra.Contains(w)) extra.Add(w);
                }
                else
                {
                    extra.Remove(w);
                    if (builtin && !dis.Contains(w)) dis.Add(w);
                }
                Cfg.HighFreqExtra = string.Join(",", extra);
                Cfg.HighFreqDisabled = string.Join(",", dis);
                SaveCfg();
                return "{\"ok\":true}";
            }

            case "/api/nameword/add":
            {
                string n = FormValue(body, "name").Trim();
                if (n.Length == 0) return "{\"ok\":false}";
                List<string> en = SplitWords(Cfg.Names);
                List<string> dis = SplitWords(Cfg.DisabledNames);
                if (!en.Contains(n)) en.Add(n);
                dis.Remove(n);
                Cfg.Names = string.Join(",", en);
                Cfg.DisabledNames = string.Join(",", dis);
                SaveCfg();
                return "{\"ok\":true}";
            }

            case "/api/nameword/toggle":
            {
                string n = FormValue(body, "name").Trim();
                bool on = FormValue(body, "on") == "1";
                List<string> en = SplitWords(Cfg.Names);
                List<string> dis = SplitWords(Cfg.DisabledNames);
                if (on) { if (!en.Contains(n)) en.Add(n); dis.Remove(n); }
                else { en.Remove(n); if (!dis.Contains(n)) dis.Add(n); }
                Cfg.Names = string.Join(",", en);
                Cfg.DisabledNames = string.Join(",", dis);
                SaveCfg();
                return "{\"ok\":true}";
            }

            case "/api/nameword/remove":
            {
                string n = FormValue(body, "name").Trim();
                List<string> en = SplitWords(Cfg.Names);
                List<string> dis = SplitWords(Cfg.DisabledNames);
                en.Remove(n); dis.Remove(n);
                Cfg.Names = string.Join(",", en);
                Cfg.DisabledNames = string.Join(",", dis);
                SaveCfg();
                return "{\"ok\":true}";
            }

            case "/api/room/join":
            {
                string host = FormValue(body, "host").Trim();
                string key = FormValue(body, "key").Trim();
                if (host.Length == 0) return "{\"ok\":false,\"error\":\"要填主机IP\"}";
                if (key.Length > 0) Cfg.RoomKey = key;
                Cfg.RoomHostUrl = host;
                Cfg.RoomMode = RoomRole.客户端;
                ApplyRoomMode();
                _configDirty = true;
                return "{\"ok\":true}";
            }

            case "/api/room/host":
            {
                string key = FormValue(body, "key").Trim();
                if (key.Length > 0) Cfg.RoomKey = key;
                Cfg.RoomMode = RoomRole.主机;
                ApplyRoomMode();
                _configDirty = true;
                return "{\"ok\":true}";
            }

            case "/api/room/leave":
                Cfg.RoomMode = RoomRole.关闭;
                ApplyRoomMode();
                _configDirty = true;
                return "{\"ok\":true}";

            case "/api/name":
            {
                string n = FormValue(body, "name").Trim();
                Cfg.SpeakerName = n;
                _configDirty = true;
                return "{\"ok\":true}";
            }

            case "/api/key":
            {
                string k = FormValue(body, "key").Trim();
                if (k.Length == 0) return "{\"ok\":false,\"error\":\"口令不能为空\"}";
                Cfg.RoomKey = k;
                _configDirty = true;
                if (_room != null) ApplyRoomMode();
                return "{\"ok\":true}";
            }

            case "/api/source":
            {
                string v = FormValue(body, "source");
                Cfg.AudioSource = v == "1" ? AudioSourceKind.系统声音 : AudioSourceKind.麦克风;
                try { _mic?.Dispose(); } catch { }
                _mic = null;
                VoiceKillRuntime rt = _runtime;
                if (rt != null)
                {
                    var mic = new MicCapture();
                    mic.OnSamples += samples => rt.Feed(samples);
                    _mic = mic;
                }
                if (_engineReady) StartMic();
                _configDirty = true;
                return "{\"ok\":true}";
            }

            case "/api/sync":
            {
                Cfg.OfflineSync = FormValue(body, "on") == "1";
                _startFailed = false;
                StopAll();   // 重新起引擎，让 SyncSeed 生效
                return "{\"ok\":true}";
            }

            case "/api/mic/list":
                return ArrayJson(MicCapture.DeviceNames().ToArray());

            case "/api/mic/set":
            {
                string raw = FormValue(body, "index");
                if (int.TryParse(raw, out int idx)) SwitchMic(idx);
                return "{\"ok\":true}";
            }

            case "/api/room":
                return _room?.HostRoundJson() ?? "{\"key\":\"\",\"seq\":0,\"words\":\"\"}";

            case "/api/room/deaths":
            {
                string raw = FormValue(query, "since");
                long since = long.TryParse(raw, out long n) ? n : 0;
                return _room?.HostDeathsJson(since) ?? "{\"last\":0,\"items\":[]}";
            }

            case "/api/room/death":
                _room?.HostAddDeath(FormValue(body, "name"), FormValue(body, "word"));
                return "{\"ok\":true}";
        }
        return null;
    }

    private void ApplyConfig(string body)
    {
        int ReadInt(string key, int fallback)
        {
            string v = FormValue(body, key);
            return int.TryParse(v, out int n) ? n : fallback;
        }

        int wpr = ReadInt("wordsPerRound", Cfg.WordsPerRound);
        int hfr = ReadInt("highFreqPerRound", Cfg.HighFreqPerRound);
        int rs = ReadInt("roundSeconds", Cfg.RoundSeconds);
        int hs = ReadInt("hudScale", Cfg.HudScalePercent);
        int sp = ReadInt("smartPick", Cfg.SmartPickPercent);
        int spc = ReadInt("smartPerRound", Cfg.SmartPerRound);
        int sh = ReadInt("showHud", Cfg.ShowInGameHud ? 1 : 0);
        int oh = ReadInt("onlyHost", Cfg.OnlyHostCanEdit ? 1 : 0);
        int rm = ReadInt("roomMode", (int)Cfg.RoomMode);
        int ds = ReadInt("deathScope", (int)Cfg.DeathScope);
        string host = FormValue(body, "hostUrl");

        bool roomChanged = rm != (int)Cfg.RoomMode ||
                           (host.Length > 0 && host != Cfg.RoomHostUrl);

        Cfg.WordsPerRound = Math.Clamp(wpr, 1, 20);
        Cfg.HighFreqPerRound = Math.Clamp(hfr, 0, 10);
        Cfg.RoundSeconds = Math.Clamp(rs, 5, 300);
        Cfg.HudScalePercent = Math.Clamp(hs, 40, 100);
        Cfg.SmartPickPercent = Math.Clamp(sp, 0, 100);
        Cfg.SmartPerRound = Math.Clamp(spc, 0, 8);
        Cfg.ShowInGameHud = sh == 1;
        Cfg.OnlyHostCanEdit = oh == 1;
        Cfg.RoomMode = (RoomRole)Math.Clamp(rm, 0, 2);
        Cfg.DeathScope = (DeathScope)Math.Clamp(ds, 0, 1);
        if (host.Length > 0) Cfg.RoomHostUrl = host;

        _runtime?.Reconfigure(cfg =>
        {
            cfg.WordsPerRound = Cfg.WordsPerRound;
            cfg.HighFreqPerRound = Cfg.HighFreqPerRound;
            cfg.SmartPickChance = Cfg.SmartPickPercent / 100.0;
            cfg.SmartPerRound = Math.Max(0, Cfg.SmartPerRound);
            cfg.HighFreqExtra = SplitWords(Cfg.HighFreqExtra);
            cfg.HighFreqDisabled = SplitWords(Cfg.HighFreqDisabled);
        }, Cfg.RoundSeconds);

        if (_room != null) _room.Scope = Cfg.DeathScope;
        if (roomChanged) ApplyRoomMode();

        _configDirty = true;
    }

    private string BuildStateJson()
    {
        var sb = new StringBuilder(4096);
        lock (_lock)
        {
            sb.Append("{\"enabled\":").Append(Cfg.Enabled ? "true" : "false");
            sb.Append(",\"ready\":").Append(_engineReady ? "true" : "false");
            sb.Append(",\"micOpen\":").Append(_mic != null && _mic.IsCapturing ? "true" : "false");
            sb.Append(",\"micLevel\":").Append(((int)(Math.Min(1.0, (_mic?.LastRms ?? 0) * 12.0) * 100)).ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"mic\":\"").Append(Esc(_mic != null && _mic.IsCapturing ? _mic.DeviceName : _micProblem)).Append('"');
            sb.Append(",\"deaths\":").Append(_deaths);
            sb.Append(",\"lastAt\":").Append(_deathSeq);
            sb.Append(",\"remain\":").Append(_runtime?.SecondsUntilRotate ?? 0);

            sb.Append(",\"wordsPerRound\":").Append(Cfg.WordsPerRound);
            sb.Append(",\"highFreqPerRound\":").Append(Cfg.HighFreqPerRound);
            sb.Append(",\"roundSeconds\":").Append(Cfg.RoundSeconds);
            sb.Append(",\"hudScale\":").Append(Cfg.HudScalePercent);
            sb.Append(",\"smartPick\":").Append(Cfg.SmartPickPercent);
            sb.Append(",\"showHud\":").Append(Cfg.ShowInGameHud ? "true" : "false");
            sb.Append(",\"roomMode\":").Append((int)Cfg.RoomMode);
            sb.Append(",\"deathScope\":").Append((int)Cfg.DeathScope);
            sb.Append(",\"hostUrl\":\"").Append(Esc(Cfg.RoomHostUrl)).Append('"');
            sb.Append(",\"roomKey\":\"").Append(Esc(Cfg.RoomKey)).Append('"');

            RoomSync room = _room;
            sb.Append(",\"roomConnected\":").Append(
                room != null && (room.IsHost || room.Connected) ? "true" : "false");

            sb.Append(",\"speaker\":\"").Append(Esc(Speaker())).Append('"');
            sb.Append(",\"source\":").Append((int)Cfg.AudioSource);
            sb.Append(",\"roomError\":\"").Append(Esc(_room?.LastError ?? "")).Append('"');
            sb.Append(",\"lanOpen\":").Append(_server != null && _server.IsLan ? "true" : "false");
            sb.Append(",\"onlyHost\":").Append(Cfg.OnlyHostCanEdit ? "true" : "false");
            sb.Append(",\"offlineSync\":").Append(Cfg.OfflineSync ? "true" : "false");
            sb.Append(",\"micList\":").Append(ArrayJson(MicCapture.DeviceNames().ToArray()));
            sb.Append(",\"micIndex\":").Append(Cfg.MicDeviceIndex);
            sb.Append(",\"localIps\":[");
            var ips = LocalIPs();
            for (int i = 0; i < ips.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(Esc(ips[i])).Append('"');
            }
            sb.Append(']');
            sb.Append(",\"port\":").Append(_actualPort > 0 ? _actualPort : Cfg.WebPort);

            // 本轮词
            sb.Append(",\"words\":[");
            for (int i = 0; i < _round.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"t\":\"").Append(Esc(_round[i].Text))
                  .Append("\",\"s\":\"").Append(_round[i].Source).Append("\"}");
            }
            sb.Append(']');

            // 下一轮预填
            sb.Append(",\"nextWords\":[");
            for (int i = 0; i < _nextWords.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(Esc(_nextWords[i])).Append('"');
            }
            sb.Append(']');

            // 高频词（本周期 / 上一周期）
            sb.Append(",\"topCur\":").Append(TopJson(_runtime?.CurrentTop(12)));
            sb.Append(",\"topPrev\":").Append(TopJson(_runtime?.PrevTop));

            // 词库
            sb.Append(",\"smartPerRound\":").Append(Cfg.SmartPerRound);
            sb.Append(",\"fatal\":\"").Append(Esc(_fatal)).Append('"');
            sb.Append(",\"binDir\":\"").Append(Esc(_binDir)).Append('"');

            sb.Append(",\"namesEnabled\":").Append(ArrayJson(SplitWords(Cfg.Names).ToArray()));
            sb.Append(",\"namesDisabled\":").Append(ArrayJson(SplitWords(Cfg.DisabledNames).ToArray()));

            sb.Append(",\"bank\":{");
            sb.Append("\"casual\":").Append(ArrayJson(WordBank.Casual));
            sb.Append(",\"formal\":").Append(ArrayJson(WordBank.Formal));
            {
                var dis = new HashSet<string>(StringComparer.Ordinal);
                foreach (string w in SplitWords(Cfg.HighFreqDisabled)) dis.Add(w);
                var on = new List<string>();
                var off = new List<string>();
                foreach (string w in WordBank.Hell)
                {
                    if (dis.Contains(w)) off.Add(w); else on.Add(w);
                }
                foreach (string w in SplitWords(Cfg.HighFreqExtra))
                {
                    if (!on.Contains(w)) on.Add(w);
                }
                sb.Append(",\"hell\":").Append(ArrayJson(on.ToArray()));
                sb.Append(",\"hellOff\":").Append(ArrayJson(off.ToArray()));
            }
            sb.Append(",\"custom\":").Append(ArrayJson(WordBank.Custom.ToArray()));
            sb.Append(",\"names\":").Append(ArrayJson(SplitWords(Cfg.Names).ToArray()));
            sb.Append(",\"namesOff\":").Append(ArrayJson(SplitWords(Cfg.DisabledNames).ToArray()));
            sb.Append('}');

            sb.Append(",\"last\":{\"text\":\"").Append(Esc(_lastText))
              .Append("\",\"word\":\"").Append(Esc(_lastWord))
              .Append("\",\"kind\":\"").Append(Esc(_lastKind)).Append("\"}");

            sb.Append(",\"events\":[");
            for (int i = 0; i < _log.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"t\":\"").Append(_log[i].T)
                  .Append("\",\"text\":\"").Append(Esc(_log[i].Text))
                  .Append("\",\"kill\":").Append(_log[i].Kill ? "true" : "false").Append('}');
            }
            sb.Append(']');

            sb.Append(",\"history\":[");
            for (int i = 0; i < _history.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"t\":\"").Append(_history[i].T)
                  .Append("\",\"text\":\"").Append(Esc(_history[i].Text)).Append('"');
                sb.Append('}');
            }
            sb.Append("]}");
        }
        return sb.ToString();
    }

    private static string TopJson(List<KeyValuePair<string, int>> top)
    {
        var sb = new StringBuilder("[");
        if (top != null)
        {
            for (int i = 0; i < top.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"t\":\"").Append(RoomSync.Escape(top[i].Key))
                  .Append("\",\"n\":").Append(top[i].Value).Append('}');
            }
        }
        return sb.Append(']').ToString();
    }

    private static string ArrayJson(IEnumerable<string> items)
    {
        var sb = new StringBuilder("[");
        bool first = true;
        foreach (string item in items)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('"').Append(RoomSync.Escape(item)).Append('"');
        }
        return sb.Append(']').ToString();
    }

    private void LogEvent(string text, bool kill)
    {
        lock (_lock)
        {
            _log.Add(new LogItem { T = DateTime.Now.ToString("HH:mm:ss"), Text = text, Kill = kill });
            while (_log.Count > 30) _log.RemoveAt(0);
        }
    }

    /// <summary>踩雷播报里用的名字。</summary>
    private string Speaker()
    {
        string n = (Cfg.SpeakerName ?? "").Trim();
        if (n.Length > 0) return n;
        List<string> names = SplitWords(Cfg.Names);
        return names.Count > 0 ? names[0] : "你";
    }

    private string FirstName() => Speaker();

    // ---------------- 小工具 ----------------

    /// <summary>给备用设置子菜单用的公开保存入口。</summary>
    public static void SaveCfgPublic() => SaveCfg();

    private static void SaveCfg()
    {
        try { _cfg.Save(_modDir); } catch { }
    }

    /// <summary>备用设置：打开配置文件夹。</summary>
    public void OpenConfigFolder()
    {
        try
        {
            string dir = string.IsNullOrEmpty(_modDir)
                ? Path.Combine(AppContext.BaseDirectory, "Mods", "VoiceKill")
                : _modDir;
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Error, Tag, "打开配置文件夹失败: " + ex.Message);
        }
    }

    /// <summary>备用设置：恢复默认配置。</summary>
    public void ResetConfig()
    {
        _cfg = new ModConfig();
        SaveCfg();
        WordBank.SetCustom(new List<string>());
        _startFailed = false;
        StopAll();
        Logger.Log(LogLevel.Info, Tag, "配置已恢复默认");
    }

    /// <summary>10/8、172.16/12、192.168/16、169.254/16 这些才是内网地址。</summary>
    private static bool IsPrivateV4(IPAddress ip)
    {
        byte[] b = ip.GetAddressBytes();
        if (b.Length != 4) return false;
        if (b[0] == 10) return true;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
        if (b[0] == 192 && b[1] == 168) return true;
        if (b[0] == 169 && b[1] == 254) return true;
        return false;
    }

    /// <summary>跨进程稳定的字符串哈希（string.GetHashCode 每个进程不一样，不能用）。</summary>
    private static int StableHash(string s)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (char c in s ?? "")
            {
                h ^= c;
                h *= 16777619;
            }
            uint v = h & 0x7fffffff;
            return v == 0 ? 1 : (int)v;
        }
    }

    /// <summary>
    /// 本机内网 IP，按「能不能用」排序。
    ///
    /// 2026-10-24 一个用户的面板让人填 169.254.142.67 —— 那是「没拿到 DHCP」时的自动地址，
    /// 别人根本连不上；同一台机器上还有 192.168.56.1 这种 VirtualBox 虚拟网卡。
    /// 所以这里改成打分：有默认网关的优先，虚拟网卡扣分，169.254 基本等于没网。
    /// </summary>
    public static List<string> LocalIPs()
    {
        var cand = new List<KeyValuePair<string, int>>();
        try
        {
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                IPInterfaceProperties props;
                try { props = ni.GetIPProperties(); } catch { continue; }

                bool hasGateway = false;
                try
                {
                    foreach (GatewayIPAddressInformation gw in props.GatewayAddresses)
                    {
                        if (gw.Address != null && !gw.Address.Equals(IPAddress.Any)) { hasGateway = true; break; }
                    }
                }
                catch { }

                string name = ((ni.Name ?? "") + " " + (ni.Description ?? "")).ToLowerInvariant();
                int baseScore = hasGateway ? 40 : 0;
                if (name.Contains("virtualbox") || name.Contains("vmware") || name.Contains("hyper-v") ||
                    name.Contains("vethernet") || name.Contains("tailscale") || name.Contains("zerotier") ||
                    name.Contains("wintun") || name.Contains("tap-"))
                    baseScore -= 30;

                try
                {
                    foreach (UnicastIPAddressInformation ua in props.UnicastAddresses)
                    {
                        IPAddress ip = ua.Address;
                        if (ip.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (IPAddress.IsLoopback(ip)) continue;

                        byte[] b = ip.GetAddressBytes();
                        int score = baseScore;
                        if (b[0] == 192 && b[1] == 168) score += 25;
                        else if (b[0] == 10) score += 20;
                        else if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) score += 15;
                        else if (b[0] == 169 && b[1] == 254) score -= 50;   // APIPA：等于没网
                        else score -= 40;                                    // 公网 IP，不该显示

                        Logger.Log(LogLevel.Debug, "VoiceKill", $"网卡 [{ni.Name}] {ip} 网关={hasGateway} 得分={score}");
                        cand.Add(new KeyValuePair<string, int>(ip.ToString(), score));
                    }
                }
                catch { }
            }
        }
        catch { }

        return cand.Where(c => c.Value >= 0)
                   .OrderByDescending(c => c.Value)
                   .Select(c => c.Key)
                   .Distinct()
                   .ToList();
    }

    private static string FirstLocalIP()
    {
        List<string> ips = LocalIPs();
        return ips.Count > 0 ? ips[0] : "本机IP";
    }

    private static List<string> SplitWords(string raw)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(raw)) return list;
        foreach (string part in raw.Split(new[] { ',', '，', ';', '；', '\n', '\r', '\t', ' ' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            string s = part.Trim();
            if (s.Length > 0 && !list.Contains(s)) list.Add(s);
        }
        return list;
    }

    /// <summary>从 form-urlencoded 或 URL query 里取值（会自动去掉开头的 ?）。</summary>
    private static string FormValue(string data, string key)
    {
        if (string.IsNullOrEmpty(data)) return "";
        string prefix = key + "=";
        foreach (string part in data.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string p = part.StartsWith("?", StringComparison.Ordinal) ? part.Substring(1) : part;
            if (!p.StartsWith(prefix, StringComparison.Ordinal)) continue;
            try { return Uri.UnescapeDataString(p.Substring(prefix.Length).Replace('+', ' ')); }
            catch { return p.Substring(prefix.Length); }
        }
        return "";
    }

    private static string Esc(string s) => RoomSync.Escape(s);
}
