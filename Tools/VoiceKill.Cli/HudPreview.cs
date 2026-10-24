using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace VoiceKill.Cli
{
    /// <summary>
    /// 把 HUD 的烤图逻辑离线跑一遍，输出一张预览 PNG。
    /// 目的：在不开游戏的情况下确认「中文能不能渲染、尺寸合不合适、位置对不对」。
    /// </summary>
    internal static class HudPreview
    {
        private const float FontSize = 42f;
        private const int PadX = 22;
        private const int PadY = 13;
        private const int Border = 2;
        private const int Radius = 13;

        private const float MarginX = 8f;
        private const float MarginY = 8f;
        private const float Gap = 5f;

        private static readonly (string Text, int Kind)[] Sample =
        {
            ("玛瑙山", 4), ("冲刺", 2), ("差一点", 3), ("金草莓", 4),
            ("寄了", 3), ("爬墙", 4), ("完了", 3), ("蹭角", 4),
            ("旧城", 4), ("别啊", 3), ("核心", 4), ("稳住", 4),
        };

        public static int Run(string outPath)
        {
            // Celeste 逻辑分辨率 320x180，放大 6 倍 = 1920x1080（等于直播画布）
            const int px = 6;
            const int W = 320 * px, H = 180 * px;

            using var frame = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(frame))
            {
                g.Clear(Color.FromArgb(255, 26, 30, 38));
                using (var bgBrush = new SolidBrush(Color.FromArgb(255, 38, 44, 56)))
                    g.FillRectangle(bgBrush, 0, 60 * px, W, H - 120 * px);

                g.InterpolationMode = InterpolationMode.HighQualityBicubic;

                // 和 mod 里完全一致的算法：每行最多 10 个，超出换行；整体按宽度/高度自动缩放；右对齐
                var chips = new List<Bitmap>();
                foreach ((string text, int kind) in Sample) chips.Add(Bake(text, kind));

                var rows = new List<List<Bitmap>>();
                var cur = new List<Bitmap>();
                foreach (var c in chips)
                {
                    if (cur.Count >= 10) { rows.Add(cur); cur = new List<Bitmap>(); }
                    cur.Add(c);
                }
                if (cur.Count > 0) rows.Add(cur);

                float margin = W * 0.012f;
                float maxH = 0f;
                foreach (var c in chips) maxH = Math.Max(maxH, c.Height);
                float gap = maxH * 0.09f;
                float rowGap = maxH * 0.22f;

                float widest = 0f;
                foreach (var r in rows)
                {
                    float wsum = gap * (r.Count - 1);
                    foreach (var c in r) wsum += c.Width;
                    widest = Math.Max(widest, wsum);
                }

                float totalBakedH = maxH * rows.Count + rowGap * (rows.Count - 1);
                float scale = Math.Min((W - margin * 2f) / widest,
                              Math.Min((H * 0.075f) / maxH, (H * 0.30f) / totalBakedH));

                float y = margin;
                foreach (var r in rows)
                {
                    float rowW = (gap * (r.Count - 1)) * scale;
                    foreach (var c in r) rowW += c.Width * scale;
                    float x = W - margin - rowW;

                    foreach (var c in r)
                    {
                        int dw = (int)Math.Round(c.Width * scale);
                        int dh = (int)Math.Round(c.Height * scale);
                        g.DrawImage(c, new Rectangle((int)Math.Round(x), (int)Math.Round(y), dw, dh));
                        x += dw + gap * scale;
                    }
                    y += maxH * scale + rowGap * scale;
                }

                using var font = new Font("Microsoft YaHei", 20, FontStyle.Regular, GraphicsUnit.Pixel);
                using var small = new SolidBrush(Color.FromArgb(190, 255, 255, 255));
                g.DrawString("HUD 预览 · 右上角 · 每行最多 10 个 · 1920x1080",
                    font, small, 18, H - 40);
            }

            frame.Save(outPath, ImageFormat.Png);
            Console.WriteLine("已输出: " + outPath);

            // 第二张：踩雷动画的静态示意（红闪 + 大字 + 右下角播报）
            using (Graphics g = Graphics.FromImage(frame))
            {
                using (var shade = new SolidBrush(Color.FromArgb(120, 255, 60, 60)))
                    g.FillRectangle(shade, 0, 0, W, H);

                using var big = Bake("冲刺", 3, 96, 235);
                float bs = Math.Min(1f, (W * 0.34f) / big.Width);
                int bw = (int)(big.Width * bs), bh = (int)(big.Height * bs);
                g.DrawImage(big, new Rectangle((W - bw) / 2, (int)(H * 0.26f), bw, bh));

                using var toast = Bake("小闪 说出了 「冲刺」", 3, 34, 225);
                float ts = Math.Min(1f, (H * 0.055f) / toast.Height);
                int tw = (int)(toast.Width * ts), th = (int)(toast.Height * ts);
                float m2 = W * 0.012f;
                g.DrawImage(toast, new Rectangle(W - (int)m2 - tw, H - (int)m2 - th - 26, tw, th));
            }

            string hitPath = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(outPath) ?? ".",
                System.IO.Path.GetFileNameWithoutExtension(outPath) + "_hit.png");
            frame.Save(hitPath, ImageFormat.Png);
            Console.WriteLine("已输出: " + hitPath);
            return 0;
        }

        private static Bitmap Bake(string text, int kind) => Bake(text, kind, FontSize, 215);

        private static Bitmap Bake(string text, int kind, float fontSize, int bgAlpha)
        {
            Color accent = kind switch
            {
                3 => Color.FromArgb(255, 110, 110),
                2 => Color.FromArgb(186, 148, 255),
                _ => Color.FromArgb(63, 190, 239),
            };

            int padX = (int)(fontSize * 0.52f), padY = (int)(fontSize * 0.31f);
            int border = Math.Max(2, (int)(fontSize * 0.05f));
            int radius = (int)(fontSize * 0.31f);

            using var font = new Font("Microsoft YaHei", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);

            SizeF measured;
            using (var probe = new Bitmap(1, 1))
            using (var pg = Graphics.FromImage(probe))
            {
                pg.TextRenderingHint = TextRenderingHint.AntiAlias;
                measured = pg.MeasureString(text, font, int.MaxValue, StringFormat.GenericTypographic);
            }

            int w = (int)Math.Ceiling(measured.Width) + padX * 2 + border * 2;
            int h = (int)Math.Ceiling(measured.Height) + padY * 2 + border * 2;

            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                g.Clear(Color.Transparent);

                using var path = RoundedRect(new Rectangle(0, 0, w - 1, h - 1), radius);
                using (var fill = new SolidBrush(Color.FromArgb(bgAlpha, 8, 12, 18))) g.FillPath(fill, path);
                using (var pen = new Pen(accent, border)) g.DrawPath(pen, path);

                using var brush = new SolidBrush(Color.White);
                g.DrawString(text, font, brush, new PointF(padX + border, padY + border),
                    StringFormat.GenericTypographic);
            }
            return bmp;
        }

        private static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
