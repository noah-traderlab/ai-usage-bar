using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace AIUsageBar
{
    /// <summary>
    /// タスクトレイ用アイコンを描く。数字＝残り％、色＝状態（緑/橙/赤/灰）。
    /// Codexは角丸の四角、Claudeは丸で区別する。
    /// </summary>
    public enum TrayKind { Codex, Claude }

    public static class TrayIcons
    {
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr hIcon);

        public static readonly Color GoodColor = Theme.Hex("#1F9D55");
        public static readonly Color WarnColor = Theme.Hex("#E07B00");
        public static readonly Color DangerColor = Theme.Hex("#D93636");
        public static readonly Color UnknownColor = Theme.Hex("#7C7F87");

        public static int SmallIconSize
        {
            get { int s = GetSystemMetrics(49); return s > 0 ? s : 16; } // SM_CXSMICON
        }

        public static Color ColorFor(Level l)
        {
            switch (l)
            {
                case Level.Good: return GoodColor;
                case Level.Warn: return WarnColor;
                case Level.Danger: return DangerColor;
                default: return UnknownColor;
            }
        }

        public static Bitmap Render(TrayKind tool, double? remaining, int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                var level = UsageStore.LevelFor(remaining);
                var fill = ColorFor(level);
                var rect = new RectangleF(0, 0, size, size);
                if (tool == TrayKind.Codex)
                {
                    using (var path = Theme.RoundRect(new RectangleF(0.5f, 0.5f, size - 1, size - 1), size * 0.22f))
                    using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                }
                else
                {
                    using (var b = new SolidBrush(fill)) g.FillEllipse(b, 0.25f, 0.25f, size - 0.5f, size - 0.5f);
                }

                string text = remaining == null ? "—" : ((int)Math.Floor(remaining.Value)).ToString();
                DrawFitted(g, text, rect, size, tool);
            }
            return bmp;
        }

        static void DrawFitted(Graphics g, string text, RectangleF rect, int size, TrayKind tool)
        {
            // 数字を枠いっぱいに収める（3桁は横方向を詰める）
            float usable = tool == TrayKind.Claude ? size * 0.86f : size * 0.96f;
            float fontPx = text.Length >= 3 ? size * 0.62f : size * 0.78f;
            using (var family = new FontFamily("Segoe UI"))
            using (var path = new GraphicsPath())
            using (var fmt = new StringFormat(StringFormat.GenericTypographic))
            {
                path.AddString(text, family, (int)FontStyle.Bold, fontPx, new PointF(0, 0), fmt);
                var bounds = path.GetBounds();
                if (bounds.Width <= 0 || bounds.Height <= 0) return;
                float scaleX = Math.Min(1f, usable / bounds.Width);
                float scaleY = 1f;
                float maxH = size * (text == "—" ? 0.2f : 0.66f);
                if (bounds.Height > maxH) scaleY = maxH / bounds.Height;
                if (text != "—") scaleX = Math.Min(scaleX, Math.Max(scaleY, 0.62f) * 1.08f);
                var m = new Matrix();
                m.Translate(-bounds.X - bounds.Width / 2, -bounds.Y - bounds.Height / 2, MatrixOrder.Append);
                m.Scale(scaleX, scaleY, MatrixOrder.Append);
                m.Translate(rect.Width / 2, rect.Height / 2, MatrixOrder.Append);
                path.Transform(m);
                using (var b = new SolidBrush(Color.White)) g.FillPath(b, path);
            }
        }

        /// <summary>タスクバー用：上段にCodex、下段にClaudeの残り％を並べたアイコン</summary>
        public static Bitmap RenderPair(double? codex, double? claude, int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                float gap = Math.Max(1f, size / 16f);
                float rowH = (size - gap) / 2f;
                DrawRow(g, new RectangleF(0, 0, size, rowH), codex, TrayKind.Codex);
                DrawRow(g, new RectangleF(0, rowH + gap, size, rowH), claude, TrayKind.Claude);
            }
            return bmp;
        }

        static void DrawRow(Graphics g, RectangleF r, double? remaining, TrayKind kind)
        {
            var fill = ColorFor(UsageStore.LevelFor(remaining));
            float radius = kind == TrayKind.Codex ? r.Height * 0.28f : r.Height / 2f;
            using (var path = Theme.RoundRect(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), radius))
            using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            string text = remaining == null ? "—" : ((int)Math.Floor(remaining.Value)).ToString();
            using (var family = new FontFamily("Segoe UI"))
            using (var path = new GraphicsPath())
            using (var fmt = new StringFormat(StringFormat.GenericTypographic))
            {
                path.AddString(text, family, (int)FontStyle.Bold, r.Height, new PointF(0, 0), fmt);
                var bounds = path.GetBounds();
                if (bounds.Width <= 0 || bounds.Height <= 0) return;
                float maxH = r.Height * (text == "—" ? 0.16f : 0.72f);
                float sy = maxH / bounds.Height;
                float sx = Math.Min(sy * 1.05f, (r.Width * 0.84f) / bounds.Width);
                var m = new Matrix();
                m.Translate(-bounds.X - bounds.Width / 2, -bounds.Y - bounds.Height / 2, MatrixOrder.Append);
                m.Scale(sx, sy, MatrixOrder.Append);
                m.Translate(r.X + r.Width / 2, r.Y + r.Height / 2, MatrixOrder.Append);
                path.Transform(m);
                using (var b = new SolidBrush(Color.White)) g.FillPath(b, path);
            }
        }

        /// <summary>複数サイズを持つアイコンを作る（タスクバーが大きさに合わせて選ぶ）</summary>
        public static Icon MultiSizeIcon(Func<int, Bitmap> render, int[] sizes)
        {
            var ms = new System.IO.MemoryStream();
            var w = new System.IO.BinaryWriter(ms);
            var frames = new System.Collections.Generic.List<byte[]>();
            foreach (int size in sizes)
            {
                using (var bmp = render(size)) frames.Add(DibFrame(bmp));
            }
            w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                int s = sizes[i];
                w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)0); w.Write((byte)0);
                w.Write((ushort)1); w.Write((ushort)32); w.Write(frames[i].Length); w.Write(offset);
                offset += frames[i].Length;
            }
            foreach (var f in frames) w.Write(f);
            w.Flush();
            ms.Position = 0;
            return new Icon(ms);
        }

        static byte[] DibFrame(Bitmap bmp)
        {
            int n = bmp.Width;
            var ms = new System.IO.MemoryStream();
            var w = new System.IO.BinaryWriter(ms);
            w.Write(40); w.Write(n); w.Write(n * 2); w.Write((ushort)1); w.Write((ushort)32);
            w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            for (int y = n - 1; y >= 0; y--)
                for (int x = 0; x < n; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A);
                }
            int maskRow = ((n + 31) / 32) * 4;
            w.Write(new byte[maskRow * n]);
            w.Flush();
            return ms.ToArray();
        }

        public static Icon ToIcon(Bitmap bmp)
        {
            IntPtr h = bmp.GetHicon();
            try
            {
                using (var tmp = Icon.FromHandle(h)) return (Icon)tmp.Clone();
            }
            finally { DestroyIcon(h); }
        }
    }
}
