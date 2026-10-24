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

    public long RoundSeq => Interlocked.Read(ref _roundSeq);
    public string RoomKey => _roomKey;

    public RoomSync()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(900) };
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
        return "{\"key\":\"" + Escape(key) + "\",\"seq\":" + seq +
               ",\"words\":\"" + Escape(words) + "\",\"scope\":\"" + Scope + "\"}";
    }

    public void HostAddDeath(string name, string word)
    {
        lock (_lock)
        {
            _deathSeq++;
            _deaths.Add(new RoomDeath { Seq = _deathSeq, Name = name ?? "", Word = word ?? "" });
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
                  .Append("\",\"word\":\"").Append(Escape(d.Word)).Append("\"}");
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
            var list = new List<string>();
            foreach (string w in packed.Split('\t'))
            {
                string t = w.Trim();
                if (t.Length > 0) list.Add(t);
            }
            if (list.Count > 0) OnRemoteRound?.Invoke(list);
        }

        string deaths = _http.GetStringAsync(Base + "/api/room/deaths?since=" + _seenDeathSeq + "&key=" + k)
            .GetAwaiter().GetResult();
        long last = ReadLong(deaths, "\"last\":");
        if (last <= _seenDeathSeq) return;

        int idx = 0;
        while (true)
        {
            int at = deaths.IndexOf("\"name\":\"", idx, StringComparison.Ordinal);
            if (at < 0) break;
            string name = ReadAt(deaths, at + "\"name\":\"".Length);
            int wat = deaths.IndexOf("\"word\":\"", at, StringComparison.Ordinal);
            string word = wat >= 0 ? ReadAt(deaths, wat + "\"word\":\"".Length) : "";
            idx = at + 8;

            if (Scope == DeathScope.全员连坐) OnRemoteDeath?.Invoke(name + "|" + word);
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
                                            "&key=" + Uri.EscapeDataString(Key ?? ""),
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

    private static string ReadAt(string json, int from)
    {
        var sb = new StringBuilder();
        for (int i = from; i < json.Length; i++)
        {
            char c = json[i];
            if (c == '\\' && i + 1 < json.Length) { sb.Append(json[++i]); continue; }
            if (c == '"') break;
            sb.Append(c);
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
