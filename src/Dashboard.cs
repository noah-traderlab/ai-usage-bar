using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace AIUsageBar
{
    public enum DashTab { Usage, History, Log, Team, Settings }

    /// <summary>
    /// タスクトレイから開く詳細画面（Mac版のポップオーバーに相当）。
    /// すべて自前で描画し、クリック位置は描画時に登録した領域で判定する。
    /// </summary>
    public class Dashboard : Form
    {
        public const int LogicalWidth = 404;
        /// <summary>検査用：画面の拡大率を指定して描画する（通常は null ＝ OS の設定に従う）</summary>
        public static float? ScaleOverride;
        public float UiScale { get { return scale; } }

        readonly UsageStore store;
        readonly Activity activity;
        readonly TeamService team;
        Theme theme;
        float scale = 1f;

        public DashTab Tab = DashTab.Usage;
        readonly Dictionary<DashTab, float> scroll = new Dictionary<DashTab, float>();
        float contentHeight;
        readonly List<Hit> hits = new List<Hit>();
        string hoverId;
        DateTime lastHidden = DateTime.MinValue;

        // 詳細ログの状態
        public DateTime LogDay = DateTime.Now.Date;
        public Tool? LogFilter;
        public bool LogByPrompt;
        readonly HashSet<string> expanded = new HashSet<string>();

        public event Action QuitRequested;
        public event Action RefreshRequested;
        public event Action SettingsChanged;
        public event Action RelocateRequested;
        public event Action OpenTeamManager;
        public event Action TeamChanged;
        public event Action FriendsChanged;
        /// <summary>フレンドの残り枠（TrayApp が設定する。null なら表示しない）</summary>
        public FriendsService Friends;

        Font fTitle, fHead, fBody, fBold, fSmall, fSmallBold, fBig, fTab;

        class Hit
        {
            public RectangleF R;
            public string Id;
            public Action Click;
            public Action<Point> RightClick;
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>true：タスクバーに並ぶ通常のウィンドウ / false：トレイから開くポップアップ</summary>
        public readonly bool TaskbarMode;
        Icon taskbarIcon;
        string taskbarIconKey;

        public Dashboard(UsageStore store, Activity activity, TeamService team = null)
        {
            this.store = store;
            this.activity = activity;
            this.team = team;
            TaskbarMode = store.Settings.ShowInTaskbar;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = TaskbarMode;
            TopMost = !TaskbarMode;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            KeyPreview = true;
            Text = AppInfo.Name;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            foreach (DashTab t in Enum.GetValues(typeof(DashTab))) scroll[t] = 0;
            ApplyTheme();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
                // 枠なしでもタスクバーのボタンで最小化・復元できるようにする
                if (TaskbarMode) cp.Style |= 0x00020000 | 0x00080000; // WS_MINIMIZEBOX | WS_SYSMENU
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { } // Windows 11 の角丸
        }

        public HashSet<string> Expanded { get { return expanded; } }
        public float ContentHeight { get { return contentHeight; } }
        public float ChromeHeight { get { return HeaderHeight + FooterHeight; } }

        public void ApplyTheme(bool? dark = null)
        {
            theme = Theme.Create(dark ?? Theme.SystemUsesDark());
            using (var g = CreateGraphics()) scale = ScaleOverride ?? g.DpiX / 96f;
            string ui = Theme.UiFontFamily;
            DisposeFonts();
            fTitle = new Font(ui, 11.5f, FontStyle.Bold);
            fHead = new Font(ui, 10f, FontStyle.Bold);
            fBody = new Font(ui, 9f);
            fBold = new Font(ui, 9f, FontStyle.Bold);
            fSmall = new Font(ui, 8f);
            fSmallBold = new Font(ui, 8f, FontStyle.Bold);
            fBig = new Font("Segoe UI", 17f, FontStyle.Bold);
            fTab = new Font(ui, 9f);
            BackColor = theme.Bg;
            Invalidate();
        }

        void DisposeFonts()
        {
            foreach (var f in new[] { fTitle, fHead, fBody, fBold, fSmall, fSmallBold, fBig, fTab }) if (f != null) f.Dispose();
        }

        float S(float v) { return v * scale; }

        // ------------------------------------------------------------------ 表示位置

        /// <summary>タスクバー表示：起動時に配置してタスクバーへ（必要なら最小化して）置く</summary>
        public void StartInTaskbar(bool minimized)
        {
            PlaceAt(Screen.PrimaryScreen, null);
            if (minimized) WindowState = FormWindowState.Minimized;
            Show();
            if (!minimized) Activate();
        }

        /// <summary>タスクバー表示：開いていれば収納、収納中なら開く</summary>
        public void ToggleFromTaskbar()
        {
            if (Visible && WindowState != FormWindowState.Minimized && Form.ActiveForm == this)
                WindowState = FormWindowState.Minimized;
            else
                RestoreFromTaskbar();
        }

        public void RestoreFromTaskbar()
        {
            if (!Visible) { PlaceAt(Screen.PrimaryScreen, null); Show(); }
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            Invalidate();
        }

        void PlaceAt(Screen screen, Point? cursor)
        {
            ApplyTheme();
            var wa = screen.WorkingArea;
            int w = (int)S(LogicalWidth);
            int h = Math.Min((int)S(700), wa.Height - (int)S(24));
            int margin = (int)S(12);
            int x = wa.Right - w - margin;
            int y = wa.Bottom - h - margin;
            if (cursor != null)
            {
                var c = cursor.Value;
                x = Math.Min(Math.Max(c.X - w / 2, wa.Left + margin), wa.Right - w - margin);
                if (!(screen.Bounds.Bottom - wa.Bottom > 0 || c.Y > wa.Top + wa.Height / 2)) y = wa.Top + margin;
                if (wa.Right < screen.Bounds.Right && c.X > wa.Right) x = wa.Right - w - margin;
                if (wa.Left > screen.Bounds.Left && c.X < wa.Left) x = wa.Left + margin;
            }
            Bounds = new Rectangle(x, y, w, h);
        }

        public void ShowNear(Point cursor)
        {
            if (TaskbarMode) { ToggleFromTaskbar(); return; }
            if (Visible) { Hide(); return; }
            if ((DateTime.UtcNow - lastHidden).TotalMilliseconds < 250) return; // アイコンのクリックで閉じた直後
            PlaceAt(Screen.FromPoint(cursor), cursor);
            Show();
            Activate();
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (TaskbarMode) return; // タスクバー表示では他をクリックしても閉じない
            if (menuOpen) return;
            lastHidden = DateTime.UtcNow;
            Hide();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) CloseToBackground();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; CloseToBackground(); return; }
            base.OnFormClosing(e);
        }

        /// <summary>閉じる操作：アプリは終了せず、タスクバー（またはトレイ）に残す</summary>
        void CloseToBackground()
        {
            if (TaskbarMode) WindowState = FormWindowState.Minimized;
            else Hide();
        }

        /// <summary>タスクバーのボタン：タイトルと、上段Codex・下段Claudeの残り％を描いたアイコン</summary>
        public void UpdateTaskbar(double? codex, double? claude)
        {
            string title = AppInfo.Name + " — Codex " + (codex == null ? "—" : Math.Floor(codex.Value) + "%") + "・Claude " + (claude == null ? "—" : Math.Floor(claude.Value) + "%");
            if (Text != title) Text = title;
            if (!TaskbarMode) return;
            string key = (codex == null ? "x" : Math.Floor(codex.Value).ToString()) + "|" + (claude == null ? "x" : Math.Floor(claude.Value).ToString());
            if (key == taskbarIconKey) return;
            try
            {
                var icon = TrayIcons.MultiSizeIcon(sz => TrayIcons.RenderPair(codex, claude, sz), new[] { 16, 20, 24, 32, 40, 48, 64 });
                var old = taskbarIcon;
                Icon = icon;
                taskbarIcon = icon;
                taskbarIconKey = key;
                if (old != null) old.Dispose();
            }
            catch { }
        }

        // ------------------------------------------------------------------ 入力

        float HeaderHeight { get { return S(98); } }
        float FooterHeight { get { return S(40); } }
        RectangleF Viewport { get { return new RectangleF(0, HeaderHeight, ClientSize.Width, ClientSize.Height - HeaderHeight - FooterHeight); } }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            ScrollBy(-e.Delta / 120f * S(56));
        }

        void ScrollBy(float dy)
        {
            float max = Math.Max(0, contentHeight - Viewport.Height);
            float v = Math.Max(0, Math.Min(max, scroll[Tab] + dy));
            if (v != scroll[Tab]) { scroll[Tab] = v; Invalidate(); }
        }

        Hit HitAt(Point p)
        {
            for (int i = hits.Count - 1; i >= 0; i--) if (hits[i].R.Contains(p)) return hits[i];
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var h = HitAt(e.Location);
            string id = h == null ? null : h.Id;
            Cursor = h != null ? Cursors.Hand : Cursors.Default;
            if (id != hoverId) { hoverId = id; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hoverId != null) { hoverId = null; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            var h = HitAt(e.Location);
            if (h == null) return;
            if (e.Button == MouseButtons.Left && h.Click != null) { h.Click(); Invalidate(); }
            else if (e.Button == MouseButtons.Right && h.RightClick != null) h.RightClick(e.Location);
        }

        public void SelectTab(DashTab t)
        {
            Tab = t;
            Invalidate();
        }

        // ------------------------------------------------------------------ 描画

        protected override void OnPaint(PaintEventArgs e)
        {
            RenderTo(e.Graphics, ClientSize);
        }

        /// <summary>画面にもビットマップにも描けるようにする（テスト用の画像出力にも使う）</summary>
        public void RenderTo(Graphics g, Size size)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            hits.Clear();
            using (var b = new SolidBrush(theme.Bg)) g.FillRectangle(b, 0, 0, size.Width, size.Height);

            var vp = new RectangleF(0, HeaderHeight, size.Width, size.Height - HeaderHeight - FooterHeight);
            float sc = scroll[Tab];

            // 本文（スクロール領域）
            var state = g.Save();
            g.SetClip(vp);
            var p = new Painter(this, g, S(14), vp.Top + S(10) - sc, size.Width - S(28), vp);
            switch (Tab)
            {
                case DashTab.Usage: DrawUsage(p); break;
                case DashTab.History: DrawHistory(p); break;
                case DashTab.Log: DrawLog(p); break;
                case DashTab.Team: DrawTeam(p); break;
                case DashTab.Settings: DrawSettings(p); break;
            }
            float newHeight = p.Y + sc - vp.Top + S(12);
            g.Restore(state);
            if (Math.Abs(newHeight - contentHeight) > 0.5f)
            {
                contentHeight = newHeight;
                float max = Math.Max(0, contentHeight - vp.Height);
                if (scroll[Tab] > max) { scroll[Tab] = max; BeginInvokeSafe(Invalidate); }
            }
            DrawScrollbar(g, vp);

            DrawHeader(g, size);
            DrawFooter(g, size);
        }

        void BeginInvokeSafe(Action a)
        {
            try { if (IsHandleCreated) BeginInvoke(a); } catch { }
        }

        void DrawScrollbar(Graphics g, RectangleF vp)
        {
            if (contentHeight <= vp.Height + 1) return;
            float ratio = vp.Height / contentHeight;
            float barH = Math.Max(S(24), vp.Height * ratio);
            float max = contentHeight - vp.Height;
            float top = vp.Top + (vp.Height - barH) * (scroll[Tab] / max);
            using (var path = Theme.RoundRect(new RectangleF(vp.Right - S(6), top + S(2), S(3.5f), barH - S(4)), S(2)))
            using (var b = new SolidBrush(Color.FromArgb(110, theme.Faint))) g.FillPath(b, path);
        }

        void DrawHeader(Graphics g, Size size)
        {
            using (var b = new SolidBrush(theme.Bg)) g.FillRectangle(b, 0, 0, size.Width, HeaderHeight);
            float x = S(14), y = S(12);
            DrawAppMark(g, new RectangleF(x, y + S(1), S(26), S(26)));
            using (var b = new SolidBrush(theme.Text)) g.DrawString(AppInfo.Name, fTitle, b, x + S(34), y - S(1));
            using (var b = new SolidBrush(theme.Sub)) g.DrawString("利用枠とトークンを、ひと目で", fSmall, b, x + S(35), y + S(19));

            // 更新ボタン
            bool busy = store.CodexFetching || store.Scanning;
            string label = busy ? "更新中…" : "更新";
            var sz = g.MeasureString(label, fBody);
            var r = new RectangleF(size.Width - S(14) - sz.Width - S(22), y + S(2), sz.Width + S(22), S(26));
            DrawButton(g, r, label, "hdr-refresh", false, () => { if (RefreshRequested != null) RefreshRequested(); });
            if (TaskbarMode)
            {
                var mr = new RectangleF(r.X - S(34), r.Y, S(28), r.Height);
                DrawButton(g, mr, "–", "hdr-minimize", false, () => { WindowState = FormWindowState.Minimized; });
            }

            // タブ
            float ty = S(52);
            var tabs = new[] { Tuple.Create(DashTab.Usage, "利用枠"), Tuple.Create(DashTab.History, "履歴"), Tuple.Create(DashTab.Log, "詳細ログ"), Tuple.Create(DashTab.Team, "チーム"), Tuple.Create(DashTab.Settings, "設定") };
            var track = new RectangleF(S(14), ty, size.Width - S(28), S(32));
            using (var path = Theme.RoundRect(track, S(8)))
            using (var b = new SolidBrush(theme.Dark ? Theme.Hex("#141517") : Theme.Hex("#E6E6EB"))) g.FillPath(b, path);
            float tw = (track.Width - S(6)) / tabs.Length;
            for (int i = 0; i < tabs.Length; i++)
            {
                var t = tabs[i];
                var tr = new RectangleF(track.X + S(3) + tw * i, track.Y + S(3), tw, track.Height - S(6));
                bool sel = Tab == t.Item1;
                string id = "tab-" + t.Item1;
                if (sel)
                {
                    using (var path = Theme.RoundRect(tr, S(6)))
                    using (var b = new SolidBrush(theme.Selected)) g.FillPath(b, path);
                }
                else if (hoverId == id)
                {
                    using (var path = Theme.RoundRect(tr, S(6)))
                    using (var b = new SolidBrush(Color.FromArgb(40, theme.Faint))) g.FillPath(b, path);
                }
                DrawCentered(g, t.Item2, sel ? fBold : fTab, sel ? theme.SelectedText : theme.Sub, tr);
                var tab = t.Item1;
                hits.Add(new Hit { R = tr, Id = id, Click = () => SelectTab(tab) });
            }
            using (var pen = new Pen(theme.Border)) g.DrawLine(pen, 0, HeaderHeight - 1, size.Width, HeaderHeight - 1);
        }

        void DrawFooter(Graphics g, Size size)
        {
            float top = size.Height - FooterHeight;
            using (var b = new SolidBrush(theme.Bg)) g.FillRectangle(b, 0, top, size.Width, FooterHeight);
            using (var pen = new Pen(theme.Border)) g.DrawLine(pen, 0, top, size.Width, top);
            using (var b = new SolidBrush(theme.Faint)) g.DrawString(AppInfo.Name + " " + AppInfo.Version + " · このPC向けのローカルビルド", fSmall, b, S(14), top + S(13));
            var sz = g.MeasureString("終了", fBody);
            var r = new RectangleF(size.Width - S(14) - sz.Width - S(22), top + S(7), sz.Width + S(22), S(26));
            DrawButton(g, r, "終了", "ftr-quit", false, () => { if (QuitRequested != null) QuitRequested(); });
        }

        void DrawAppMark(Graphics g, RectangleF r)
        {
            using (var path = Theme.RoundRect(r, S(6)))
            using (var b = new SolidBrush(theme.Dark ? Theme.Hex("#2F3136") : Theme.Hex("#26282C"))) g.FillPath(b, path);
            float d = r.Width * 0.46f, w = r.Width * 0.12f;
            using (var p1 = new Pen(Theme.Hex("#2EC4B6"), w) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(p1, r.X + r.Width * 0.14f, r.Y + r.Height * 0.2f, d, d, 30, 290);
            using (var p2 = new Pen(Theme.Hex("#F08A5D"), w) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(p2, r.X + r.Width * 0.40f, r.Y + r.Height * 0.34f, d, d, 210, 290);
        }

        void DrawButton(Graphics g, RectangleF r, string label, string id, bool primary, Action click)
        {
            bool hover = hoverId == id;
            Color bg = primary ? theme.Accent : (hover ? theme.CardHover : theme.Card);
            using (var path = Theme.RoundRect(r, S(6)))
            {
                using (var b = new SolidBrush(bg)) g.FillPath(b, path);
                using (var pen = new Pen(primary ? theme.Accent : theme.Border)) g.DrawPath(pen, path);
            }
            DrawCentered(g, label, fBody, primary ? Color.White : theme.Text, r);
            hits.Add(new Hit { R = r, Id = id, Click = click });
        }

        void DrawCentered(Graphics g, string text, Font f, Color c, RectangleF r)
        {
            using (var b = new SolidBrush(c))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(text, f, b, r, sf);
        }

        // ------------------------------------------------------------------ 描画ヘルパー

        class Painter
        {
            public readonly Dashboard D;
            public readonly Graphics G;
            public float X, Y, W;
            public bool Measure;
            readonly RectangleF viewport;

            public Painter(Dashboard d, Graphics g, float x, float y, float w, RectangleF viewport)
            {
                D = d; G = g; X = x; Y = y; W = w; this.viewport = viewport;
            }

            public Painter Sub(float x, float y, float w, bool measure)
            {
                return new Painter(D, G, x, y, w, viewport) { Measure = measure || Measure };
            }

            public bool Visible(float top, float bottom)
            {
                return bottom >= viewport.Top && top <= viewport.Bottom;
            }

            public float TextHeight(string text, Font f, float width, int maxLines)
            {
                if (string.IsNullOrEmpty(text)) return 0;
                using (var sf = Format(maxLines))
                {
                    var sz = G.MeasureString(text, f, new SizeF(width, maxLines > 0 ? f.GetHeight(G) * maxLines + 1 : 100000), sf);
                    return (float)Math.Ceiling(sz.Height);
                }
            }

            static StringFormat Format(int maxLines)
            {
                var sf = new StringFormat(StringFormat.GenericDefault);
                sf.Trimming = StringTrimming.EllipsisCharacter;
                if (maxLines == 1) sf.FormatFlags |= StringFormatFlags.NoWrap;
                if (maxLines > 0) sf.FormatFlags |= StringFormatFlags.LineLimit;
                return sf;
            }

            /// <summary>折り返しテキスト。高さを返す（Yは進めない）</summary>
            public float TextAt(string text, Font f, Color c, float x, float y, float width, int maxLines)
            {
                float h = TextHeight(text, f, width, maxLines);
                if (maxLines > 0) h = Math.Min(h, f.GetHeight(G) * maxLines + 2);
                if (!Measure && h > 0 && Visible(y, y + h))
                {
                    using (var b = new SolidBrush(c))
                    using (var sf = Format(maxLines))
                        G.DrawString(text, f, b, new RectangleF(x, y, width, h + 2), sf);
                }
                return h;
            }

            public void Text(string text, Font f, Color c, int maxLines = 0, float gap = 0)
            {
                float h = TextAt(text, f, c, X, Y, W, maxLines);
                Y += h + gap;
            }

            public float TextWidth(string text, Font f)
            {
                using (var sf = new StringFormat(StringFormat.GenericDefault))
                    return G.MeasureString(text, f, 100000, sf).Width;
            }

            public void Space(float v) { Y += D.S(v); }

            public void AddHit(RectangleF r, string id, Action click)
            {
                if (Measure) return;
                var vis = RectangleF.Intersect(r, viewport);
                if (vis.Width <= 0 || vis.Height <= 0) return;
                D.hits.Add(new Hit { R = vis, Id = id, Click = click });
            }

            public void AddRightHit(RectangleF r, string id, Action<Point> click)
            {
                if (Measure) return;
                var vis = RectangleF.Intersect(r, viewport);
                if (vis.Width <= 0 || vis.Height <= 0) return;
                D.hits.Add(new Hit { R = vis, Id = id, RightClick = click });
            }

            public void Fill(RectangleF r, Color c, float radius)
            {
                if (Measure || !Visible(r.Top, r.Bottom)) return;
                using (var path = Theme.RoundRect(r, radius))
                using (var b = new SolidBrush(c)) G.FillPath(b, path);
            }

            public void Stroke(RectangleF r, Color c, float radius)
            {
                if (Measure || !Visible(r.Top, r.Bottom)) return;
                using (var path = Theme.RoundRect(r, radius))
                using (var pen = new Pen(c)) G.DrawPath(pen, path);
            }

            public void Line(float x1, float y1, float x2, float y2, Color c)
            {
                if (Measure || !Visible(Math.Min(y1, y2), Math.Max(y1, y2))) return;
                using (var pen = new Pen(c)) G.DrawLine(pen, x1, y1, x2, y2);
            }

            /// <summary>カード：中身を一度計測してから背景を描き、中身を描く</summary>
            public void Card(Action<Painter> content, string hoverId = null, Action click = null, Color? tint = null)
            {
                float pad = D.S(14);
                var m = Sub(X + pad, Y + pad, W - pad * 2, true);
                content(m);
                float h = m.Y - Y + pad;
                var r = new RectangleF(X, Y, W, h);
                bool hover = hoverId != null && D.hoverId == hoverId;
                if (!Measure && !D.theme.Dark && Visible(r.Top, r.Bottom))
                    for (int i = 4; i >= 1; i--)
                        using (var sp = Theme.RoundRect(new RectangleF(r.X - i, r.Y + i + 1, r.Width + i * 2, r.Height + i), D.S(10) + i))
                        using (var sb = new SolidBrush(Color.FromArgb(6, 20, 26, 40))) G.FillPath(sb, sp);
                Fill(r, hover ? D.theme.CardHover : D.theme.Card, D.S(10));
                Stroke(r, D.theme.Border, D.S(10));
                if (tint != null && !Measure && Visible(r.Top, r.Bottom))
                {
                    using (var path = Theme.RoundRect(new RectangleF(r.X, r.Y + D.S(12), D.S(3), Math.Max(0, r.Height - D.S(24))), D.S(1.5f)))
                    using (var b = new SolidBrush(tint.Value)) G.FillPath(b, path);
                }
                if (click != null) AddHit(r, hoverId, click);
                if (!Measure) content(Sub(X + pad, Y + pad, W - pad * 2, false));
                Y += h + D.S(10);
            }

            public void Bar(float y, float width, double? remaining, Color color)
            {
                float h = D.S(7);
                var track = new RectangleF(X, y, width, h);
                Fill(track, D.theme.Track, h / 2);
                if (remaining != null && remaining.Value > 0)
                    Fill(new RectangleF(X, y, Math.Max(h, (float)(width * Math.Min(100, remaining.Value) / 100)), h), color, h / 2);
            }

            /// <summary>横並びのチップ。幅を返す</summary>
            public float Chip(float x, float y, string label, bool selected, string id, Action click, Font f = null, float padding = 0)
            {
                f = f ?? D.fSmall;
                float w = TextWidth(label, D.fSmallBold) + (padding > 0 ? padding : D.S(24));
                float h = D.S(24);
                var r = new RectangleF(x, y, w, h);
                bool hover = D.hoverId == id;
                Fill(r, selected ? D.theme.Accent : (hover ? D.theme.CardHover : D.theme.Card), h / 2);
                if (!selected) Stroke(r, D.theme.Border, h / 2);
                if (!Measure && Visible(r.Top, r.Bottom))
                    D.DrawCentered(G, label, selected ? D.fSmallBold : f, selected ? Color.White : D.theme.Text, r);
                AddHit(r, id, click);
                return w;
            }

            public void Chips(IList<Tuple<string, bool, string, Action>> items)
            {
                float x = X, h = D.S(24), gap = D.S(5);
                foreach (var it in items)
                {
                    float w = TextWidth(it.Item1, D.fSmallBold) + D.S(18);
                    if (x > X && x + w > X + W) { x = X; Y += h + gap; }
                    x += Chip(x, Y, it.Item1, it.Item2, it.Item3, it.Item4, null, D.S(18)) + gap;
                }
                Y += h;
            }

            public void Toggle(string label, string desc, bool value, string id, Action click, string lockedReason = null)
            {
                if (lockedReason != null) { desc = (string.IsNullOrEmpty(desc) ? "" : desc + "\n") + "※" + lockedReason; click = null; }
                float sw = D.S(36), sh = D.S(20);
                float textW = W - sw - D.S(12);
                float top = Y;
                float h1 = TextAt(label, D.fBold, D.theme.Text, X, Y, textW, 0);
                float h2 = string.IsNullOrEmpty(desc) ? 0 : TextAt(desc, D.fSmall, D.theme.Sub, X, Y + h1 + D.S(2), textW, 0) + D.S(2);
                float rowH = Math.Max(sh, h1 + h2);
                var sr = new RectangleF(X + W - sw, top + (Math.Min(rowH, D.S(28)) - sh) / 2, sw, sh);
                Fill(sr, click == null ? D.theme.Track : (value ? D.theme.Accent : D.theme.Track), sh / 2);
                float knob = sh - D.S(6);
                if (!Measure && Visible(sr.Top, sr.Bottom))
                    using (var b = new SolidBrush(Color.White))
                        G.FillEllipse(b, value ? sr.Right - knob - D.S(3) : sr.X + D.S(3), sr.Y + D.S(3), knob, knob);
                if (click != null) AddHit(new RectangleF(X, top, W, rowH), id, click);
                Y += rowH;
            }

            public float Link(float x, float y, string label, string id, Action click)
            {
                float w = TextWidth(label, D.fSmall);
                float h = D.fSmall.GetHeight(G);
                bool hover = D.hoverId == id;
                if (!Measure && Visible(y, y + h))
                {
                    using (var f = new Font(D.fSmall, hover ? FontStyle.Underline : FontStyle.Regular))
                    using (var b = new SolidBrush(D.theme.Link))
                        G.DrawString(label, f, b, x, y);
                }
                AddHit(new RectangleF(x, y, w, h), id, click);
                return w;
            }

            public void Button(float x, float y, float w, string label, string id, Action click, bool primary = false)
            {
                var r = new RectangleF(x, y, w, D.S(26));
                if (Measure || !Visible(r.Top, r.Bottom)) { return; }
                var clip = G.Clip;
                D.DrawButton(G, r, label, id, primary, click);
                // DrawButton は hits に直接追加するので、表示範囲外の分を除く
                var last = D.hits[D.hits.Count - 1];
                last.R = RectangleF.Intersect(last.R, viewport);
            }

            public void Badge(float x, float y, string text, Color color, out float width)
            {
                float w = TextWidth(text, D.fSmallBold) + D.S(10);
                float h = D.S(18);
                width = w;
                var r = new RectangleF(x, y, w, h);
                Fill(r, Color.FromArgb(D.theme.Dark ? 60 : 32, color), D.S(4));
                if (!Measure && Visible(r.Top, r.Bottom))
                    D.DrawCentered(G, text, D.fSmallBold, color, r);
            }
        }

        // ------------------------------------------------------------------ 利用枠

        void DrawUsage(Painter p)
        {
            var now = DateTime.UtcNow;

            // Codex
            p.Card(c =>
            {
                var snap = store.Codex;
                double? min = store.CodexMinRemaining(now);
                CardHeader(c, "Codex", CodexSubtitle(), min, theme.CodexTint);
                if (store.CodexError != null)
                {
                    c.Text(store.CodexError, fSmall, theme.Danger, 0, S(6));
                }
                if (snap == null || snap.Main == null)
                {
                    c.Text(store.CodexFetching ? "接続しています…" : "利用枠は未取得です", fBody, theme.Sub, 0, S(2));
                    return;
                }
                bool stale = store.CodexStale(now);
                foreach (var w in snap.Main.Windows)
                    QuotaRow(c, w.Label, UsageStore.WindowExpired(w, now) || stale ? (double?)null : w.RemainingPercent, w.RemainingPercent,
                        ResetText(w, now), UsageStore.WindowExpired(w, now) ? "リセット時刻を過ぎました。更新が必要です" : null);
                if (snap.Others.Count > 0)
                {
                    c.Space(2);
                    c.Text("モデル別の枠", fSmallBold, theme.Sub, 0, S(3));
                    foreach (var g in snap.Others)
                    {
                        string line = g.Title + "　" + string.Join("・", g.Windows.Select(w => w.Label + " 残り" + Math.Floor(w.RemainingPercent) + "%"));
                        c.Text(line, fSmall, theme.Sub, 1, S(2));
                    }
                }
                c.Space(4);
                c.Text((stale ? "古い情報です。" : "") + "取得：" + Fmt.DateTimeShort(snap.ObservedAtUtc.ToLocalTime()) + "（" + Fmt.Ago(snap.ObservedAtUtc, now) + "）" + (store.CodexFetching ? "　更新中…" : ""), fSmall, stale ? theme.Warn : theme.Faint);
            }, null, null, theme.CodexTint);

            // Claude
            p.Card(c =>
            {
                var cl = store.Claude;
                double? min = store.ClaudeMinRemaining(now);
                CardHeader(c, "Claude 共有枠", "通常チャット・Claude Codeの共有枠 · Desktop保存履歴", min, theme.ClaudeTint);
                if (cl == null)
                {
                    c.Text(store.ClaudeError ?? "利用枠は未取得です", fSmall, theme.Sub, 0, S(4));
                }
                else
                {
                    bool stale = cl.IsStale(now, store.Settings.ClaudeIntervalMinutes);
                    QuotaRow(c, "5時間", stale ? null : cl.FiveHourRemaining, cl.FiveHourRemaining ?? 0, null, null);
                    QuotaRow(c, "週間", stale ? null : cl.WeeklyRemaining, cl.WeeklyRemaining ?? 0, null, null);
                    c.Text("前回の記録：" + Fmt.DateTimeShort(cl.SampleAtUtc.ToLocalTime()) + "（" + Fmt.Ago(cl.SampleAtUtc, now) + "）" + (stale ? "　未取得または古い情報" : ""), fSmall, stale ? theme.Warn : theme.Faint, 0, S(4));
                }
                float y = c.Y;
                c.Link(c.X, y, "Claudeで使用状況を開く", "lnk-claude-usage", () => OpenUrl("https://claude.ai/settings/usage"));
                c.Y = y + fSmall.GetHeight(c.G) + S(6);
                c.Text("リセット時刻・モデル別枠はアプリで確認できます。保存履歴は15分（確認間隔を長くした場合はその分を加えた時間）を超えると古い情報として扱います。本アプリの更新ボタンだけではClaude側の保存履歴は新しくなりません。", fSmall, theme.Faint);
            }, null, null, theme.ClaudeTint);

            DrawFriends(p, now);

            p.Text("タスクトレイには、5時間・週間のうち最も少ない残り枠を表示します。緑：20%超、橙：20%以下、赤：10%以下、灰：未取得・古い情報。Codexは角丸の四角、Claudeは丸のアイコンです。", fSmall, theme.Faint, 0, S(8));
            p.Text("ChatGPT通常チャットの残量・個別トークン数は取得未対応です。Claude通常チャット単独のトークン数も分離できません。", fSmall, theme.Faint);
        }

        // ------------------------------------------------------------------ フレンドの残り枠

        void DrawFriends(Painter p, DateTime now)
        {
            var fs = Friends;
            if (fs == null) return;
            p.Card(c =>
            {
                float top = c.Y;
                float h1 = c.TextAt("フレンドの残り枠", fHead, theme.Text, c.X, top, c.W, 1);
                float h2 = c.TextAt("共有フォルダ経由 · 表示名と残り%だけを共有", fSmall, theme.Sub, c.X, top + h1 + S(1), c.W, 1);
                c.Y = top + h1 + h2 + S(10);
                if (!fs.Allowed)
                {
                    c.Text("管理者の設定（DisableFriends）により、フレンド機能は使えません。", fSmall, theme.Sub);
                    return;
                }
                if (!fs.Config.IsConfigured)
                {
                    c.Text("フレンドと Claude・Codex の残り枠を見せ合えます。OneDrive・Googleドライブなど、フレンドと共有しているフォルダを使います（アプリは通信しません）。メールアドレス・トークン数・作業内容は共有しません。", fSmall, theme.Sub, 0, S(10));
                    c.Button(c.X, c.Y, c.W, "フレンド機能を設定する…", "friends-setup", () => RunDialog(() => FriendsUi.Configure(this, fs), AfterFriendsChange), true);
                    c.Y += S(26);
                    return;
                }
                if (fs.LastError != null) c.Text(fs.LastError, fSmall, theme.Danger, 0, S(6));
                var others = fs.Others;
                if (others.Count == 0)
                    c.Text(fs.LastReadUtc == null ? "読み込んでいます…" : "まだフレンドがいません。設定画面の招待コードをフレンドに送ってください。", fSmall, theme.Sub, 0, S(8));
                foreach (var fr in others) FriendRow(c, fr, now);
                var me = fs.Mine;
                if (me != null)
                {
                    c.Line(c.X, c.Y, c.X + c.W, c.Y, theme.Border);
                    c.Space(8);
                    FriendRow(c, me, now);
                }
                float half = (c.W - S(8)) / 2;
                float y = c.Y;
                c.Button(c.X, y, half, "設定…", "friends-settings", () => RunDialog(() => FriendsUi.Configure(this, fs), AfterFriendsChange));
                c.Button(c.X + half + S(8), y, half, "フォルダを開く", "friends-folder", () =>
                {
                    string dir = fs.Config.FriendsDir;
                    if (dir != null && Directory.Exists(dir)) Hardening.OpenFolder(dir, null);
                    else TeamUi.Error(this, "共有フォルダが見つかりません：" + (fs.Config.Folder ?? ""));
                });
                c.Y = y + S(26);
            }, null, null, theme.Accent);
        }

        void FriendRow(Painter c, FriendStatus fr, DateTime now)
        {
            bool stale = fr.IsStale(now);
            double? min = stale ? null : fr.MinRemaining;
            Color col = theme.LevelColor(UsageStore.LevelFor(min));
            float top = c.Y;
            string right = stale ? "— 更新なし" : min == null ? "— 未取得" : "残り " + Math.Floor(min.Value) + "%";
            float rw = c.TextWidth(right, fBold);
            c.TextAt(fr.IsMe ? fr.Name + "（あなた・公開中の内容）" : fr.Name, fBold, stale ? theme.Sub : theme.Text, c.X, top, c.W - rw - S(8), 1);
            c.TextAt(right, fBold, col, c.X + c.W - rw, top, rw + S(4), 1);
            float y = top + fBold.GetHeight(c.G) + S(4);
            c.Bar(y, c.W, min, col);
            c.Y = y + S(7) + S(5);
            string claude = fr.ClaudeFiveHour == null && fr.ClaudeWeekly == null ? "Claude —"
                : "Claude 5時間 " + Pct(fr.ClaudeFiveHour) + "・週間 " + Pct(fr.ClaudeWeekly);
            string codex = fr.Codex.Count == 0 ? "Codex —"
                : "Codex " + string.Join("・", fr.Codex.Select(w => w.Label + " " + Math.Floor(w.Remaining) + "%"));
            c.Text(claude + "　" + codex, fSmall, stale ? theme.Faint : theme.Sub, 2, S(1));
            var reset = fr.NextCodexReset(now);
            string when = stale
                ? StaleText(fr, now)
                : (reset == null ? "" : "Codexリセット " + Fmt.DateTimeShort(reset.Value.ToLocalTime()) + "（" + Fmt.Until(reset.Value, now) + "）· ") + Fmt.Ago(fr.UpdatedAtUtc, now) + "に更新";
            c.Text(when, fSmall, stale ? theme.Warn : theme.Faint, 1, 0);
            c.Space(10);
        }

        static string StaleText(FriendStatus fr, DateTime now)
        {
            return AIUsageBar.Friends.StaleMinutes + "分以上更新がありません（最終 " + Fmt.DateTimeShort(fr.UpdatedAtUtc.ToLocalTime()) + "・" + Fmt.Ago(fr.UpdatedAtUtc, now) + "）";
        }

        static string Pct(double? v) { return v == null ? "—" : Math.Floor(v.Value) + "%"; }

        void AfterFriendsChange()
        {
            if (FriendsChanged != null) FriendsChanged();
            Invalidate();
        }

        string CodexSubtitle()
        {
            var s = store.Codex;
            var parts = new List<string> { "ChatGPTアカウント" };
            if (s != null && s.PlanType != null) parts.Add(char.ToUpper(s.PlanType[0]) + s.PlanType.Substring(1));
            if (s != null && s.AccountEmail != null) parts.Add(s.AccountEmail);
            return string.Join(" · ", parts);
        }

        void CardHeader(Painter c, string title, string subtitle, double? min, Color tint)
        {
            float top = c.Y;
            string pct = min == null ? "—" : Math.Floor(min.Value) + "%";
            var level = UsageStore.LevelFor(min);
            float pw = c.TextWidth(pct, fBig);
            float textW = c.W - pw - S(8);
            float h1 = c.TextAt(title, fHead, theme.Text, c.X, top, textW, 1);
            float h2 = c.TextAt(subtitle, fSmall, theme.Sub, c.X, top + h1 + S(1), textW, 2);
            if (!c.Measure && c.Visible(top, top + S(30)))
            {
                using (var b = new SolidBrush(theme.LevelColor(level)))
                    c.G.DrawString(pct, fBig, b, c.X + c.W - pw + S(4), top - S(4));
            }
            c.Y = top + Math.Max(h1 + h2 + S(1), S(34)) + S(8);
        }

        void QuotaRow(Painter c, string label, double? remaining, double rawRemaining, string resetText, string warning)
        {
            var level = UsageStore.LevelFor(remaining);
            Color col = theme.LevelColor(level);
            float top = c.Y;
            c.TextAt(label, fBold, theme.Text, c.X, top, c.W / 2, 1);
            string right = remaining == null ? "— 未取得" : "残り " + Math.Floor(remaining.Value) + "%";
            float rw = c.TextWidth(right, fBold);
            c.TextAt(right, fBold, col, c.X + c.W - rw, top, rw + S(4), 1);
            float y = top + fBold.GetHeight(c.G) + S(4);
            c.Bar(y, c.W, remaining ?? (double?)null, col);
            y += S(7) + S(4);
            c.Y = y;
            if (resetText != null) c.Text(resetText, fSmall, theme.Faint, 1, 0);
            if (warning != null) c.Text(warning, fSmall, theme.Warn, 0, 0);
            c.Space(9);
        }

        static string ResetText(QuotaWindow w, DateTime now)
        {
            if (w.ResetsAtUtc == null) return null;
            return "リセット " + Fmt.DateTimeShort(w.ResetsAtUtc.Value.ToLocalTime()) + "（" + Fmt.Until(w.ResetsAtUtc.Value, now) + "）";
        }

        // ------------------------------------------------------------------ 履歴

        void DrawHistory(Painter p)
        {
            var today = DateTime.Now.Date;
            var days = Enumerable.Range(0, 7).Select(i => today.AddDays(i - 6)).ToList();

            if (store.Scanning)
            {
                string pct = store.ScanTotal > 0 ? ((int)(100.0 * store.ScanDone / store.ScanTotal)).ToString() : "0";
                p.Text("ローカル記録を集計中… " + pct + "％（表示は途中の値です）", fSmall, theme.Warn, 0, S(8));
            }

            // Codex（サービスの日別）
            p.Card(c =>
            {
                c.Text("Codex", fHead, theme.Text, 1, S(1));
                c.Text("アカウント全体（Codexが返す日別トークン）", fSmall, theme.Sub, 1, S(8));
                var snap = store.Codex;
                var values = new List<long?>();
                foreach (var d in days)
                {
                    long? v = null;
                    if (snap != null && snap.Daily != null)
                    {
                        var b = snap.Daily.FirstOrDefault(x => x.Date == d.ToString("yyyy-MM-dd"));
                        if (b != null) v = b.Tokens;
                    }
                    values.Add(v);
                }
                if (snap == null || snap.Daily == null)
                {
                    c.Text(snap != null ? "利用枠は取得できましたが、トークン履歴は未取得です" : "未取得です", fSmall, theme.Sub, 0, S(6));
                }
                Totals(c, values);
                Chart(c, days, values, theme.CodexTint, "codex");
                c.Text("Codexが返すアカウント履歴です。当日分が未反映のことがあります。日付はサービスの集計日を使用しています。ChatGPT通常チャットを含む合計としては扱っていません。", fSmall, theme.Faint);
            }, null, null, theme.CodexTint);

            // Claude Code（このPC）
            p.Card(c =>
            {
                c.Text("Claude Code", fHead, theme.Text, 1, S(1));
                c.Text("このPCの記録（入力・出力・キャッシュを含む）", fSmall, theme.Sub, 1, S(8));
                var sums = days.Select(d => store.Logs.DayTotal(Tool.ClaudeCode, d)).ToList();
                var values = sums.Select(s => s.Responses > 0 ? (long?)s.Total : (store.Logs.LastScanUtc != null ? (long?)0 : null)).ToList();
                Totals(c, values);
                Chart(c, days, values, theme.ClaudeTint, "claude");
                var total = new TokenSum();
                foreach (var s in sums) total.Add(s);
                c.Text("7日間の内訳：" + Activity.BreakdownText(Tool.ClaudeCode, total), fSmall, theme.Sub, 0, S(6));
                c.Text("このPCのClaude Code標準保存先から集計。入力・出力・キャッシュを含み、同じ応答の重複を除いています。端末の現地時刻で日付を区切ります。API利用とサブスクリプション利用を含む可能性があり、請求額や残量には換算しません。", fSmall, theme.Faint);
                if (store.Logs.DiscardedLines > 0 || store.Logs.SkippedRecords > 0 || store.ScanError != null)
                    c.Text("一部の記録を読み取れず、集計に含められていません", fSmall, theme.Warn, 0, 0);
            }, null, null, theme.ClaudeTint);

            // Codex（このPC・内訳）
            p.Card(c =>
            {
                c.Text("Codex（このPCの内訳）", fHead, theme.Text, 1, S(1));
                c.Text("このPCのCodexセッション記録から集計（応答ID単位で重複除去）", fSmall, theme.Sub, 0, S(8));
                var sums = days.Select(d => store.Logs.DayTotal(Tool.Codex, d)).ToList();
                var values = sums.Select(s => s.Responses > 0 ? (long?)s.Total : (store.Logs.LastScanUtc != null ? (long?)0 : null)).ToList();
                Totals(c, values);
                Chart(c, days, values, Theme.Mix(theme.CodexTint, theme.Sub, 0.35), "codex-local");
                var total = new TokenSum();
                foreach (var s in sums) total.Add(s);
                c.Text("7日間の内訳：" + Activity.BreakdownText(Tool.Codex, total), fSmall, theme.Sub, 0, S(6));
                c.Text("Codexの入力にはキャッシュ済み入力が含まれ、出力には推論出力が含まれます。サービス側の合計とは集計日や範囲が異なる場合があります。", fSmall, theme.Faint);
            }, null, null, theme.CodexTint);
        }

        void Totals(Painter c, List<long?> values)
        {
            long sum = values.Where(v => v != null).Sum(v => v.Value);
            long? today = values[values.Count - 1];
            float half = c.W / 2;
            float top = c.Y;
            c.TextAt("表示7日間の取得済み合計", fSmall, theme.Sub, c.X, top, half, 1);
            c.TextAt("今日のトークン", fSmall, theme.Sub, c.X + half, top, half, 1);
            float y = top + fSmall.GetHeight(c.G) + S(1);
            c.TextAt(values.All(v => v == null) ? "—" : Fmt.Tokens(sum), fHead, theme.Text, c.X, y, half, 1);
            c.TextAt(today == null ? "未反映 / 未取得" : Fmt.Tokens(today.Value), today == null ? fBody : fHead, today == null ? theme.Faint : theme.Text, c.X + half, y, half, 1);
            c.Y = y + fHead.GetHeight(c.G) + S(10);
        }

        void Chart(Painter c, List<DateTime> days, List<long?> values, Color color, string idPrefix)
        {
            float chartH = S(84);
            float labelH = fSmall.GetHeight(c.G) * 2;
            float top = c.Y + S(12);
            long max = Math.Max(1, values.Where(v => v != null).Select(v => v.Value).DefaultIfEmpty(0).Max());
            float colW = c.W / days.Count;
            float barW = Math.Min(S(26), colW * 0.56f);
            c.Line(c.X, top + chartH, c.X + c.W, top + chartH, theme.Border);
            for (int i = 0; i < days.Count; i++)
            {
                float cx = c.X + colW * i + colW / 2;
                var v = values[i];
                string id = idPrefix + "-bar-" + i;
                bool hover = hoverId == id;
                if (v == null)
                {
                    c.Fill(new RectangleF(cx - barW / 2, top + chartH - S(3), barW, S(3)), theme.Track, S(1));
                }
                else
                {
                    float h = v.Value <= 0 ? 0 : Math.Max(S(3), chartH * v.Value / max);
                    var r = new RectangleF(cx - barW / 2, top + chartH - h, barW, h);
                    c.Fill(r, hover ? Theme.Mix(color, theme.Text, 0.25) : color, S(3));
                    if (!c.Measure && v.Value > 0)
                    {
                        string t = Fmt.Tokens(v.Value);
                        float tw = c.TextWidth(t, fSmall);
                        c.TextAt(t, fSmall, hover ? theme.Text : theme.Faint, cx - tw / 2, top + chartH - h - fSmall.GetHeight(c.G), tw + S(4), 1);
                    }
                }
                string label = Fmt.DayShort(days[i]) + "\n" + (i == days.Count - 1 ? "今日" : Fmt.Weekday(days[i]));
                if (!c.Measure && c.Visible(top + chartH, top + chartH + labelH + S(4)))
                {
                    using (var b = new SolidBrush(i == days.Count - 1 ? theme.Text : theme.Faint))
                    using (var sf = new StringFormat { Alignment = StringAlignment.Center })
                        c.G.DrawString(label, fSmall, b, new RectangleF(cx - colW / 2, top + chartH + S(3), colW, labelH + S(4)), sf);
                }
                var day = days[i];
                c.AddHit(new RectangleF(cx - colW / 2, top, colW, chartH + labelH), id, () => { LogDay = day; SelectTab(DashTab.Log); });
            }
            c.Y = top + chartH + labelH + S(10);
        }

        // ------------------------------------------------------------------ 詳細ログ

        void DrawLog(Painter p)
        {
            var today = DateTime.Now.Date;
            if (store.Policy.DisableActivityLog)
            {
                p.Card(c =>
                {
                    c.Text("詳細ログは組織のポリシーで無効です", fHead, theme.Text, 0, S(4));
                    c.Text("管理者の設定（DisableActivityLog）により、セッションや入力の一覧は表示されません。作業フォルダや依頼文の位置も保存しません。", fSmall, theme.Sub, 0, 0);
                });
                return;
            }
            if (!store.ActivityAllowed)
            {
                p.Card(c =>
                {
                    c.Text("詳細ログはオフです", fHead, theme.Text, 0, S(4));
                    c.Text("設定の「詳細ログを表示」をオンにすると、Claude Code・Codexのセッションと入力の一覧を表示します。", fSmall, theme.Sub, 0, S(8));
                    c.Button(c.X, c.Y, S(120), "設定を開く", "log-open-settings", () => SelectTab(DashTab.Settings));
                    c.Y += S(26);
                });
                return;
            }

            // 日付
            var dayChips = new List<Tuple<string, bool, string, Action>>();
            for (int i = 0; i < 7; i++)
            {
                var d = today.AddDays(-i);
                string label = i < 2 ? Fmt.DayLabel(d, today) : Fmt.DayShort(d);
                dayChips.Add(Tuple.Create(label, LogDay == d, "day-" + i, (Action)(() => { LogDay = d; scroll[DashTab.Log] = 0; })));
            }
            p.Chips(dayChips);
            p.Space(8);

            // 絞り込み・表示切替・書き出し
            float rowTop = p.Y;
            float x = p.X;
            x += p.Chip(x, rowTop, "すべて", LogFilter == null, "flt-all", () => LogFilter = null) + S(5);
            x += p.Chip(x, rowTop, "Claude Code", LogFilter == Tool.ClaudeCode, "flt-claude", () => LogFilter = Tool.ClaudeCode) + S(5);
            x += p.Chip(x, rowTop, "Codex", LogFilter == Tool.Codex, "flt-codex", () => LogFilter = Tool.Codex) + S(5);
            if (store.ExportAllowed) p.Button(p.X + p.W - S(78), rowTop - S(1), S(78), "書き出す…", "log-export", ShowExportMenu);
            p.Y = rowTop + S(24) + S(6);
            float x2 = p.X;
            x2 += p.Chip(x2, p.Y, "セッション別", !LogByPrompt, "view-session", () => LogByPrompt = false) + S(5);
            p.Chip(x2, p.Y, "入力の一覧（誰が・何を）", LogByPrompt, "view-prompt", () => LogByPrompt = true);
            p.Y += S(24) + S(10);

            if (store.Scanning)
            {
                string pct = store.ScanTotal > 0 ? ((int)(100.0 * store.ScanDone / store.ScanTotal)).ToString() : "0";
                p.Text("ローカル記録を集計中… " + pct + "％（表示は途中の値です）", fSmall, theme.Warn, 0, S(8));
            }

            var views = store.Logs.SessionsForDay(LogDay).Where(v => LogFilter == null || v.Info.Tool == LogFilter.Value).ToList();

            // その日のまとめ
            p.Card(c =>
            {
                c.Text(LogDay.ToString("yyyy年M月d日（ddd）", new System.Globalization.CultureInfo("ja-JP")) + "のまとめ", fHead, theme.Text, 1, S(6));
                bool any = false;
                foreach (Tool t in new[] { Tool.ClaudeCode, Tool.Codex })
                {
                    if (LogFilter != null && LogFilter.Value != t) continue;
                    var sum = store.Logs.DayTotal(t, LogDay);
                    int sessions = views.Count(v => v.Info.Tool == t);
                    int prompts = views.Where(v => v.Info.Tool == t).Sum(v => v.PromptsInDay);
                    float top = c.Y;
                    float bw;
                    c.Badge(c.X, top, Activity.ToolName(t), t == Tool.Codex ? theme.CodexTint : theme.ClaudeTint, out bw);
                    string head = sum.Responses == 0 && sessions == 0 ? "記録なし" : "合計 " + Fmt.Tokens(sum.Total) + "　セッション " + sessions + "件・入力 " + prompts + "件";
                    c.TextAt(head, fBold, theme.Text, c.X + bw + S(8), top, c.W - bw - S(8), 1);
                    c.Y = top + S(20);
                    if (sum.Responses > 0) c.Text(Activity.BreakdownText(t, sum), fSmall, theme.Sub, 0, S(6));
                    else c.Space(4);
                    any = true;
                }
                if (!any) c.Space(2);
                var ca = activity.ClaudeAccount;
                c.Text("入力者（このPCのユーザー）：" + Activity.LocalActor, fSmall, theme.Sub, 0, S(1));
                c.Text("Claude：" + (ca == null ? "未取得" : (ca.Email ?? ca.DisplayName ?? "—")) + "　Codex：" + (activity.CodexAccountLabel ?? "未取得"), fSmall, theme.Sub, 0, 0);
            });

            if (views.Count == 0)
            {
                p.Space(12);
                p.Text(store.Logs.LastScanUtc == null && store.Scanning ? "集計中です…" : "この日付のデータはありません", fBody, theme.Sub, 0, S(4));
                p.Text("このPCに保存された Claude Code（~/.claude/projects）と Codex（~/.codex/sessions）の記録を表示します。ChatGPT・Claudeの通常チャットは端末に履歴が残らないため対象外です。", fSmall, theme.Faint);
                return;
            }

            if (LogByPrompt) DrawPromptList(p, views);
            else foreach (var v in views) DrawSessionCard(p, v);

            p.Space(4);
            p.Text("アカウントは現在ログイン中の情報です（Claude Codeのリモート接続で別アカウントが記録されている場合はそれを表示）。依頼文はログから表示時に読み取り、集計キャッシュには保存しません。", fSmall, theme.Faint);
        }

        void DrawSessionCard(Painter p, LocalLogs.SessionView v)
        {
            var s = v.Info;
            string key = "sess:" + s.Key;
            bool open = expanded.Contains(key);
            Color tint = s.Tool == Tool.Codex ? theme.CodexTint : theme.ClaudeTint;
            p.Card(c =>
            {
                float top = c.Y;
                float bw;
                c.Badge(c.X, top, Activity.ToolName(s.Tool), tint, out bw);
                c.TextAt(Activity.TimeRange(v), fSmall, theme.Sub, c.X + bw + S(8), top + S(2), S(110), 1);
                string tot = Fmt.Tokens(v.Tokens.Total) + (v.Estimated ? "（推定）" : "");
                float tw = c.TextWidth(tot, fBold);
                c.TextAt(tot, fBold, theme.Text, c.X + c.W - tw, top, tw + S(4), 1);
                c.Y = top + S(22);

                c.Text(activity.Title(s), fBold, theme.Text, open ? 0 : 2, S(3));
                string meta = Activity.FolderName(s.Cwd) + "　·　" + Activity.ViaLabel(s, null) + (s.Model != null ? "　·　" + s.Model : "");
                c.Text(meta, fSmall, theme.Sub, open ? 0 : 1, S(2));
                c.Text(Activity.BreakdownText(s.Tool, v.Tokens), fSmall, theme.Faint, open ? 0 : 1, S(4));

                string foot = "入力 " + v.PromptsInDay + "件" + (v.Subagents.Count > 0 ? "・サブエージェント " + v.Subagents.Count + "件" : "") + "　" + (open ? "閉じる ▲" : "詳細 ▼");
                c.Text(foot, fSmall, theme.Link, 1, 0);

                if (!open) return;
                c.Space(8);
                c.Line(c.X, c.Y, c.X + c.W, c.Y, theme.Border);
                c.Space(8);
                KeyValue(c, "入力者", Activity.LocalActor);
                KeyValue(c, "アカウント", activity.AccountLabel(s));
                KeyValue(c, "入力の種類", Activity.InputKindLabel(s, null));
                float fy = c.Y;
                c.TextAt("作業フォルダ", fSmall, theme.Sub, c.X, fy, S(76), 1);
                if (!string.IsNullOrEmpty(s.Cwd) && !Hardening.IsUncPath(s.Cwd) && Directory.Exists(s.Cwd))
                {
                    string cwd = s.Cwd;
                    float h = c.TextAt(cwd, fSmall, theme.Link, c.X + S(80), fy, c.W - S(80), 0);
                    c.AddHit(new RectangleF(c.X + S(80), fy, c.W - S(80), h), "open-cwd-" + s.Key, () => Hardening.OpenFolder(cwd, null));
                    c.Y = fy + h + S(3);
                }
                else KeyValue(c, null, s.Cwd ?? "不明", fy);
                KeyValue(c, "期間", Fmt.DateTimeShort(Fmt.FromUnixMs(s.Start).ToLocalTime()) + " 〜 " + Fmt.DateTimeShort(Fmt.FromUnixMs(s.End).ToLocalTime()));
                if (v.Subagents.Count > 0)
                    KeyValue(c, "サブエージェント", string.Join("、", v.Subagents.Select(x => x.Nickname ?? Activity.FolderName(x.Id)).Distinct().Take(12)));
                c.Space(6);

                var prompts = store.Logs.PromptsForDay(s, LogDay);
                if (prompts.Count == 0)
                {
                    var first = store.Logs.FirstPrompt(s);
                    c.Text("この日の入力はありません（前からの作業の続き・自動実行）", fSmall, theme.Sub, 0, S(4));
                    if (first != null && store.PromptsAllowed)
                    {
                        c.Text("最初の依頼（" + Fmt.DateTimeShort(Fmt.FromUnixMs(first.T).ToLocalTime()) + "）", fSmallBold, theme.Sub, 1, S(2));
                        c.Text(activity.PromptText(s.Tool, first) ?? "（読み取れませんでした）", fSmall, theme.Text, 6, 0);
                    }
                    return;
                }
                c.Text("この日の入力（" + prompts.Count + "件）", fSmallBold, theme.Sub, 1, S(4));
                foreach (var pr in prompts.Take(60))
                {
                    string label = Fmt.FromUnixMs(pr.T).ToLocalTime().ToString("HH:mm") + "　" + Activity.InputKindLabel(s, pr.Source) + "・" + Activity.ViaLabel(s, pr.Source);
                    c.Text(label, fSmallBold, theme.Text, 1, S(1));
                    string text = store.PromptsAllowed ? (activity.PromptText(s.Tool, pr) ?? "（読み取れませんでした）") : (store.Policy.HidePromptText ? "（組織のポリシーで依頼文は非表示）" : "（依頼文の表示はオフ）");
                    c.Text(text, fSmall, theme.Sub, 5, S(7));
                }
                if (prompts.Count > 60) c.Text("ほか " + (prompts.Count - 60) + "件（書き出しで全件を確認できます）", fSmall, theme.Faint);
            }, key, () => { if (!expanded.Remove(key)) expanded.Add(key); }, tint);
        }

        void KeyValue(Painter c, string key, string value, float? y = null)
        {
            float top = y ?? c.Y;
            if (key != null) c.TextAt(key, fSmall, theme.Sub, c.X, top, S(76), 1);
            float h = c.TextAt(value ?? "—", fSmall, theme.Text, c.X + S(80), top, c.W - S(80), 0);
            c.Y = top + Math.Max(h, fSmall.GetHeight(c.G)) + S(3);
        }

        void DrawPromptList(Painter p, List<LocalLogs.SessionView> views)
        {
            var entries = new List<Tuple<PromptEntry, LineRef, SessionInfo>>();
            foreach (var v in views)
                foreach (var pr in store.Logs.PromptsForDay(v.Info, LogDay))
                    entries.Add(Tuple.Create((PromptEntry)null, pr, v.Info));
            entries = entries.OrderByDescending(e => e.Item2.T).ToList();
            if (entries.Count == 0)
            {
                p.Text("この日の入力記録はありません（トークン記録のみ）", fBody, theme.Sub);
                return;
            }
            foreach (var e in entries.Take(300))
            {
                var s = e.Item3;
                var pr = e.Item2;
                string key = "prompt:" + pr.Path + "|" + pr.Offset;
                bool open = expanded.Contains(key);
                Color tint = s.Tool == Tool.Codex ? theme.CodexTint : theme.ClaudeTint;
                p.Card(c =>
                {
                    float top = c.Y;
                    float bw;
                    c.Badge(c.X, top, Activity.ToolName(s.Tool), tint, out bw);
                    string when = Fmt.FromUnixMs(pr.T).ToLocalTime().ToString("HH:mm");
                    c.TextAt(when + "　" + Activity.InputKindLabel(s, pr.Source), fSmallBold, theme.Text, c.X + bw + S(8), top + S(1), c.W - bw - S(8), 1);
                    c.Y = top + S(22);
                    string text = store.PromptsAllowed ? (activity.PromptText(s.Tool, pr) ?? "（読み取れませんでした）") : (store.Policy.HidePromptText ? "（組織のポリシーで依頼文は非表示）" : "（依頼文の表示はオフ）");
                    c.Text(text, fBody, theme.Text, open ? 0 : 3, S(5));
                    c.Text("入力者 " + Activity.LocalActor + "　·　" + activity.AccountLabel(s), fSmall, theme.Sub, open ? 0 : 1, S(1));
                    c.Text(Activity.ViaLabel(s, pr.Source) + "　·　" + Activity.FolderName(s.Cwd) + "　·　" + activity.Title(s), fSmall, theme.Faint, open ? 0 : 1, 0);
                }, key, () => { if (!expanded.Remove(key)) expanded.Add(key); }, tint);
            }
            if (entries.Count > 300) p.Text("ほか " + (entries.Count - 300) + "件（書き出しで全件を確認できます）", fSmall, theme.Faint);
        }

        bool menuOpen;

        void ShowExportMenu()
        {
            var menu = new ContextMenuStrip();
            string dayLabel = LogDay.ToString("M/d");
            menu.Items.Add(dayLabel + " を Markdown で保存…", null, (s, e) => Export(false, false));
            menu.Items.Add("7日間を Markdown で保存…", null, (s, e) => Export(true, false));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(dayLabel + " を CSV で保存…", null, (s, e) => Export(false, true));
            menu.Items.Add("7日間を CSV で保存…", null, (s, e) => Export(true, true));
            menuOpen = true;
            menu.Closed += (s, e) => { menuOpen = false; };
            menu.Show(this, PointToClient(Cursor.Position));
        }

        void Export(bool week, bool csv)
        {
            if (!store.ExportAllowed) return;
            var today = DateTime.Now.Date;
            var days = week ? Enumerable.Range(0, 7).Select(i => today.AddDays(-i)).ToList() : new List<DateTime> { LogDay };
            string name = "AI作業ログ_" + (week ? days.Min().ToString("yyyy-MM-dd") + "〜" + days.Max().ToString("yyyy-MM-dd") : LogDay.ToString("yyyy-MM-dd")) + (csv ? ".csv" : ".md");
            menuOpen = true;
            try
            {
                using (var dlg = new SaveFileDialog())
                {
                    dlg.FileName = name;
                    dlg.Filter = csv ? "CSV (*.csv)|*.csv" : "Markdown (*.md)|*.md";
                    dlg.RestoreDirectory = true;
                    string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                    if (Directory.Exists(downloads)) dlg.InitialDirectory = downloads;
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    string text = csv ? activity.ExportCsv(days, LogFilter, store.PromptsAllowed) : activity.ExportMarkdown(days, LogFilter, store.PromptsAllowed);
                    // CSVはExcelで文字化けしないようBOM付き
                    File.WriteAllText(dlg.FileName, text, new UTF8Encoding(csv));
                    Hardening.OpenFolder(Path.GetDirectoryName(dlg.FileName), dlg.FileName);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "保存できませんでした：" + ex.Message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { menuOpen = false; }
        }

        // ------------------------------------------------------------------ チーム

        void RunDialog(Func<bool> dialog, Action afterOk)
        {
            menuOpen = true;
            try { if (dialog() && afterOk != null) afterOk(); }
            catch (TeamException ex) { TeamUi.Error(this, ex.Message); }
            finally { menuOpen = false; Invalidate(); }
        }

        void AfterTeamChange()
        {
            if (TeamChanged != null) TeamChanged();
            Invalidate();
        }

        void DrawTeam(Painter p)
        {
            if (team == null)
            {
                p.Text("チーム機能を読み込めませんでした。", fBody, theme.Sub);
                return;
            }
            if (!team.Allowed)
            {
                p.Card(c =>
                {
                    c.Text("チーム機能は組織のポリシーで無効です", fHead, theme.Text, 0, S(4));
                    c.Text("管理者の設定（DisableTeamSharing）により、共有フォルダへの報告とマスター機能は使えません。", fSmall, theme.Sub);
                });
                return;
            }
            var local = team.Local;
            if (!local.Joined)
            {
                p.Card(c =>
                {
                    c.Text("チームで利用状況を確認する", fHead, theme.Text, 0, S(6));
                    c.Text("マスター（導入した人）が、メンバーをメールアドレスで登録し、各メンバーのAI利用量・金額（プラン月額とAPI換算の目安）・作業内容・依頼文を表で確認できます。一般ユーザーは自分の分だけを見られます。", fSmall, theme.Sub, 0, S(8));
                    c.Text("データは社内の共有フォルダ（ファイルサーバー・OneDrive / SharePoint の同期フォルダなど）に暗号化して置きます。本アプリはインターネットに接続しません。", fSmall, theme.Sub, 0, S(10));
                    if (local.Corrupt) c.Text(local.LastError, fSmall, theme.Danger, 0, S(10));
                    c.Button(c.X, c.Y, c.W, "チームを作成する（マスターになる）…", "team-create", () => RunDialog(() => TeamUi.CreateTeam(this, team), AfterTeamChange));
                    c.Y += S(26) + S(8);
                    c.Button(c.X, c.Y, c.W, "チームに参加する（一般ユーザー）…", "team-join", () => RunDialog(() => TeamUi.JoinTeam(this, team), AfterTeamChange));
                    c.Y += S(26);
                });
                return;
            }

            var cfg = team.Team;
            bool master = team.IsMaster;
            var me = cfg == null ? null : cfg.MemberFor(team.MyEmails());
            bool listedMaster = me != null && me.IsMaster;
            p.Card(c =>
            {
                float top = c.Y;
                float bw;
                string role = master ? "マスター" : listedMaster ? "マスター（このPCでは未有効）" : "一般ユーザー";
                c.Badge(c.X, top, role, master ? theme.Accent : theme.Sub, out bw);
                c.Y = top + S(24);
                c.Text(cfg == null ? "（チーム設定を読み込めません）" : cfg.TeamName, fHead, theme.Text, 1, S(6));
                if (team.TeamError != null) c.Text(team.TeamError, fSmall, theme.Danger, 0, S(6));
                KeyValue(c, "共有フォルダ", local.SharedFolder);
                if (cfg != null) KeyValue(c, "チーム確認コード", cfg.Fingerprint);
                KeyValue(c, "あなた", (local.DeclaredName ?? "") + "（" + string.Join("、", team.MyEmails()) + "）");
                KeyValue(c, "このPCの確認コード", team.MyPcFingerprint() ?? "—");
                if (cfg != null)
                {
                    KeyValue(c, "マスター", string.Join("、", cfg.Members.Where(m => m.IsMaster).Select(m => m.Name)));
                    var eff = team.EffectiveScope(cfg);
                    KeyValue(c, "共有中の範囲", ScopeText.Label(eff));
                    c.Text(ScopeText.Describe(eff), fSmall, theme.Faint, 0, S(4));
                    KeyValue(c, "同意した範囲", ScopeText.Label(local.ConsentedScope) + (local.ConsentedAtUtc == null ? "" : "（" + Fmt.DateTimeShort(local.ConsentedAtUtc.Value.ToLocalTime()) + "）"));
                }
                KeyValue(c, "最終報告", local.LastUploadUtc == null ? "まだありません" : Fmt.DateTimeShort(local.LastUploadUtc.Value.ToLocalTime()) + "（" + Fmt.Ago(local.LastUploadUtc.Value, DateTime.UtcNow) + "）");
                if (local.LastError != null) c.Text("報告できませんでした：" + local.LastError, fSmall, theme.Warn, 0, S(4));
                if (cfg != null && team.NeedsConsent(cfg))
                {
                    c.Space(4);
                    c.Text("このチームでは、すべて（依頼文の本文を含む）の共有への同意が必要です。同意するまでは、以前に同意した範囲だけを共有します。", fSmall, theme.Warn, 0, S(6));
                    c.Button(c.X, c.Y, c.W, "共有内容を確認して同意する…", "team-consent", () => RunDialog(() => TeamUi.Consent(this, team), AfterTeamChange));
                    c.Y += S(26) + S(4);
                }
                c.Space(8);
                if (master)
                {
                    c.Button(c.X, c.Y, c.W, "チーム管理画面を開く（全メンバーの利用状況）…", "team-manager", () => { if (OpenTeamManager != null) OpenTeamManager(); }, true);
                    c.Y += S(26) + S(8);
                    float mh = (c.W - S(8)) / 2;
                    float mrow = c.Y;
                    c.Button(c.X, mrow, mh, "メンバーを追加（メール）…", "team-add-members", () => RunDialog(() => TeamUi.AddMembers(this, team), AfterTeamChange));
                    c.Button(c.X + mh + S(8), mrow, mh, "招待を送る（メール）…", "team-invite", () => RunDialog(() => { TeamUi.ShowInvite(this, team); return false; }, AfterTeamChange));
                    c.Y = mrow + S(26) + S(8);
                }
                else if (listedMaster)
                {
                    c.Button(c.X, c.Y, c.W, "マスター権限を有効にする（パスフレーズ）…", "team-unlock", () => RunDialog(() =>
                    {
                        string pass = TeamUi.AskPassphrase(this, "マスター権限を有効にする", "チームのパスフレーズを入力してください。マスターの権限は、パスフレーズを知っていることで確認します（チーム設定でマスターに指定されていることも必要です）。鍵は、このPCの本人のWindowsアカウントでだけ開けるように保護して保存します。", false);
                        if (pass == null) return false;
                        Cursor = Cursors.WaitCursor;
                        try { team.UnlockMaster(pass); } finally { Cursor = Cursors.Default; }
                        TeamUi.Info(this, "マスター権限を有効にしました。");
                        return true;
                    }, AfterTeamChange));
                    c.Y += S(26) + S(8);
                }
                float half = (c.W - S(8)) / 2;
                float row = c.Y;
                c.Button(c.X, row, half, "今すぐ報告", "team-upload", () =>
                {
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try { team.Upload(true); }
                        catch (Exception ex) { Hardening.AppendErrorLog(store.Paths.DataDir, ex.ToString()); }
                        try { BeginInvoke((Action)Invalidate); } catch { }
                    });
                });
                c.Button(c.X + half + S(8), row, half, "共有される内容…", "team-consent2", () => RunDialog(() => TeamUi.Consent(this, team), AfterTeamChange));
                c.Y = row + S(26) + S(8);
                c.Button(c.X, c.Y, c.W, "チームから抜ける…", "team-leave", () =>
                {
                    menuOpen = true;
                    var ans = MessageBox.Show(this, "チームから抜けますか？\n共有フォルダにあるこのPCの報告を削除し、以後は報告しません。", AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    menuOpen = false;
                    if (ans != DialogResult.Yes) return;
                    team.Leave();
                    AfterTeamChange();
                });
                c.Y += S(26);
            });

            p.Card(c =>
            {
                c.Text("見える範囲", fBold, theme.Text, 0, S(6));
                if (master)
                    c.Text("マスターは、チーム管理画面で全メンバーの利用量・金額と、各メンバーの閲覧範囲（本人が同意した範囲まで）の作業内容・依頼文を確認できます。メンバーの追加、権限（マスター／一般ユーザー）と閲覧範囲の変更、PCの承認もそこで行います。", fSmall, theme.Sub);
                else
                    c.Text("一般ユーザーは、このアプリで自分の利用状況だけを見られます。他のメンバーの報告は暗号化されていて読めません。あなたの報告は、上の「共有中の範囲」だけがマスターに共有されます。", fSmall, theme.Sub);
            });
        }

        // ------------------------------------------------------------------ 設定

        void DrawSettings(Painter p)
        {
            var st = store.Settings;
            p.Card(c =>
            {
                c.Text("Codexの更新間隔", fBold, theme.Text, 0, S(6));
                c.Chips(Settings.IntervalChoices.Select(m => Tuple.Create(m + "分", st.CodexIntervalMinutes == m, "int-" + m, (Action)(() => { st.CodexIntervalMinutes = m; Changed(); }))).ToList());
                c.Space(6);
                c.Text("Codexの確認は、生成リクエストを送らずに利用状況だけを取得します。", fSmall, theme.Sub, 0, S(12));
                c.Text("Claude・作業ログの確認間隔", fBold, theme.Text, 0, S(6));
                c.Chips(Settings.ClaudeIntervalChoices.Select(m => Tuple.Create(m + "分", st.ClaudeIntervalMinutes == m, "cint-" + m, (Action)(() => { st.ClaudeIntervalMinutes = m; Changed(); }))).ToList());
                c.Space(6);
                c.Text("Claudeの利用枠（Claude Desktopの保存履歴）と、Claude Code・Codexの記録を確認する間隔です。PC内のファイルを読むだけで通信はしません。Claudeの利用枠はClaude Desktopが約15分ごとに記録するため、間隔を短くしてもそれより新しくはなりません。間隔を長くした場合は、古い情報（灰色）とみなすまでの時間も延ばします。", fSmall, theme.Sub);
            });

            p.Card(c =>
            {
                c.Toggle("残り利用枠が少ないときに通知", "Windowsの通知で知らせます", st.NotificationsEnabled, "tgl-notify", () => { st.NotificationsEnabled = !st.NotificationsEnabled; Changed(); });
                c.Space(10);
                c.Text("通知する残量", fBold, st.NotificationsEnabled ? theme.Text : theme.Faint, 0, S(6));
                c.Chips(Settings.ThresholdChoices.Select(v => Tuple.Create(v + "%以下", st.NotificationThreshold == v, "thr-" + v, (Action)(() => { st.NotificationThreshold = v; Changed(); }))).ToList());
                c.Space(12);
                c.Text("枠がリセットされたら通知", fBold, theme.Text, 0, S(6));
                c.Chips(Enumerable.Range(0, ResetWatcher.ModeNames.Length).Select(i => Tuple.Create(ResetWatcher.ModeNames[i], st.ResetNotify == i, "rst-" + i, (Action)(() => { st.ResetNotify = i; Changed(); }))).ToList());
                c.Space(6);
                c.Text("Codexはリセット時刻を過ぎたとき、Claudeは残りが大きく戻ったとき（保存履歴は約15分ごと）に知らせます。「残りが少なかったときだけ」は、リセット前の残りが上の「通知する残量」以下だった場合です。まったく使っていない枠のリセットは知らせません。", fSmall, theme.Sub);
                c.Space(12);
                c.Toggle("タスクバーに表示", "ウィンドウとしてタスクバーに並べます（右クリックでピン留めできます）。オフにするとタスクトレイから開くポップアップになります", st.ShowInTaskbar, "tgl-taskbar", () => { st.ShowInTaskbar = !st.ShowInTaskbar; Changed(); });
                c.Space(10);
                c.Toggle("起動時はタスクバーに収納", "起動したときは画面を開かず、タスクバーに最小化しておきます", st.StartMinimized, "tgl-startmin", () => { st.StartMinimized = !st.StartMinimized; Changed(); });
                c.Space(12);
                bool startup = StartupRegistration.IsEnabled();
                c.Toggle("ログイン時に起動", "Windowsにサインインしたときに自動で起動します", startup, "tgl-startup", () =>
                {
                    string exe = Hardening.CurrentExePath;
                    if (!startup && Hardening.IsRiskyLocation(exe))
                    {
                        menuOpen = true;
                        MessageBox.Show(this, "ダウンロードやデスクトップなど、他のファイルが置かれやすい場所から実行しているため、ログイン時起動は登録できません。\n\n設定の「推奨の場所へ移動」で移してから登録してください。", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                        menuOpen = false;
                        return;
                    }
                    try { StartupRegistration.Set(!startup, exe); }
                    catch (Exception ex) { MessageBox.Show(this, "ログイン項目を変更できませんでした：" + ex.Message, AppInfo.Name); }
                }, store.Policy.DisableStartupRegistration ? "組織のポリシーで管理されています（ログイン時起動は禁止）" : null);
            });

            p.Card(c =>
            {
                c.Toggle("詳細ログを表示", "Claude Code・Codexのセッション、入力者、依頼文の一覧を表示します", store.ActivityAllowed, "tgl-activity", () => { st.ActivityEnabled = !st.ActivityEnabled; Changed(); }, store.Policy.DisableActivityLog ? "組織のポリシーで管理されています（詳細ログは無効）" : null);
                c.Space(10);
                c.Toggle("依頼文を表示", "オフにすると、一覧と書き出しに依頼文を含めません", store.PromptsAllowed, "tgl-prompts", () => { st.ShowPrompts = !st.ShowPrompts; Changed(); }, store.Policy.HidePromptText || store.Policy.DisableActivityLog ? "組織のポリシーで管理されています（依頼文は非表示）" : null);
            });

            p.Card(c =>
            {
                c.Text("このPCで検出したもの", fBold, theme.Text, 0, S(6));
                foreach (var d in store.Detect())
                {
                    float top = c.Y;
                    string mark = d.Found ? "✓" : "—";
                    c.TextAt(mark, fBold, d.Found ? theme.Good : theme.Faint, c.X, top, S(16), 1);
                    c.TextAt(d.Name, fBody, theme.Text, c.X + S(20), top, S(120), 1);
                    c.TextAt(d.Found ? "検出" : "未検出", fSmall, d.Found ? theme.Sub : theme.Faint, c.X + S(140), top + S(1), c.W - S(140), 1);
                    c.Y = top + fBody.GetHeight(c.G) + S(4);
                }
                c.Space(2);
                c.Text("検出はインストールの確認です。ログイン状態とは異なります。", fSmall, theme.Faint);
            });

            p.Card(c =>
            {
                c.Text("データ", fBold, theme.Text, 0, S(6));
                c.Text("集計キャッシュに保存するのは、トークン使用量・取得時刻・セッションID・作業フォルダのパス・ログ内の位置・依頼文のHMAC（重複判定用）です。会話本文・認証情報・メールアドレスは保存しません。保存先は本人・SYSTEM・Administratorsだけが読めます。", fSmall, theme.Sub, 0, S(8));
                c.Button(c.X, c.Y, S(200), "保存先をエクスプローラーで開く", "btn-open-data", () =>
                {
                    Hardening.SecureDirectory(store.Paths.DataDir);
                    Hardening.OpenFolder(store.Paths.DataDir, null);
                });
                c.Y += S(26) + S(6);
                c.Text(store.Paths.DataDir, fSmall, theme.Faint);
            });

            p.Card(c =>
            {
                c.Text("セキュリティ・導入", fBold, theme.Text, 0, S(6));
                c.Text("読み書きする場所、通信、起動する子プロセス、exeのハッシュ、組織ポリシーの状態を、この端末の実際の値で書き出します（会話本文・メールアドレスは含みません）。", fSmall, theme.Sub, 0, S(8));
                c.Button(c.X, c.Y, S(230), "仕様・セキュリティ情報を書き出す…", "btn-secinfo", ExportSecurityInfo);
                c.Y += S(26) + S(10);
                if (store.Policy.AnyEnforced)
                    c.Text("※組織のポリシーが適用されています：" + string.Join("、", store.Policy.ToDictionary().Where(kv => kv.Value).Select(kv => kv.Key)), fSmall, theme.Warn, 0, S(8));
                string exe = Hardening.CurrentExePath;
                if (Hardening.IsRiskyLocation(exe))
                {
                    c.Text("このexeはダウンロードなど他のファイルが置かれやすい場所にあります。常用する場合は推奨の場所（" + Hardening.RecommendedInstallDir + "）へ移してください。", fSmall, theme.Warn, 0, S(8));
                    c.Button(c.X, c.Y, S(170), "推奨の場所へ移動…", "btn-relocate", () => { if (RelocateRequested != null) RelocateRequested(); });
                    c.Y += S(26);
                }
                else c.Text("実行場所：" + exe, fSmall, theme.Faint);
            });
        }

        void ExportSecurityInfo()
        {
            menuOpen = true;
            try
            {
                using (var dlg = new SaveFileDialog())
                {
                    dlg.FileName = "AI-Usage-Bar_仕様・セキュリティ情報_" + Environment.MachineName + "_" + DateTime.Now.ToString("yyyy-MM-dd") + ".md";
                    dlg.Filter = "Markdown (*.md)|*.md";
                    dlg.RestoreDirectory = true;
                    string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                    if (Directory.Exists(downloads)) dlg.InitialDirectory = downloads;
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    File.WriteAllText(dlg.FileName, SecurityInfo.Build(store, team), new UTF8Encoding(false));
                    Hardening.OpenFolder(Path.GetDirectoryName(dlg.FileName), dlg.FileName);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "保存できませんでした：" + ex.Message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { menuOpen = false; }
        }

        void Changed()
        {
            store.Settings.Save(store.Paths.DataDir);
            if (SettingsChanged != null) SettingsChanged();
        }

        // ------------------------------------------------------------------ 外部

        static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }


        protected override void Dispose(bool disposing)
        {
            if (disposing) { DisposeFonts(); if (taskbarIcon != null) taskbarIcon.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
