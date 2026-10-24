using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Celeste.Mod.VoiceKill;

/// <summary>
/// 请求分发：method = GET/POST，path 已去掉 query，query 是 ? 后面的原样。
/// 返回 JSON 字符串；返回 null 表示 404。
/// </summary>
public delegate string VoiceKillRequestHandler(string method, string path, string query, string body, bool isLocal);

/// <summary>
/// 本地 HTTP 服务：网页面板 + 面板接口。
///
/// ⚠️ 为什么不用 HttpListener：
/// 它绑 `http://+:port/` 需要管理员权限或 netsh urlacl，普通用户跑不了；
/// 而只绑 localhost 的话**局域网里别的机器永远连不上**（联机功能因此实测失败）。
/// 这里用裸 TcpListener，绑 0.0.0.0 不需要任何权限。
///
/// 安全策略：只有「房间模式 = 主机」时才对外开放端口，其它情况只监听 127.0.0.1。
/// </summary>
public sealed class PanelServer : IDisposable
{
    private readonly int _port;
    private readonly VoiceKillRequestHandler _handler;
    private TcpListener _listener;
    private Thread _thread;
    private volatile bool _running;

    public bool IsLan { get; private set; }

    public PanelServer(int port, VoiceKillRequestHandler handler)
    {
        _port = port;
        _handler = handler;
    }

    public bool Start(bool allowLan, out string error)
    {
        error = null;
        try
        {
            IPAddress addr = allowLan ? IPAddress.Any : IPAddress.Loopback;
            _listener = new TcpListener(addr, _port);
            _listener.Start();
            IsLan = allowLan;
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "VoiceKill.Http" };
            _thread.Start();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private void Loop()
    {
        while (_running)
        {
            TcpClient client;
            try { client = _listener.AcceptTcpClient(); }
            catch { break; }

            try { HandleClient(client); }
            catch (Exception ex) { Logger.Log(LogLevel.Debug, "VoiceKill", "HTTP: " + ex.Message); }
            finally { try { client.Close(); } catch { } }
        }
    }

    private void HandleClient(TcpClient client)
    {
        client.ReceiveTimeout = 3000;
        client.SendTimeout = 3000;
        using NetworkStream ns = client.GetStream();

        // ---- 读请求头 ----
        var ms = new MemoryStream();
        var buf = new byte[8192];
        int headEnd = -1;
        while (headEnd < 0)
        {
            int n = ns.Read(buf, 0, buf.Length);
            if (n <= 0) return;
            ms.Write(buf, 0, n);
            if (ms.Length > 64 * 1024) return;
            headEnd = FindHeaderEnd(ms.GetBuffer(), (int)ms.Length);
        }

        byte[] all = ms.ToArray();
        string head = Encoding.UTF8.GetString(all, 0, headEnd);
        string[] lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
        string[] first = lines.Length > 0 ? lines[0].Split(' ') : Array.Empty<string>();
        string method = first.Length > 0 ? first[0] : "GET";
        string target = first.Length > 1 ? first[1] : "/";

        string path = target, query = "";
        int q = target.IndexOf('?');
        if (q >= 0) { path = target.Substring(0, q); query = target.Substring(q); }

        int contentLength = 0;
        foreach (string line in lines)
        {
            if (!line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) continue;
            int.TryParse(line.Substring(15).Trim(), out contentLength);
        }

        // ---- 读请求体 ----
        string body = "";
        if (contentLength > 0 && contentLength < 1024 * 1024)
        {
            var bodyBytes = new byte[contentLength];
            int already = Math.Max(0, all.Length - (headEnd + 4));
            if (already > contentLength) already = contentLength;
            if (already > 0) Array.Copy(all, headEnd + 4, bodyBytes, 0, already);

            int got = already;
            while (got < contentLength)
            {
                int n = ns.Read(bodyBytes, got, contentLength - got);
                if (n <= 0) break;
                got += n;
            }
            body = Encoding.UTF8.GetString(bodyBytes, 0, got);
        }

        // ---- 分发 ----
        bool isLocal = false;
        try
        {
            var remote = ((IPEndPoint)client.Client.RemoteEndPoint).Address;
            isLocal = IPAddress.IsLoopback(remote);
        }
        catch { }

        string text, contentType;
        int status = 200;

        if (path.Equals("/diag", StringComparison.OrdinalIgnoreCase))
        {
            // 纯文本诊断页：不跑 JS，用来分清「服务坏了」还是「页面脚本坏了」
            string diag;
            try { diag = "OK\n" + (_handler?.Invoke(method, "/state", query, body, true) ?? "(null)"); }
            catch (Exception ex) { diag = "ERR " + ex; }
            text = diag;
            contentType = "text/plain; charset=utf-8";
        }
        else if (path.Equals("/panel", StringComparison.OrdinalIgnoreCase))
        {
            // 页面只给本机看：开着房的时候，别让同一局域网的人翻你的面板
            if (!isLocal)
            {
                text = "只允许本机访问";
                contentType = "text/plain; charset=utf-8";
                status = 403;
            }
            else
            {
                text = PanelHtml;
                contentType = "text/html; charset=utf-8";
            }
        }
        else
        {
            string json;
            try { json = _handler?.Invoke(method, path, query, body, isLocal); }
            catch (Exception ex) { json = "{\"error\":\"" + RoomSync.Escape(ex.Message) + "\"}"; }

            if (json == null) { status = 404; json = "{\"error\":\"not found\"}"; }
            text = json;
            contentType = "application/json; charset=utf-8";
        }

        byte[] outBytes = Encoding.UTF8.GetBytes(text);
        string headOut =
            "HTTP/1.1 " + status + (status == 200 ? " OK" : " Not Found") + "\r\n" +
            "Content-Type: " + contentType + "\r\n" +
            "Content-Length: " + outBytes.Length + "\r\n" +
            "Cache-Control: no-store\r\n" +
            "Access-Control-Allow-Origin: *\r\n" +
            "Connection: close\r\n\r\n";

        byte[] headBytes = Encoding.UTF8.GetBytes(headOut);
        ns.Write(headBytes, 0, headBytes.Length);
        ns.Write(outBytes, 0, outBytes.Length);
        ns.Flush();
    }

    private static int FindHeaderEnd(byte[] data, int len)
    {
        for (int i = 0; i + 3 < len; i++)
        {
            if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
                return i;
        }
        return -1;
    }

    public void Stop()
    {
        _running = false;
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }

    public void Dispose() => Stop();

    private static string _panelHtml;

    /// <summary>控制面板页面，作为内嵌资源打进 dll，不依赖外部文件路径。</summary>
    public static string PanelHtml
    {
        get
        {
            if (_panelHtml != null) return _panelHtml;
            try
            {
                var asm = typeof(PanelServer).Assembly;
                foreach (string name in asm.GetManifestResourceNames())
                {
                    if (!name.EndsWith("panel.html", StringComparison.OrdinalIgnoreCase)) continue;
                    using var stream = asm.GetManifestResourceStream(name);
                    using var reader = new System.IO.StreamReader(stream, Encoding.UTF8);
                    _panelHtml = reader.ReadToEnd();
                    return _panelHtml;
                }
            }
            catch { }
            _panelHtml = "<!doctype html><meta charset=utf-8><h1>panel.html 没打进包</h1>";
            return _panelHtml;
        }
    }
}
