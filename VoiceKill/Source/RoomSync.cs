using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using VoiceKill.Core;

namespace Celeste.Mod.VoiceKill;

public sealed class RoomDeath
{
    public long Seq;
    public string Name = "";
    public string Word = "";
    /// <summary>谁报的（客户端的随机 id）。用来避免「自己的死亡回声」把自己再杀一次。</summary>
    public string Origin = "";
}

/// <summary>
/// 房间模式：多人进同一个房间，共享同一套危险词；可选「一人踩雷全员连坐」。
///
/// 实现方式刻意选得最土：本地 HTTP。
///   - 主机：换词时把词表挂到 /api/room 上，客户端来拉
///   - 客户端：后台线程每 800ms 拉一次，房间号（地图+房间）一致才采纳
///   - 死亡：客户端把自己踩雷报给主机，主机广播；AllDie 模式下客户端收到就跟着死
///
/// 为什么不走 CelesteNet / MiaoNet 的 API：那些是别人的内部实现，
/// 版本一变就静默失效（交接文档里 Mixin 那条坑就是这个）。HTTP 只依赖自己。
/// </summary>
public sealed class RoomSync : IDisposable
{
    private readonly HttpClient _http;
    private readonly List<RoomDeath> _deaths = new List<RoomDeath>();
    private readonly object _lock = new object();
    private Thread _pollThread;
    private volatile bool _running;

    private long _roundSeq;
    private long _deathSeq;
    private long _seenDeathSeq;
    private string _roomKey = "";
    private string _wordsTab = "";
    private int _wordsCount;

    /// <summary>客户端采纳到主机的新一轮词。</summary>
    public event Action<IReadOnlyList<string>> OnRemoteRound;

    /// <summary>AllDie 模式下，别人踩雷了。</summary>
    public event Action<string> OnRemoteDeath;

    public bool IsHost;
    public bool IsClient;
    public string HostUrl = "";

    /// <summary>房间口令（握手用，主机侧会校验）。</summary>
    public string Key = "";

    /// <summary>本机所在房间的口令（客户端用它判断是不是同一个房间）。</summary>
    public string LocalRoomKey = "";

    /// <summary>踩雷后是只死自己还是全员连坐。</summary>
    public DeathScope Scope = DeathScope.只死自己;

    /// <summary>客户端是否成功连上主机（面板上的小绿点用）。</summary>
    public volatile bool Connected;

    /// <summary>最后一次连接失败的原因（面板要显示，否则用户只看到「正在连接」）。</summary>
    public volatile string LastError = "";

    /// <summary>本机客户端 id：随死亡一起报给主机，主机再广播出来，自己就认得是自己的死亡。</summary>
    public readonly string ClientId = Guid.NewGuid().ToString("N").Substring(0, 10);

    /// <summary>主机当前设定的踩雷范围（-1 = 还不知道）。客机以主机的为准。</summary>
    public volatile int HostScopeRaw = -1;

    /// <summary>对方是不带词数的老版本（0.9.5 及以前）。</summary>
    public volatile bool PeerOutdated;

    /// <summary>协议层面的异常（词数对不上等），面板上要显示出来。</summary>
    public volatile string ProtocolWarn = "";

    /// <summary>
    /// 强制重新采纳主机的当前这一轮词。
    /// 读档 / 换关之后运行时是新的，如果只等主机下次换词（可能 30 秒后），
    /// 客机会有一段时间一个字都不显示。
    /// </summary>
    public void ForceResync()
    {
        if (!IsClient) return;
        Interlocked.Exchange(ref _roundSeq, -1);
    }

    public long RoundSeq => Interlocked.Read(ref _roundSeq);
    public string RoomKey => _roomKey;

    public RoomSync()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(2500) };
        try { _http.DefaultRequestHeaders.ConnectionClose = true; } catch { }
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        if (IsClient)
        {
            _pollThread = new Thread(PollLoop) { IsBackground = true, Name = "VoiceKill.Room" };
            _pollThread.Start();
        }
    }

    public void Stop()
    {
        _running = false;
        try { _pollThread?.Join(600); } catch { }
        _pollThread = null;
    }

    // ---------------- 主机侧 ----------------

    public void HostSetRound(string roomKey, IReadOnlyList<DangerWord> words)
    {
        if (!IsHost) return;
        var sb = new StringBuilder();
        if (words != null)
        {
            foreach (DangerWord w in words)
            {
                if (w == null || string.IsNullOrEmpty(w.Text)) continue;
                if (sb.Length > 0) sb.Append('\t');
                sb.Append(w.Text);
            }
        }

        lock (_lock)
        {
            _roomKey = roomKey ?? "";
            _wordsTab = sb.ToString();
            _wordsCount = words == null ? 0 : words.Count;
            _roundSeq++;
        }
    }

    public string HostRoundJson()
    {
        string key, words;
        long seq;
        lock (_lock)
        {
            key = _roomKey;
            words = _wordsTab;
            seq = _roundSeq;
        }
        lock (_lock)
        {
            // n = 词数。老版本没这个字段，客机据此判断「对方版本旧，词会粘在一起」
            return "{\"key\":\"" + Escape(key) + "\",\"seq\":" + seq +
                   ",\"n\":" + _wordsCount +
                   ",\"words\":\"" + Escape(words) + "\",\"scope\":\"" + Scope + "\"}";
        }
    }

    public void HostAddDeath(string name, string word)
        => HostAddDeath(name, word, "");

    public void HostAddDeath(string name, string word, string origin)
    {
        lock (_lock)
        {
            _deathSeq++;
            _deaths.Add(new RoomDeath
            {
                Seq = _deathSeq,
                Name = name ?? "",
                Word = word ?? "",
                Origin = origin ?? ""
            });
            while (_deaths.Count > 100) _deaths.RemoveAt(0);
        }
    }

    public string HostDeathsJson(long since)
    {
        var sb = new StringBuilder("{\"last\":");
        lock (_lock)
        {
            sb.Append(_deathSeq).Append(",\"items\":[");
            bool first = true;
            foreach (RoomDeath d in _deaths)
            {
                if (d.Seq <= since) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"seq\":").Append(d.Seq)
                  .Append(",\"name\":\"").Append(Escape(d.Name))
                  .Append("\",\"word\":\"").Append(Escape(d.Word))
                  .Append("\",\"o\":\"").Append(Escape(d.Origin)).Append("\"}");
            }
        }
        return sb.Append("]}").ToString();
    }

    // ---------------- 客户端侧 ----------------

    private void PollLoop()
    {
        while (_running)
        {
            try
            {
                PollOnce();
                Connected = true;
                LastError = "";
            }
            catch (Exception ex)
            {
                Connected = false;
                LastError = ex.GetType().Name + ": " + ex.Message;
            }
            Thread.Sleep(800);
        }
    }

    private string Base => string.IsNullOrEmpty(HostUrl) ? "" : HostUrl.TrimEnd('/');

    private void PollOnce()
    {
        if (!IsClient || string.IsNullOrEmpty(Base)) return;

        string k = Uri.EscapeDataString(Key ?? "");
        string round = _http.GetStringAsync(Base + "/api/room?key=" + k).GetAwaiter().GetResult();
        string key = ReadStr(round, "\"key\":\"");
        long seq = ReadLong(round, "\"seq\":");

        // 只有「同一个房间」才采纳主机的词
        if (!string.IsNullOrEmpty(key) && key == LocalRoomKey && seq != Interlocked.Read(ref _roundSeq))
        {
            Interlocked.Exchange(ref _roundSeq, seq);
            string packed = ReadStr(round, "\"words\":\"");
            int expect = (int)ReadLong(round, "\"n\":");

            var list = new List<string>();
            foreach (string w in packed.Split('\t'))
            {
                string t = w.Trim();
                if (t.Length > 0) list.Add(t);
            }

            if (expect == 0)
            {
                // 老主机（0.9.5 及以前）不发 n。它的词表本身没问题，但如果你这边的
                // 版本也旧，就会解成一坨 —— 这里只提示，不拦。
                PeerOutdated = true;
            }
            else
            {
                PeerOutdated = false;
                if (list.Count != expect)
                    ProtocolWarn = $"词数对不上（主机说 {expect} 个，这边收到 {list.Count} 个）";
                else
                    ProtocolWarn = "";
            }

            if (list.Count > 0) OnRemoteRound?.Invoke(list);

            // 踩雷范围以主机为准：主机设「全员连坐」就所有人都连坐，
            // 不再由每个客户端自己在面板上设（以前就是这么散的，导致有人不连坐）
            string scope = ReadStr(round, "\"scope\":\"");
            if (scope == "全员连坐") HostScopeRaw = 1;
            else if (scope == "只死自己") HostScopeRaw = 0;
        }

        string deaths = _http.GetStringAsync(Base + "/api/room/deaths?since=" + _seenDeathSeq + "&key=" + k)
            .GetAwaiter().GetResult();
        ApplyDeaths(deaths);
    }

    /// <summary>
    /// 处理主机广播的死亡列表。抽成独立方法是为了能单独验证
    /// 「自己的死亡回声不能再杀自己」和「按主机的踩雷范围决定」这两条规则。
    /// </summary>
    private void ApplyDeaths(string deaths)
    {
        if (string.IsNullOrEmpty(deaths)) return;

        long last = ReadLong(deaths, "\"last\":");
        if (last < _seenDeathSeq) _seenDeathSeq = 0;   // 主机重启过，序号归零了
        if (last <= _seenDeathSeq) return;

        int idx = 0;
        while (true)
        {
            int at = deaths.IndexOf("\"name\":\"", idx, StringComparison.Ordinal);
            if (at < 0) break;
            string name = ReadAt(deaths, at + "\"name\":\"".Length);
            int wat = deaths.IndexOf("\"word\":\"", at, StringComparison.Ordinal);
            string word = wat >= 0 ? ReadAt(deaths, wat + "\"word\":\"".Length) : "";

            int oat = deaths.IndexOf("\"o\":\"", at, StringComparison.Ordinal);
            string origin = oat >= 0 ? ReadAt(deaths, oat + "\"o\":\"".Length) : "";

            idx = at + 8;

            // 自己报上去的死亡会被主机广播回来，认出来就跳过，
            // 否则「我死了 → 报给主机 → 主机广播 → 我又死一次」。
            if (origin.Length > 0 && origin == ClientId) continue;

            int scope = HostScopeRaw >= 0 ? HostScopeRaw : (int)Scope;
            if (scope == (int)DeathScope.全员连坐) OnRemoteDeath?.Invoke(name + "|" + word);
        }

        _seenDeathSeq = last;
    }

    /// <summary>客户端把改词请求转发给主机（房主统一生效）。</summary>
    public bool SendEdit(string body)
    {
        if (!IsClient || string.IsNullOrEmpty(Base)) return false;
        try
        {
            var content = new StringContent(body ?? "", Encoding.UTF8, "application/x-www-form-urlencoded");
            using var resp = _http.PostAsync(Base + "/api/remote/edit", content).GetAwaiter().GetResult();
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>客户端把自己踩雷的事报给主机。</summary>
    public void ReportDeath(string name, string word)
    {
        if (!IsClient || string.IsNullOrEmpty(Base)) return;
        try
        {
            var content = new StringContent("name=" + Uri.EscapeDataString(name ?? "") +
                                            "&word=" + Uri.EscapeDataString(word ?? "") +
                                            "&key=" + Uri.EscapeDataString(Key ?? "") +
                                            "&origin=" + Uri.EscapeDataString(ClientId),
                Encoding.UTF8, "application/x-www-form-urlencoded");
            _http.PostAsync(Base + "/api/room/death", content).GetAwaiter().GetResult();
        }
        catch { }
    }

    // ---------------- 解析小工具 ----------------

    private static string ReadStr(string json, string marker)
    {
        int at = json.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return "";
        return ReadAt(json, at + marker.Length);
    }

    /// <summary>
    /// 读一个 JSON 字符串（从引号后开始，读到引号为止）。
    ///
    /// ⚠️ 2026-10-25 修：以前这里遇到 `\\` 就直接 Append 下一个字符，
    /// 于是主机发来的分隔符 `\\t` 被解成字母 `t` —— 客机看到的就是
    /// 一个词 `1t2t3` 而不是三个词。这是一次真实的联机事故，别再简化这段。
    /// </summary>
    private static string ReadAt(string json, int from)
    {
        var sb = new StringBuilder();
        for (int i = from; i < json.Length; i++)
        {
            char c = json[i];
            if (c == '"') break;
            if (c != '\\') { sb.Append(c); continue; }
            if (i + 1 >= json.Length) break;

            char e = json[++i];
            switch (e)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case '/': sb.Append('/'); break;
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case 'u':
                    if (i + 4 < json.Length &&
                        ushort.TryParse(json.Substring(i + 1, 4), System.Globalization.NumberStyles.HexNumber,
                                        System.Globalization.CultureInfo.InvariantCulture, out ushort code))
                    {
                        sb.Append((char)code);
                        i += 4;
                    }
                    break;
                default: sb.Append(e); break;
            }
        }
        return sb.ToString();
    }

    private static long ReadLongAt(string json, int from)
    {
        long value = 0;
        int i = from;
        while (i < json.Length && json[i] >= '0' && json[i] <= '9')
        {
            value = value * 10 + (json[i] - '0');
            i++;
        }
        return value;
    }

    private static long ReadLong(string json, string marker)
    {
        int at = json.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return 0;
        int i = at + marker.Length;
        long value = 0;
        while (i < json.Length && json[i] >= '0' && json[i] <= '9')
        {
            value = value * 10 + (json[i] - '0');
            i++;
        }
        return value;
    }

    public static string Escape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append(' ');
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        Stop();
        try { _http.Dispose(); } catch { }
    }
}
