using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace AIUsageBar
{
    /// <summary>ダッシュボード1行分（メンバーのAI活用状況）</summary>
    public class ExecRow
    {
        public string Name;
        public bool IsMember;
        public long Claude;          // Claude Code トークン
        public long Codex;           // Codex トークン（ローカル記録が無い場合はサービス集計）
        public long Output;          // 出力トークン（AIが生成した量の目安）
        public double Jpy;           // API換算（円）
        public bool Unpriced;
        public long PlanJpy;         // 契約プラン月額
        public int ActiveDays;       // 稼働日数
        public int Prompts;          // 依頼数（閲覧範囲が概要以上のとき）
        public int Sessions;         // セッション数
        public int Models;           // 使ったモデル数
        public bool UsesClaude, UsesCodex;
        public string LastActive;    // 最終稼働日

        public long Tokens { get { return Claude + Codex; } }

        /// <summary>AI活用度スコア（0-100）。データ量・頻度・幅からの目安で、作業の質は測らない。</summary>
        public int Score
        {
            get
            {
                double days = Math.Min(ActiveDays, 20) / 20.0 * 35;
                double prm = Math.Min(Prompts, 100) / 100.0 * 25;
                double tok = Math.Min(Math.Log10(1 + Tokens) / 7.0, 1.0) * 25;
                double tools = ((UsesClaude ? 1 : 0) + (UsesCodex ? 1 : 0)) / 2.0 * 8;
                double models = Math.Min(Models, 3) / 3.0 * 7;
                // 依頼数が取れない（利用量のみ）メンバーは、その配点をトークン量で補う
                if (Prompts == 0 && Tokens > 0) { tok = Math.Min(Math.Log10(1 + Tokens) / 7.0, 1.0) * 50; prm = 0; }
                return (int)Math.Round(Math.Max(0, Math.Min(100, days + prm + tok + tools + models)));
            }
        }

        public string Level
        {
            get { int s = Score; return s >= 70 ? "AIマスター" : s >= 45 ? "活用中" : s >= 20 ? "発展途上" : "未活用"; }
        }
    }

    public class DailyPoint
    {
        public string Date;
        public long Claude, Codex;
        public double Jpy;
        public long Tokens { get { return Claude + Codex; } }
    }

    /// <summary>
    /// 上層部・部門長向けの「AI活用ダッシュボード」。
    /// 何に使っているか（活動）、誰が使いこなしているか（AI力）、どう浸透させるか（サマリー・次の一手）、費用（目安）を1画面に。
    /// 外部ライブラリを使わず GDI+ で描画する。AI活用度スコアは「量・頻度・幅」の目安で、作業の質や人事評価そのものではない。
    /// </summary>
    public class TeamDashboardForm : Form
    {
        readonly string teamName, periodLabel;
        readonly List<ExecRow> rows;
        readonly List<DailyPoint> daily;
        readonly double usdJpy;
        readonly bool feesReadable;
        readonly int teamSize;
        readonly AppPaths paths;      // AI提案・スタートラインの設定保存先（null なら未対応）
        readonly bool aiAllowed;
        TeamAnalytics.TeamBaseline baseline;   // 顧客別スタートライン（既定オフ）

        // モダン＆ミニマルの配色（やわらかい寒色ニュートラル＋ブランド2色＋落ち着いたアクセント）
        static readonly Color Teal = Color.FromArgb(15, 157, 130);   // Codex
        static readonly Color Clay = Color.FromArgb(214, 99, 56);    // Claude
        static readonly Color Ink = Color.FromArgb(22, 26, 33);
        static readonly Color Sub = Color.FromArgb(112, 122, 138);
        static readonly Color Line = Color.FromArgb(233, 236, 241);
        static readonly Color Bg = Color.FromArgb(246, 247, 250);
        static readonly Color Panel = Color.White;
        static readonly Color Good = Color.FromArgb(22, 163, 132);
        static readonly Color Mid = Color.FromArgb(79, 99, 214);
        static readonly Color Low = Color.FromArgb(197, 145, 34);
        static readonly Color Bad = Color.FromArgb(202, 94, 74);
        static readonly Color Accent = Color.FromArgb(79, 99, 214);
        static readonly Color Shadow = Color.FromArgb(18, 24, 40);
        static readonly int R = 14;   // 角丸

        readonly Font fTitle, fKpi, fH, fBody, fSmall, fBadge;

        public TeamDashboardForm(string teamName, string periodLabel, List<ExecRow> rows, List<DailyPoint> daily,
            double usdJpy, bool feesReadable, int teamSize, AppPaths paths = null, bool aiAllowed = false)
        {
            this.teamName = teamName;
            this.periodLabel = periodLabel;
            this.rows = rows.OrderByDescending(r => r.Score).ThenByDescending(r => r.Tokens).ToList();
            this.daily = daily.OrderBy(d => d.Date).ToList();
            this.usdJpy = usdJpy;
            this.feesReadable = feesReadable;
            this.teamSize = Math.Max(teamSize, rows.Count);
            this.paths = paths;
            this.aiAllowed = aiAllowed && paths != null;
            this.baseline = paths != null ? TeamAnalytics.TeamBaseline.Load(paths) : new TeamAnalytics.TeamBaseline();

            Text = AppInfo.Name + " — AI活用ダッシュボード（" + teamName + "）";
            Font = TeamUi.UiFont;
            fTitle = new Font(Font.FontFamily, 15f, FontStyle.Bold);
            fKpi = new Font(Font.FontFamily, 19f, FontStyle.Bold);
            fH = new Font(Font.FontFamily, 11.5f, FontStyle.Bold);
            fBody = new Font(Font.FontFamily, 9.5f);
            fSmall = new Font(Font.FontFamily, 8.5f);
            fBadge = new Font(Font.FontFamily, 8.5f, FontStyle.Bold);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1040, 720);
            MinimumSize = new Size(900, 640);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Bg;
            DoubleBuffered = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 46, Padding = new Padding(10), BackColor = Bg };
            var close = new Button { Text = "閉じる", AutoSize = true, DialogResult = DialogResult.OK, MinimumSize = new Size(96, 28) };
            var csv = new Button { Text = "CSVで保存", AutoSize = true, MinimumSize = new Size(110, 28) };
            csv.Click += (s, e) => SaveCsv();
            var ai = new Button { Text = "AIに提案してもらう…", AutoSize = true, MinimumSize = new Size(150, 28), Font = new Font(TeamUi.UiFont, FontStyle.Bold) };
            ai.Click += (s, e) => RunAiSuggest();
            var sched = new Button { Text = "定期レポート…", AutoSize = true, MinimumSize = new Size(110, 28) };
            sched.Click += (s, e) => ScheduleSettings();
            var baseBtn = new Button { Text = "スタートライン…", AutoSize = true, MinimumSize = new Size(120, 28) };
            baseBtn.Click += (s, e) => BaselineSettings();
            bar.Controls.Add(close);
            bar.Controls.Add(csv);
            if (aiAllowed) bar.Controls.Add(ai);
            if (aiAllowed) bar.Controls.Add(sched);
            if (paths != null) bar.Controls.Add(baseBtn);
            Controls.Add(bar);
            AcceptButton = close;
            TeamUi.StyleButton(close, false);
            TeamUi.StyleButton(csv, false);
            TeamUi.StyleButton(ai, true);
            TeamUi.StyleButton(sched, false);
            TeamUi.StyleButton(baseBtn, false);
            Resize += (s, e) => Invalidate();
        }

        int Active { get { return rows.Count(r => r.Tokens > 0); } }
        long TotalTokens { get { return rows.Sum(r => r.Tokens); } }
        long TotalOutput { get { return rows.Sum(r => r.Output); } }
        int TotalPrompts { get { return rows.Sum(r => r.Prompts); } }
        double TotalJpy { get { return rows.Sum(r => r.Jpy); } }
        long TotalPlan { get { return rows.Sum(r => r.PlanJpy); } }
        double AvgScore { get { return rows.Count == 0 ? 0 : rows.Average(r => r.Score); } }
        bool AnyUnpriced { get { return rows.Any(r => r.Unpriced); } }
        bool HasPrompts { get { return rows.Any(r => r.Prompts > 0); } }

        static string Yen(double v) { return "¥" + Math.Round(v).ToString("#,0", CultureInfo.InvariantCulture); }
        static Color LevelColor(int s) { return s >= 70 ? Good : s >= 45 ? Mid : s >= 20 ? Low : Bad; }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            int w = ClientSize.Width, m = 20, y = 18;

            g.DrawString("AI活用ダッシュボード", fTitle, new SolidBrush(Ink), m, y);
            // 右上：チーム名・期間をピルで
            string sub = teamName + "　·　" + periodLabel;
            var ss = g.MeasureString(sub, fBody);
            var pill = new Rectangle((int)(w - m - ss.Width - 24), y + 1, (int)ss.Width + 22, 24);
            FillRound(g, pill, 12, Color.FromArgb(238, 240, 246), Line);
            g.DrawString(sub, fBody, new SolidBrush(Sub), pill.X + 11, pill.Y + 4);
            y += 36;

            // KPI
            bool bl = baseline != null && baseline.Enabled;
            int adoptPct = teamSize > 0 ? (int)Math.Round(Active * 100.0 / teamSize) : 0;
            string adoptSub = bl ? "目標 " + baseline.TargetAdoptionPct + "%（" + (adoptPct >= baseline.TargetAdoptionPct ? "達成" : "あと" + (baseline.TargetAdoptionPct - adoptPct) + "pt") + "）" : Pct(Active, teamSize) + " が今期利用";
            string scoreSub = bl ? "目標 " + baseline.TargetScore + "点／" + baseline.StageName : "0–100（活用の量・頻度・幅）";
            var kpis = new List<string[]>
            {
                new[] { "活用しているメンバー", Active + " / " + teamSize + " 人", adoptSub },
                new[] { "AI力 平均スコア", ((int)Math.Round(AvgScore)).ToString() + " 点", scoreSub },
                new[] { "依頼数（何をしているか）", HasPrompts ? TotalPrompts.ToString("#,0") + " 件" : "— ", HasPrompts ? "直近の作業依頼" : "閲覧範囲の同意が必要" },
                new[] { "AIが生成した量", Fmt.Tokens(TotalOutput), "出力トークン（作業量の目安）" },
                new[] { "API換算コスト（目安）", Yen(TotalJpy) + (AnyUnpriced ? " *" : ""), feesReadable ? "プラン月額 " + Yen(TotalPlan) : "" },
            };
            var kpiAccent = new[] { Accent, Clay, Teal, Mid, Low };
            int gap = 14, tileW = (w - m * 2 - gap * (kpis.Count - 1)) / kpis.Count, tileH = 96;
            for (int i = 0; i < kpis.Count; i++)
            {
                var r = new Rectangle(m + i * (tileW + gap), y, tileW, tileH);
                DrawCard(g, r, R);
                // 左上の細いアクセントバー
                using (var b = new SolidBrush(kpiAccent[i % kpiAccent.Length]))
                using (var p = Round(new Rectangle(r.X + 14, r.Y + 15, 3, 14), 2)) g.FillPath(b, p);
                g.DrawString(kpis[i][0], fSmall, new SolidBrush(Sub), r.X + 23, r.Y + 15);
                g.DrawString(kpis[i][1], fKpi, new SolidBrush(Ink), r.X + 14, r.Y + 36);
                g.DrawString(Ellipsize(g, kpis[i][2], fSmall, tileW - 24), fSmall, new SolidBrush(Sub), r.X + 14, r.Y + 70);
            }
            y += tileH + gap;

            int midH = ClientSize.Height - y - 66 - 156;
            int leftW = (int)((w - m * 2 - gap) * 0.56);
            DrawRanking(g, new Rectangle(m, y, leftW, midH));
            DrawSummary(g, new Rectangle(m + leftW + gap, y, w - m * 2 - gap - leftW, midH));
            y += midH + gap;
            DrawDaily(g, new Rectangle(m, y, w - m * 2, ClientSize.Height - y - 66));

            g.DrawString("※ AI活用度スコアは「量・頻度・幅」の目安で、作業の質・人事評価そのものではありません。API換算は目安で実請求額と異なります。"
                + (AnyUnpriced ? " *一部モデルは単価未設定。" : ""), fSmall, new SolidBrush(Sub), m, ClientSize.Height - 56);
        }

        void DrawRanking(Graphics g, Rectangle rect)
        {
            DrawCard(g, rect, R);
            int pad = 18;
            g.DrawString("AI力ランキング（誰が使いこなしているか）", fH, new SolidBrush(Ink), rect.X + pad, rect.Y + pad);
            int top = rect.Y + pad + 28;
            var list = rows.Where(r => r.Tokens > 0 || r.Prompts > 0).ToList();
            if (list.Count == 0) { g.DrawString("この期間の活用データがありません。", fBody, new SolidBrush(Sub), rect.X + pad, top + 8); return; }
            int rowH = 40;
            int n = Math.Min(list.Count, (rect.Bottom - top - 6) / rowH);
            bool bl = baseline != null && baseline.Enabled;
            int barX = rect.X + (int)(rect.Width * 0.40f), barW = rect.Width - (barX - rect.X) - pad - 46;
            int goalX = barX + (int)(barW * Math.Max(0, Math.Min(100, bl ? baseline.TargetScore : 0)) / 100.0);
            if (bl)   // 目標スコアの位置に縦線（相対表示）
            {
                using (var gp = new Pen(Color.FromArgb(150, 90, 96, 110)) { DashStyle = DashStyle.Dash })
                    g.DrawLine(gp, goalX, top - 2, goalX, top + n * rowH - 6);
                g.DrawString("目標 " + baseline.TargetScore, fSmall, new SolidBrush(Sub), goalX - 14, top - 20);
            }
            for (int i = 0; i < n; i++)
            {
                var r = list[i];
                int ry = top + i * rowH;
                var lc = LevelColor(r.Score);
                // 名前＋レベルバッジ
                g.DrawString(Ellipsize(g, r.Name, fBody, rect.Width * 0.32f), fBody, new SolidBrush(Ink), rect.X + pad, ry + 2);
                DrawBadge(g, rect.X + pad, ry + 20, r.Level, lc);
                // スコアバー
                var track = new Rectangle(barX, ry + 6, barW, 12);
                FillRound(g, track, 6, Color.FromArgb(238, 240, 245), Color.FromArgb(233, 236, 241));
                var fill = new Rectangle(barX, ry + 6, Math.Max(6, (int)(barW * r.Score / 100.0)), 12);
                FillRoundGrad(g, fill, 6, Lighten(lc, 0.18), lc);
                if (bl)   // 目標到達を✓で示す
                    using (var gp = new Pen(Color.FromArgb(150, 90, 96, 110)) { DashStyle = DashStyle.Dash })
                        g.DrawLine(gp, goalX, ry + 4, goalX, ry + 20);
                g.DrawString(r.Score.ToString() + (bl && r.Score >= baseline.TargetScore ? " ✓" : ""), fBadge, new SolidBrush(bl && r.Score >= baseline.TargetScore ? Good : Ink), track.Right + 8, ry + 4);
                // 指標
                string metrics = "稼働" + r.ActiveDays + "日" + (r.Prompts > 0 ? "・依頼" + r.Prompts + "件" : "") + "・" + Fmt.Tokens(r.Tokens)
                    + (r.Jpy > 0 ? "・" + Yen(r.Jpy) : "") + "・" + Tools(r);
                g.DrawString(Ellipsize(g, metrics, fSmall, barW + 40), fSmall, new SolidBrush(Sub), barX, ry + 22);
                if (i < n - 1) using (var lp = new Pen(Line)) g.DrawLine(lp, rect.X + pad, ry + rowH - 2, rect.Right - pad, ry + rowH - 2);
            }
            if (list.Count > n) g.DrawString("ほか " + (list.Count - n) + " 人（CSVで全件）", fSmall, new SolidBrush(Sub), rect.X + pad, rect.Bottom - 18);
        }

        static string Tools(ExecRow r)
        {
            if (r.UsesClaude && r.UsesCodex) return "Claude+Codex";
            if (r.UsesClaude) return "Claudeのみ";
            if (r.UsesCodex) return "Codexのみ";
            return "—";
        }

        void DrawSummary(Graphics g, Rectangle rect)
        {
            DrawCard(g, rect, R);
            int pad = 18;
            g.DrawString("サマリー・次の一手", fH, new SolidBrush(Accent), rect.X + pad, rect.Y + pad);
            int y = rect.Y + pad + 28;
            foreach (var line in BuildInsights())
            {
                if (y + 18 > rect.Bottom - pad) break;
                // 行頭のマーク
                using (var b = new SolidBrush(line.Item2)) g.FillEllipse(b, rect.X + pad, y + 5, 7, 7);
                foreach (var seg in WrapText(g, line.Item1, fBody, rect.Width - pad * 2 - 14))
                {
                    if (y + 18 > rect.Bottom - pad) break;
                    g.DrawString(seg, fBody, new SolidBrush(Ink), rect.X + pad + 14, y);
                    y += 18;
                }
                y += 6;
            }
        }

        /// <summary>取得済みデータから、上層部・部門長への「気づき」と「次の一手」（共通ロジックを使い、色を付ける）。</summary>
        List<Tuple<string, Color>> BuildInsights()
        {
            return TeamAnalytics.Insights(rows, teamSize, baseline).Select(i => Tuple.Create(i.Text, SeverityColor(i.Severity))).ToList();
        }

        static Color SeverityColor(int sev)
        {
            switch (sev)
            {
                case 1: return Good;
                case 2: return Low;
                case 3: return Bad;
                case 4: return Mid;
                case 5: return Sub;
                default: return Accent;
            }
        }

        void DrawDaily(Graphics g, Rectangle rect)
        {
            DrawCard(g, rect, R);
            int pad = 18;
            g.DrawString("日別トークン（Claude Code ＋ Codex）", fH, new SolidBrush(Ink), rect.X + pad, rect.Y + pad);
            int legX = rect.Right - pad - 210;
            g.FillRectangle(new SolidBrush(Clay), legX, rect.Y + pad + 2, 12, 12);
            g.DrawString("Claude Code", fSmall, new SolidBrush(Sub), legX + 16, rect.Y + pad);
            g.FillRectangle(new SolidBrush(Teal), legX + 110, rect.Y + pad + 2, 12, 12);
            g.DrawString("Codex", fSmall, new SolidBrush(Sub), legX + 126, rect.Y + pad);

            var pts = daily.Where(d => d.Tokens > 0).ToList();
            if (pts.Count > 31) pts = pts.Skip(pts.Count - 31).ToList();
            if (pts.Count == 0) { g.DrawString("この期間のトークンがありません。", fBody, new SolidBrush(Sub), rect.X + pad, rect.Y + 40); return; }
            long max = pts.Max(d => d.Tokens);
            var plot = new Rectangle(rect.X + pad + 44, rect.Y + pad + 26, rect.Width - pad * 2 - 44, rect.Height - pad * 2 - 26 - 20);
            using (var lp = new Pen(Line))
                for (int k = 0; k <= 2; k++)
                {
                    int gy = plot.Bottom - (int)(plot.Height * k / 2.0);
                    g.DrawLine(lp, plot.X, gy, plot.Right, gy);
                    string lbl = Fmt.Tokens((long)(max * k / 2.0));
                    var lsz = g.MeasureString(lbl, fSmall);
                    g.DrawString(lbl, fSmall, new SolidBrush(Sub), plot.X - lsz.Width - 6, gy - lsz.Height / 2);
                }
            float slot = (float)plot.Width / pts.Count, bw = Math.Min(slot * 0.58f, 30f);
            int every = (int)Math.Ceiling(pts.Count / 14.0);
            for (int i = 0; i < pts.Count; i++)
            {
                var d = pts[i];
                float cx = plot.X + slot * i + slot / 2, x = cx - bw / 2;
                float hc = (float)(plot.Height * (d.Claude / (double)max));
                float hx = (float)(plot.Height * (d.Codex / (double)max));
                bool topIsCodex = hx > 0.5f;
                // Claude（下）
                if (hc > 0.5f) FillBarSeg(g, x, plot.Bottom - hc, bw, hc, Lighten(Clay, 0.12), Clay, !topIsCodex);
                // Codex（上・積み上げ）
                if (topIsCodex) FillBarSeg(g, x, plot.Bottom - hc - hx, bw, hx, Lighten(Teal, 0.12), Teal, true);
                if (i % every == 0)
                {
                    string dl = d.Date.Length >= 10 ? d.Date.Substring(5) : d.Date;
                    var dsz = g.MeasureString(dl, fSmall);
                    g.DrawString(dl, fSmall, new SolidBrush(Sub), cx - dsz.Width / 2, plot.Bottom + 3);
                }
            }
        }

        // ------------------------------------------------------------------ 補助

        void DrawBadge(Graphics g, int x, int y, string text, Color c)
        {
            var sz = g.MeasureString(text, fBadge);
            var r = new Rectangle(x, y, (int)sz.Width + 14, 16);
            using (var b = new SolidBrush(Color.FromArgb(30, c))) using (var p = Round(r, 8)) g.FillPath(b, p);
            g.DrawString(text, fBadge, new SolidBrush(c), x + 7, y + 1);
        }

        static string Pct(double a, double b) { return b <= 0 ? "0%" : Math.Round(a / b * 100).ToString("0") + "%"; }
        static string Join(IEnumerable<string> names) { return string.Join("、", names); }
        static Color Lighten(Color c, double t) { return Color.FromArgb(c.A, (int)(c.R + (255 - c.R) * t), (int)(c.G + (255 - c.G) * t), (int)(c.B + (255 - c.B) * t)); }

        /// <summary>棒グラフの1区間（縦グラデーション、上端だけ角丸オプション）。</summary>
        static void FillBarSeg(Graphics g, float x, float y, float w, float h, Color c1, Color c2, bool roundTop)
        {
            if (h < 0.5f) return;
            var rf = new RectangleF(x, y, w, h);
            using (var path = new GraphicsPath())
            {
                float rad = roundTop ? Math.Min(6f, w / 2f) : 0f, d = rad * 2;
                if (rad > 0)
                {
                    path.AddArc(rf.X, rf.Y, d, d, 180, 90);
                    path.AddArc(rf.Right - d, rf.Y, d, d, 270, 90);
                    path.AddLine(rf.Right, rf.Y + rad, rf.Right, rf.Bottom);
                    path.AddLine(rf.Right, rf.Bottom, rf.X, rf.Bottom);
                    path.CloseFigure();
                }
                else path.AddRectangle(rf);
                using (var b = new LinearGradientBrush(new RectangleF(rf.X, rf.Y, rf.Width, Math.Max(1f, rf.Height)), c1, c2, LinearGradientMode.Vertical))
                    g.FillPath(b, path);
            }
        }

        static IEnumerable<string> WrapText(Graphics g, string s, Font f, float maxW)
        {
            var words = s.Replace("。", "。​").Split('​');
            var cur = "";
            foreach (var word in words)
            {
                if (cur.Length > 0 && g.MeasureString(cur + word, f).Width > maxW) { yield return cur; cur = ""; }
                cur += word;
                while (g.MeasureString(cur, f).Width > maxW && cur.Length > 1)
                {
                    int cut = cur.Length;
                    while (cut > 1 && g.MeasureString(cur.Substring(0, cut), f).Width > maxW) cut--;
                    yield return cur.Substring(0, cut);
                    cur = cur.Substring(cut);
                }
            }
            if (cur.Length > 0) yield return cur;
        }

        static void FillRound(Graphics g, Rectangle r, int radius, Color fill, Color border)
        {
            using (var path = Round(r, radius))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                using (var p = new Pen(border)) g.DrawPath(p, path);
            }
        }

        /// <summary>やわらかい影付きのカード（モダン＆ミニマル）。</summary>
        static void DrawCard(Graphics g, Rectangle r, int radius = 14)
        {
            for (int i = 5; i >= 1; i--)   // 多層の薄い影で自然なぼかしに
            {
                var sr = new Rectangle(r.X - i, r.Y + i + 1, r.Width + i * 2, r.Height + i);
                using (var path = Round(sr, radius + i))
                using (var b = new SolidBrush(Color.FromArgb(6, Shadow)))
                    g.FillPath(b, path);
            }
            using (var path = Round(r, radius))
            {
                using (var b = new SolidBrush(Panel)) g.FillPath(b, path);
                using (var p = new Pen(Line)) g.DrawPath(p, path);
            }
        }

        /// <summary>角丸の塗り（グラデーション対応）。</summary>
        static void FillRoundGrad(Graphics g, Rectangle r, int radius, Color c1, Color c2)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using (var path = Round(r, radius))
            using (var b = new LinearGradientBrush(new Rectangle(r.X, r.Y, r.Width, r.Height), c1, c2, LinearGradientMode.Vertical))
                g.FillPath(b, path);
        }

        static GraphicsPath Round(Rectangle r, int radius)
        {
            int d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        static string Ellipsize(Graphics g, string s, Font f, float maxWidth)
        {
            if (string.IsNullOrEmpty(s) || g.MeasureString(s, f).Width <= maxWidth) return s ?? "";
            while (s.Length > 1 && g.MeasureString(s + "…", f).Width > maxWidth) s = s.Substring(0, s.Length - 1);
            return s + "…";
        }

        // ------------------------------------------------------------------ AI提案

        void RunAiSuggest()
        {
            if (!aiAllowed || paths == null) { MessageBox.Show(this, "この機能は使えません（組織のポリシー等）。", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            var cfg = AiConfig.Load(paths);
            if (!cfg.IsComplete && !AiSettings(cfg)) return;

            string summary = AiClient.BuildSummary(teamName, periodLabel, rows, daily, teamSize, BuildInsights().Select(t => t.Item1));

            using (var dlg = new Form
            {
                Text = "AIに提案してもらう", Font = TeamUi.UiFont, StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.Sizable, MinimizeBox = false, MaximizeBox = true,
                ClientSize = new Size(660, 560), ShowInTaskbar = false,
            })
            {
                try { dlg.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
                var top = new Label { Dock = DockStyle.Top, Height = 66, Padding = new Padding(12, 10, 12, 6), ForeColor = Sub,
                    Text = "モデル：" + cfg.Model + "\n下のサマリー（数値と傾向のみ・依頼文の本文は含みません）を Anthropic API に送信して、提案文を作ります。API の利用料はあなたのキーに課金されます。" };
                var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
                var sumBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Text = summary, BackColor = SystemColors.Window, Font = new Font(FontFamily.GenericMonospace, 9f) };
                var outBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Text = "「送信して提案を得る」を押してください。", BackColor = SystemColors.Window };
                split.Panel1.Controls.Add(sumBox);
                split.Panel1.Controls.Add(new Label { Dock = DockStyle.Top, Text = "送信するサマリー", Height = 18, ForeColor = Sub });
                split.Panel2.Controls.Add(outBox);
                split.Panel2.Controls.Add(new Label { Dock = DockStyle.Top, Text = "AIの提案", Height = 18, ForeColor = Sub });

                var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 46, Padding = new Padding(10) };
                var close = new Button { Text = "閉じる", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(96, 28) };
                var copy = new Button { Text = "提案をコピー", AutoSize = true, MinimumSize = new Size(110, 28), Enabled = false };
                var send = new Button { Text = "送信して提案を得る", AutoSize = true, MinimumSize = new Size(150, 28), Font = new Font(TeamUi.UiFont, FontStyle.Bold) };
                var gear = new Button { Text = "設定…", AutoSize = true, MinimumSize = new Size(72, 28) };
                copy.Click += (s, e) => { try { Clipboard.SetText(outBox.Text); copy.Text = "コピーしました"; } catch { } };
                gear.Click += (s, e) => { var c2 = AiConfig.Load(paths); if (AiSettings(c2)) top.Text = "モデル：" + c2.Model + top.Text.Substring(top.Text.IndexOf('\n')); };
                send.Click += (s, e) =>
                {
                    send.Enabled = false; gear.Enabled = false; outBox.Text = "送信しています…（数十秒かかることがあります）"; dlg.Cursor = Cursors.WaitCursor;
                    var cfgNow = AiConfig.Load(paths);
                    System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                    {
                        string result, error = null;
                        try { result = AiClient.Suggest(cfgNow, summary); }
                        catch (AiException ex) { result = null; error = ex.Message; }
                        catch (Exception ex) { result = null; error = "予期しないエラー：" + ex.Message; }
                        try
                        {
                            dlg.BeginInvoke((Action)(() =>
                            {
                                dlg.Cursor = Cursors.Default; send.Enabled = true; gear.Enabled = true;
                                if (error != null) { outBox.Text = "提案を取得できませんでした。\r\n\r\n" + error; }
                                else { outBox.Text = result; copy.Enabled = true; }
                            }));
                        }
                        catch { }
                    });
                };
                bar.Controls.Add(close); bar.Controls.Add(copy); bar.Controls.Add(send); bar.Controls.Add(gear);
                dlg.Controls.Add(split);
                dlg.Controls.Add(bar);
                dlg.Controls.Add(top);
                dlg.AcceptButton = send;
                dlg.CancelButton = close;
                dlg.Shown += (s, e) => { try { split.SplitterDistance = 210; } catch { } TeamUi.Modernize(dlg); };
                dlg.ShowDialog(this);
            }
        }

        /// <summary>顧客別スタートライン（基準値）の設定。保存したら画面を更新。</summary>
        void BaselineSettings()
        {
            if (paths == null) { MessageBox.Show(this, "この機能は使えません。", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            using (var f = BuildBaselineForm(paths))
                if (f.ShowDialog(this) == DialogResult.OK)
                {
                    baseline = TeamAnalytics.TeamBaseline.Load(paths);
                    Invalidate();
                }
        }

        /// <summary>スタートライン設定ダイアログを組み立てて返す（描画テストからも使う）。</summary>
        public static Form BuildBaselineForm(AppPaths paths)
        {
            var b = TeamAnalytics.TeamBaseline.Load(paths);
            var f = new Form
            {
                Text = "スタートライン（顧客の基準値）", Font = TeamUi.UiFont, StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
                ClientSize = new Size(560, 330), ShowInTaskbar = false,
            };
            try { f.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            var note = new Label { Dock = DockStyle.Top, Height = 92, Padding = new Padding(14, 12, 14, 4), ForeColor = Sub,
                Text = "顧客のAI導入状況に合わせて『スタートライン（基準値）』を決めます。ダッシュボードは、この目標に対する"
                + "達成度（活用率・平均AI力）を相対表示し、次の一手もこの目標を踏まえて提案します。段階を選ぶと目安値が入り、"
                + "必要なら個別に調整できます。設定はこのPC（マスター）だけに保存し、メンバーには共有しません。" };
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(14, 6, 14, 6) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var enabled = new CheckBox { Text = "スタートラインを使う（目標を表示する）", Checked = b.Enabled, AutoSize = true };
            grid.Controls.Add(enabled, 0, 0); grid.SetColumnSpan(enabled, 2);
            grid.Controls.Add(new Label { Text = "導入段階", Anchor = AnchorStyles.Left, AutoSize = true, Margin = new Padding(3, 7, 10, 3) }, 0, 1);
            var stage = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
            foreach (var n in TeamAnalytics.TeamBaseline.StageNames) stage.Items.Add(n);
            stage.SelectedIndex = b.StageIndex;
            grid.Controls.Add(stage, 1, 1);
            grid.Controls.Add(new Label { Text = "目標 平均AI力（0–100）", Anchor = AnchorStyles.Left, AutoSize = true, Margin = new Padding(3, 7, 10, 3) }, 0, 2);
            var score = new NumericUpDown { Minimum = 0, Maximum = 100, Value = b.TargetScore, Width = 72, Anchor = AnchorStyles.Left };
            grid.Controls.Add(score, 1, 2);
            grid.Controls.Add(new Label { Text = "目標 活用率（%）", Anchor = AnchorStyles.Left, AutoSize = true, Margin = new Padding(3, 7, 10, 3) }, 0, 3);
            var adopt = new NumericUpDown { Minimum = 0, Maximum = 100, Value = b.TargetAdoptionPct, Width = 72, Anchor = AnchorStyles.Left };
            grid.Controls.Add(adopt, 1, 3);
            stage.SelectedIndexChanged += (s, e) =>
            {
                var tmp = new TeamAnalytics.TeamBaseline(); tmp.ApplyStage(stage.SelectedIndex);
                score.Value = tmp.TargetScore; adopt.Value = tmp.TargetAdoptionPct;
            };

            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 46, Padding = new Padding(10) };
            var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(96, 28) };
            var ok = new Button { Text = "保存", AutoSize = true, MinimumSize = new Size(96, 28) };
            ok.Click += (s, e) =>
            {
                b.Enabled = enabled.Checked;
                b.StageIndex = stage.SelectedIndex;
                b.TargetScore = (int)score.Value;
                b.TargetAdoptionPct = (int)adopt.Value;
                b.Save(paths);
                f.DialogResult = DialogResult.OK; f.Close();
            };
            bar.Controls.Add(cancel); bar.Controls.Add(ok);
            f.Controls.Add(grid); f.Controls.Add(bar); f.Controls.Add(note);
            f.AcceptButton = ok; f.CancelButton = cancel;
            f.Shown += (s, e) => TeamUi.Modernize(f);
            return f;
        }

        /// <summary>定期レポートの設定（自動でAI提案を生成・保存・通知）。</summary>
        void ScheduleSettings()
        {
            if (!aiAllowed || paths == null) { MessageBox.Show(this, "この機能は使えません。", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            using (var f = BuildScheduleForm(paths)) f.ShowDialog(this);
        }

        /// <summary>定期レポート設定ダイアログを組み立てて返す（描画テストからも使う）。</summary>
        public static Form BuildScheduleForm(AppPaths paths)
        {
            var sc = AiSchedule.Load(paths);
            {
                var f = new Form
                {
                    Text = "定期レポートの設定", Font = TeamUi.UiFont, StartPosition = FormStartPosition.CenterParent,
                    FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
                    ClientSize = new Size(540, 320), ShowInTaskbar = false,
                };
                try { f.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
                var note = new Label { Dock = DockStyle.Top, Height = 84, Padding = new Padding(14, 12, 14, 4), ForeColor = Sub,
                    Text = "設定した頻度で、このダッシュボードの集計から自動でAI提案を生成し、テキストで保存してタスクトレイに通知します。"
                    + "生成にはAI提案と同じAPIキーを使い（費用が発生）、集計サマリーのみを送信します。"
                    + "このPC（マスター）でアプリが起動していて、指定時刻を過ぎた最初のタイミングに実行されます。" };
                var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(14, 6, 14, 6) };
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                var enabled = new CheckBox { Text = "定期レポートを有効にする", Checked = sc.Enabled, AutoSize = true };
                grid.Controls.Add(enabled, 0, 0); grid.SetColumnSpan(enabled, 2);
                grid.Controls.Add(new Label { Text = "頻度", Anchor = AnchorStyles.Left, AutoSize = true, Margin = new Padding(3, 7, 10, 3) }, 0, 1);
                var freq = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
                foreach (var n in AiSchedule.FreqNames) freq.Items.Add(n);
                freq.SelectedIndex = sc.Frequency;
                grid.Controls.Add(freq, 1, 1);
                var weekLabel = new Label { Text = "曜日", Anchor = AnchorStyles.Left, AutoSize = true, Margin = new Padding(3, 7, 10, 3) };
                grid.Controls.Add(weekLabel, 0, 2);
                var week = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
                foreach (var n in AiSchedule.WeekdayNames) week.Items.Add(n + "曜");
                week.SelectedIndex = sc.Weekday;
                grid.Controls.Add(week, 1, 2);
                var domLabel = new Label { Text = "日（毎月）", Anchor = AnchorStyles.Left, AutoSize = true, Margin = new Padding(3, 7, 10, 3) };
                grid.Controls.Add(domLabel, 0, 3);
                var dom = new NumericUpDown { Minimum = 1, Maximum = 28, Value = sc.DayOfMonth, Width = 70, Anchor = AnchorStyles.Left };
                grid.Controls.Add(dom, 1, 3);
                grid.Controls.Add(new Label { Text = "時刻", Anchor = AnchorStyles.Left, AutoSize = true, Margin = new Padding(3, 7, 10, 3) }, 0, 4);
                var timeRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0), FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
                var hour = new NumericUpDown { Minimum = 0, Maximum = 23, Value = sc.Hour, Width = 56, TextAlign = HorizontalAlignment.Center };
                var minute = new NumericUpDown { Minimum = 0, Maximum = 59, Value = sc.Minute, Width = 56, TextAlign = HorizontalAlignment.Center };
                timeRow.Controls.Add(hour);
                timeRow.Controls.Add(new Label { Text = "時", AutoSize = true, Margin = new Padding(2, 7, 8, 3) });
                timeRow.Controls.Add(minute);
                timeRow.Controls.Add(new Label { Text = "分", AutoSize = true, Margin = new Padding(2, 7, 3, 3) });
                grid.Controls.Add(timeRow, 1, 4);
                grid.Controls.Add(new Label { Text = "対象期間", Anchor = AnchorStyles.Left, AutoSize = true, Margin = new Padding(3, 7, 10, 3) }, 0, 5);
                var period = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
                foreach (var n in TeamAnalytics.PeriodNames) period.Items.Add(n);
                period.SelectedIndex = sc.PeriodIndex;
                grid.Controls.Add(period, 1, 5);
                Action sync = () => { weekLabel.Visible = week.Visible = freq.SelectedIndex == 1; domLabel.Visible = dom.Visible = freq.SelectedIndex == 2; };
                freq.SelectedIndexChanged += (s, e) => sync();
                sync();

                var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 46, Padding = new Padding(10) };
                var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(96, 28) };
                var ok = new Button { Text = "保存", AutoSize = true, MinimumSize = new Size(96, 28) };
                var openDir = new Button { Text = "保存先を開く", AutoSize = true, MinimumSize = new Size(110, 28) };
                openDir.Click += (s, e) => { string d = System.IO.Path.Combine(paths.DataDir, "ai-reports"); try { System.IO.Directory.CreateDirectory(d); } catch { } Hardening.OpenFolder(d, null); };
                ok.Click += (s, e) =>
                {
                    sc.Enabled = enabled.Checked;
                    sc.Frequency = freq.SelectedIndex;
                    sc.Weekday = week.SelectedIndex;
                    sc.DayOfMonth = (int)dom.Value;
                    sc.Hour = (int)hour.Value;
                    sc.Minute = (int)minute.Value;
                    sc.PeriodIndex = period.SelectedIndex;
                    sc.Save(paths);
                    var cfg = AiConfig.Load(paths);
                    if (sc.Enabled && !cfg.IsComplete)
                        MessageBox.Show(f, "定期レポートを有効にしましたが、AIの設定（APIキー）がまだです。「AIに提案してもらう」→「設定…」でキーを登録してください。", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    f.DialogResult = DialogResult.OK; f.Close();
                };
                bar.Controls.Add(cancel); bar.Controls.Add(ok); bar.Controls.Add(openDir);
                f.Controls.Add(grid); f.Controls.Add(bar); f.Controls.Add(note);
                f.AcceptButton = ok; f.CancelButton = cancel;
                f.Shown += (s, e) => TeamUi.Modernize(f);
                return f;
            }
        }

        /// <summary>AI提案の設定（APIキー・モデル）。有効化＝ネット送信の同意を明示。保存したら true</summary>
        bool AiSettings(AiConfig cfg)
        {
            using (var f = new Form
            {
                Text = "AI提案の設定", Font = TeamUi.UiFont, StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
                ClientSize = new Size(560, 340), ShowInTaskbar = false,
            })
            {
                try { f.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
                var note = new Label { Dock = DockStyle.Top, Height = 118, Padding = new Padding(14, 12, 14, 6), ForeColor = Sub,
                    Text = "AI提案は既定でオフの機能です。有効にすると、このダッシュボードの集計サマリー（数値と傾向。依頼文の本文や会話は含みません）を、"
                    + "あなたのAPIキーでAIサービスに送信し、提案文を受け取ります。\n\n"
                    + "・API の利用料はあなたのキーに課金されます。\n"
                    + "・API キーはこのPCの本人だけが読める形（DPAPI）で保存し、外部には送りません。\n"
                    + "・「Anthropic（Claude）」は console.anthropic.com のキー（sk-ant-）。「OpenAI互換」は他社の /chat/completions に対応するサービスで、"
                    + "エンドポイント(Base URL)・モデル名・キーを入力します。" };
                var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(14, 6, 14, 6) };
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                grid.Controls.Add(new Label { Text = "提供元", Anchor = AnchorStyles.Left, AutoSize = true, Margin = new Padding(3, 7, 10, 3) }, 0, 0);
                var provider = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
                provider.Items.Add("Anthropic（Claude）");
                provider.Items.Add("OpenAI互換（他社・自前）");
                provider.SelectedIndex = cfg.IsOpenAi ? 1 : 0;
                grid.Controls.Add(provider, 1, 0);
                var urlLabel = new Label { Text = "エンドポイント", Anchor = AnchorStyles.Left, AutoSize = true, Margin = new Padding(3, 7, 10, 3) };
                grid.Controls.Add(urlLabel, 0, 1);
                var url = new TextBox { Dock = DockStyle.Fill, Text = cfg.BaseUrl };
                grid.Controls.Add(url, 1, 1);
                grid.Controls.Add(new Label { Text = "モデル", Anchor = AnchorStyles.Left, AutoSize = true, Margin = new Padding(3, 7, 10, 3) }, 0, 2);
                var model = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Dock = DockStyle.Fill };   // OpenAI互換ではモデル名を自由入力
                foreach (var mdl in AiConfig.Models) model.Items.Add(mdl);
                model.Text = string.IsNullOrEmpty(cfg.Model) ? AiConfig.Models[0] : cfg.Model;
                grid.Controls.Add(model, 1, 2);
                grid.Controls.Add(new Label { Text = "APIキー", Anchor = AnchorStyles.Left, AutoSize = true, Margin = new Padding(3, 7, 10, 3) }, 0, 3);
                var key = new TextBox { UseSystemPasswordChar = true, Dock = DockStyle.Fill };
                grid.Controls.Add(key, 1, 3);
                grid.Controls.Add(new Label { Text = cfg.HasKey ? "（設定済み。変えるときだけ入力）" : "", ForeColor = Sub, AutoSize = true }, 1, 4);
                Action sync = () =>
                {
                    bool oa = provider.SelectedIndex == 1;
                    urlLabel.Visible = url.Visible = oa;
                    model.DropDownStyle = oa ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList;
                    if (!oa && !AiConfig.Models.Contains(model.Text)) model.Text = AiConfig.Models[0];
                    if (oa && string.IsNullOrWhiteSpace(url.Text)) url.Text = "https://api.openai.com/v1";
                };
                provider.SelectedIndexChanged += (s, e) => sync();
                sync();

                var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 46, Padding = new Padding(10) };
                var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(96, 28) };
                var ok = new Button { Text = "有効にする（保存）", AutoSize = true, MinimumSize = new Size(140, 28) };
                bool saved = false;
                ok.Click += (s, e) =>
                {
                    bool oa = provider.SelectedIndex == 1;
                    if (!cfg.HasKey && string.IsNullOrWhiteSpace(key.Text)) { MessageBox.Show(f, "APIキーを入力してください。", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                    if (string.IsNullOrWhiteSpace(model.Text)) { MessageBox.Show(f, "モデル名を入力してください。", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                    if (oa && !(url.Text.Trim().StartsWith("https://") || url.Text.Trim().StartsWith("http://localhost"))) { MessageBox.Show(f, "エンドポイントは https:// で始まるURLを入力してください。", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                    if (!oa && !string.IsNullOrWhiteSpace(key.Text) && !key.Text.Trim().StartsWith("sk-ant-"))
                        if (MessageBox.Show(f, "Anthropic のキーは通常 sk-ant- で始まります。このまま保存しますか？", AppInfo.Name, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                    cfg.Provider = oa ? "openai" : "anthropic";
                    cfg.Model = model.Text.Trim();
                    cfg.BaseUrl = oa ? url.Text.Trim() : "";
                    if (!string.IsNullOrWhiteSpace(key.Text)) cfg.SetKey(key.Text);
                    cfg.Save(paths);
                    saved = true;
                    f.DialogResult = DialogResult.OK; f.Close();
                };
                bar.Controls.Add(cancel); bar.Controls.Add(ok);
                f.Controls.Add(grid); f.Controls.Add(bar); f.Controls.Add(note);
                f.AcceptButton = ok; f.CancelButton = cancel;
                f.Shown += (s, e) => TeamUi.Modernize(f);
                f.ShowDialog(this);
                return saved && cfg.IsComplete;
            }
        }

        void SaveCsv()
        {
            using (var dlg = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "AI活用_" + DateTime.Now.ToString("yyyy-MM-dd") + ".csv", RestoreDirectory = true })
            {
                string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                if (Directory.Exists(downloads)) dlg.InitialDirectory = downloads;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var sb = new StringBuilder();
                var head = new[] { "名前", "AI力スコア", "レベル", "稼働日数", "依頼数", "セッション", "使用ツール", "モデル数",
                    "Claudeトークン", "Codexトークン", "合計トークン", "出力トークン", "API換算(円・目安)", "プラン月額(円)", "最終稼働" };
                sb.AppendLine(string.Join(",", head.Select(Hardening.CsvCell)));
                foreach (var r in rows)
                    sb.AppendLine(string.Join(",", new[] {
                        r.Name, r.Score.ToString(), r.Level, r.ActiveDays.ToString(), r.Prompts.ToString(), r.Sessions.ToString(),
                        Tools(r), r.Models.ToString(), r.Claude.ToString(), r.Codex.ToString(), r.Tokens.ToString(), r.Output.ToString(),
                        Math.Round(r.Jpy).ToString("0", CultureInfo.InvariantCulture) + (r.Unpriced ? "(一部未設定)" : ""),
                        feesReadable ? r.PlanJpy.ToString() : "", r.LastActive ?? "" }.Select(Hardening.CsvCell)));
                sb.AppendLine();
                sb.AppendLine(Hardening.CsvCell("サマリー・次の一手"));
                foreach (var line in BuildInsights()) sb.AppendLine(Hardening.CsvCell("・" + line.Item1));
                File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
                Hardening.OpenFolder(Path.GetDirectoryName(dlg.FileName), dlg.FileName);
            }
        }
    }
}
