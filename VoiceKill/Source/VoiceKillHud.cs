using System;
using System.Collections.Generic;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Celeste;
using Celeste.Mod;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Monocle;
using VoiceKill.Core;
using Gdi = System.Drawing;
using XnaColor = Microsoft.Xna.Framework.Color;

namespace Celeste.Mod.VoiceKill;

/// <summary>
/// 把中文画进游戏。
///
/// Celeste 自带的位图字体只有西文字形，画不出「冲刺」这种字。
/// 所以用 GDI+（系统里的微软雅黑）把文字**预渲染成贴图**，
/// 圆角底 + 描边 + 白字都烤进图里，运行时只负责贴。
///
/// 坐标一律用 batch.GraphicsDevice.Viewport 的真实像素，
/// **不要用 Engine.ScreenMatrix** —— 那个矩阵在这个时机并不提供 320→屏幕的缩放。
/// </summary>
internal static class VoiceKillHud
{
    // 一轮最多一行放几个；超过就换行
    public const int MaxPerRow = 10;

    private const float GapRatio = 0.09f;      // 词间距 ÷ 贴图高
    private const float RowGapRatio = 0.22f;   // 行间距 ÷ 贴图高
    private const float TargetHeightRatio = 0.075f;  // 单行高度最多占屏高多少
    private const float MaxBlockRatio = 0.30f;      // 所有行加起来最多占屏高多少
    private const float MarginRatio = 0.012f;
    private const int CacheLimit = 64;

    private static readonly Dictionary<string, Texture2D> Cache = new Dictionary<string, Texture2D>();
    private static readonly Queue<string> Order = new Queue<string>();
    private static bool _unsupported;
    private static bool _loggedError;
    private static bool _loggedSpace;
    private static Texture2D _white;

    // ---------------- 危险词条 ----------------

    public static void Warmup(IReadOnlyList<DangerWord> words)
    {
        if (_unsupported || words == null) return;
        foreach (DangerWord w in words)
        {
            string key = Key(w);
            if (Cache.ContainsKey(key)) continue;
            try { Cache[key] = BakeChip(w.Text, AccentOf(w.Source)); Register(key); }
            catch (Exception ex) { Fail(ex); }
            return; // 一帧只烤一张
        }
    }

    public static void Render(IReadOnlyList<DangerWord> words, int percent)
    {
        if (_unsupported || words == null || words.Count == 0) return;
        if (Engine.Scene is not Level) return;

        bool allReady = true;
        foreach (DangerWord w in words) if (!Cache.ContainsKey(Key(w))) { allReady = false; break; }
        if (!allReady) { Warmup(words); return; }

        SpriteBatch batch = Draw.SpriteBatch;
        if (batch == null) return;

        Viewport vp = batch.GraphicsDevice.Viewport;
        float W = vp.Width, H = vp.Height;
        if (W < 8f || H < 8f) return;

        float margin = Math.Max(4f, W * MarginRatio);

        // 按 MaxPerRow 切行
        var rows = new List<List<DangerWord>>();
        var row = new List<DangerWord>();
        foreach (DangerWord w in words)
        {
            if (row.Count >= MaxPerRow) { rows.Add(row); row = new List<DangerWord>(); }
            row.Add(w);
        }
        if (row.Count > 0) rows.Add(row);

        float maxH = 0f;
        foreach (DangerWord w in words) maxH = Math.Max(maxH, Cache[Key(w)].Height);
        if (maxH <= 0f) return;

        float gap = maxH * GapRatio;
        float rowGap = maxH * RowGapRatio;

        // 最宽的那一行决定缩放
        float widest = 0f;
        foreach (var r in rows)
        {
            float wsum = gap * (r.Count - 1);
            foreach (DangerWord x in r) wsum += Cache[Key(x)].Width;
            widest = Math.Max(widest, wsum);
        }

        float totalBakedH = maxH * rows.Count + rowGap * (rows.Count - 1);
        float scale = Math.Min((W - margin * 2f) / widest,
                     Math.Min((H * TargetHeightRatio) / maxH, (H * MaxBlockRatio) / totalBakedH));
        if (percent > 0) scale *= Math.Min(percent, 100) / 100f;
        if (scale <= 0.0001f) return;

        if (!_loggedSpace)
        {
            _loggedSpace = true;
            Logger.Log(LogLevel.Info, "VoiceKill",
                $"HUD: 视口 {vp.Width}x{vp.Height}，{rows.Count} 行，缩放 {scale:0.000}");
        }

        batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp,
            DepthStencilState.None, RasterizerState.CullNone);

        float y = margin;
        foreach (var r in rows)
        {
            float rowW = gap * (r.Count - 1);
            foreach (DangerWord x in r) rowW += Cache[Key(x)].Width;
            rowW *= scale;

            float xpos = W - margin - rowW;  // 整行贴右边
            foreach (DangerWord d in r)
            {
                Texture2D tex = Cache[Key(d)];
                batch.Draw(tex, new Vector2(MathF.Round(xpos), MathF.Round(y)), null,
                    XnaColor.White * 0.96f, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
                xpos += tex.Width * scale + gap * scale;
            }
            y += maxH * scale + rowGap * scale;
        }

        batch.End();
    }

    // ---------------- 踩雷动画 + 右下角提示 ----------------

    /// <param name="since">距踩雷过了多少秒</param>
    public static void RenderHit(string word, string speaker, float since)
    {
        if (_unsupported || string.IsNullOrEmpty(word)) return;
        if (Engine.Scene is not Level) return;
        if (since < 0f || since > 3.0f) return;

        SpriteBatch batch = Draw.SpriteBatch;
        if (batch == null) return;

        Viewport vp = batch.GraphicsDevice.Viewport;
        float W = vp.Width, H = vp.Height;
        if (W < 8f || H < 8f) return;

        float margin = Math.Max(6f, W * MarginRatio);

        Texture2D big = GetExtra("hit|" + word, () => BakeChip(word, Gdi.Color.FromArgb(255, 90, 90), 56, 235));
        Texture2D toast = GetExtra("toast|" + speaker + "|" + word,
            () => BakeChip("/ " + speaker + " 说出了 「" + word + "」", Gdi.Color.FromArgb(255, 120, 120), 26, 225));
        if (big == null || toast == null) return;

        batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp,
            DepthStencilState.None, RasterizerState.CullNone);

        // 1) 全屏红闪（0 ~ 0.4s，浅浅一下就好，别糊住画面）
        if (since < 0.4f)
        {
            float a = 0.20f * (1f - since / 0.4f);
            if (_white == null)
            {
                _white = new Texture2D(batch.GraphicsDevice, 1, 1);
                _white.SetData(new[] { XnaColor.White });
            }
            batch.Draw(_white, new Microsoft.Xna.Framework.Rectangle(0, 0, (int)W, (int)H),
                new XnaColor(255, 60, 60) * a);
        }

        // 2) 大字弹出（0 ~ 1.5s）：0.86 弹到 1.06，最后 0.5s 淡出
        if (since < 1.5f)
        {
            float grow = Math.Min(1f, since / 0.16f);
            float s = (0.88f + 0.12f * grow) * Math.Min(1f, H * 0.10f / big.Height);
            float a = since < 1.0f ? 1f : (1.5f - since) / 0.5f;
            float tx = (W - big.Width * s) / 2f;
            float ty = H * 0.17f;
            batch.Draw(big, new Vector2(MathF.Round(tx), MathF.Round(ty)), null,
                XnaColor.White * a, 0f, Vector2.Zero, s, SpriteEffects.None, 0f);
        }

        // 3) 右下角提示（0 ~ 2.8s）：从右边滑进来
        if (since < 2.8f)
        {
            float dy = 1f - Math.Min(1f, since / 0.18f);
            float a = since < 2.2f ? 1f : (2.8f - since) / 0.6f;
            float s = Math.Min(1f, (H * 0.042f) / toast.Height);
            float tx = W - margin - toast.Width * s + 40f * dy * 0f; // 水平不闪，垂直滑入
            float ty = H - margin - toast.Height * s - 26f * dy;
            batch.Draw(toast, new Vector2(MathF.Round(tx), MathF.Round(ty)), null,
                XnaColor.White * a, 0f, Vector2.Zero, s, SpriteEffects.None, 0f);
        }

        batch.End();
    }

    public static void Clear()
    {
        try { _white?.Dispose(); } catch { }
        _white = null;
        foreach (Texture2D tex in Cache.Values)
        {
            try { tex.Dispose(); } catch { }
        }
        Cache.Clear();
        Order.Clear();
        _extra.Clear();
        _unsupported = false;
        _loggedError = false;
        _loggedSpace = false;
    }

    // ---------------- 烤图 ----------------

    private static string Key(DangerWord w) => "chip|" + w.Source + "|" + w.Text;

    private static Gdi.Color AccentOf(WordSource src)
    {
        switch (src)
        {
            case WordSource.Hell: return Gdi.Color.FromArgb(255, 110, 110);
            case WordSource.Smart: return Gdi.Color.FromArgb(186, 148, 255);
            default: return Gdi.Color.FromArgb(63, 190, 239);
        }
    }

    private static readonly Dictionary<string, Texture2D> _extra = new Dictionary<string, Texture2D>();

    private static Texture2D GetExtra(string key, Func<Texture2D> make)
    {
        if (_extra.TryGetValue(key, out Texture2D t) && t != null) return t;
        try
        {
            Texture2D made = make();
            if (_extra.Count > 12) _extra.Clear();
            _extra[key] = made;
            return made;
        }
        catch (Exception ex) { Fail(ex); return null; }
    }

    private static void Register(string key)
    {
        Order.Enqueue(key);
        while (Order.Count > CacheLimit)
        {
            string old = Order.Dequeue();
            if (Cache.TryGetValue(old, out Texture2D t))
            {
                try { t.Dispose(); } catch { }
                Cache.Remove(old);
            }
        }
    }

    private static void Fail(Exception ex)
    {
        if (_loggedError) return;
        _loggedError = true;
        _unsupported = true;
        Logger.Log(LogLevel.Error, "VoiceKill", "HUD 贴图生成失败，游戏内显示已停用: " + ex.Message);
    }

    private static Texture2D BakeChip(string text, Gdi.Color accent) => BakeChip(text, accent, 42f, 215);

    /// <summary>把一句话烤成「圆角底 + 描边 + 白字」的贴图。</summary>
    private static Texture2D BakeChip(string text, Gdi.Color accent, float fontSize, int bgAlpha)
    {
        if (string.IsNullOrEmpty(text)) text = "?";

        int padX = (int)(fontSize * 0.52f);
        int padY = (int)(fontSize * 0.31f);
        int border = Math.Max(2, (int)(fontSize * 0.05f));
        int radius = (int)(fontSize * 0.31f);

        using Gdi.Font font = CreateFont(fontSize);

        Gdi.SizeF measured;
        using (var probe = new Gdi.Bitmap(1, 1))
        using (Gdi.Graphics pg = Gdi.Graphics.FromImage(probe))
        {
            pg.TextRenderingHint = TextRenderingHint.AntiAlias;
            measured = pg.MeasureString(text, font, int.MaxValue, Gdi.StringFormat.GenericTypographic);
        }

        int w = Math.Max((int)Math.Ceiling(measured.Width) + padX * 2 + border * 2, 16);
        int h = Math.Max((int)Math.Ceiling(measured.Height) + padY * 2 + border * 2, 16);

        using var bmp = new Gdi.Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (Gdi.Graphics g = Gdi.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.Clear(Gdi.Color.Transparent);

            using Gdi.Drawing2D.GraphicsPath path = RoundedRect(new Gdi.Rectangle(0, 0, w - 1, h - 1), radius);
            using (var fill = new Gdi.SolidBrush(Gdi.Color.FromArgb(bgAlpha, 8, 12, 18)))
                g.FillPath(fill, path);
            using (var pen = new Gdi.Pen(accent, border))
                g.DrawPath(pen, path);

            using var brush = new Gdi.SolidBrush(Gdi.Color.White);
            g.DrawString(text, font, brush, new Gdi.PointF(padX + border, padY + border),
                Gdi.StringFormat.GenericTypographic);
        }

        XnaColor[] pixels = ToPixels(bmp);
        var tex = new Texture2D(Draw.SpriteBatch.GraphicsDevice, w, h);
        tex.SetData(pixels);
        return tex;
    }

    private static Gdi.Font CreateFont(float size)
    {
        foreach (string name in new[] { "Microsoft YaHei", "微软雅黑", "SimHei", "黑体", "SimSun" })
        {
            try
            {
                var f = new Gdi.Font(name, size, Gdi.FontStyle.Bold, Gdi.GraphicsUnit.Pixel);
                if (f.FontFamily.Name == name || f.Name == name) return f;
                f.Dispose();
            }
            catch { }
        }
        return new Gdi.Font(Gdi.FontFamily.GenericSansSerif, size,
            Gdi.FontStyle.Bold, Gdi.GraphicsUnit.Pixel);
    }

    private static Gdi.Drawing2D.GraphicsPath RoundedRect(Gdi.Rectangle r, int radius)
    {
        int d = Math.Max(2, radius * 2);
        var path = new Gdi.Drawing2D.GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// GDI+ 位图 → 贴图像素。**必须预乘 alpha**
    /// （Celeste 用的是 XNA 的 BlendState.AlphaBlend，语义是 One / InvSrcAlpha）。
    /// </summary>
    private static XnaColor[] ToPixels(Gdi.Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        Gdi.Imaging.BitmapData data = bmp.LockBits(new Gdi.Rectangle(0, 0, w, h),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int bytes = w * h * 4;
            var buffer = new byte[bytes];
            Marshal.Copy(data.Scan0, buffer, 0, bytes);

            var pixels = new XnaColor[w * h];
            for (int i = 0; i < pixels.Length; i++)
            {
                byte b = buffer[i * 4 + 0];
                byte g = buffer[i * 4 + 1];
                byte r = buffer[i * 4 + 2];
                byte a = buffer[i * 4 + 3];

                if (a != 255 && a != 0)
                {
                    r = (byte)(r * a / 255);
                    g = (byte)(g * a / 255);
                    b = (byte)(b * a / 255);
                }
                pixels[i] = new XnaColor(r, g, b, a);
            }
            return pixels;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
