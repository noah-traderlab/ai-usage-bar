using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AIUsageBar
{
    /// <summary>
    /// チームの報告から、上層部向けの集計（メンバー別のAI活用状況・日別合計）と「気づき」を作る共通処理。
    /// 管理画面のダッシュボードと、定期レポートの自動生成の両方から使う。
    /// </summary>
    public static class TeamAnalytics
    {
        public class Result
        {
            public string TeamName;
            public List<ExecRow> Rows = new List<ExecRow>();
            public List<DailyPoint> Daily = new List<DailyPoint>();
            public int TeamSize;
            public bool FeesReadable;
            public double UsdJpy = 150;
        }

        /// <summary>承認済みの報告を、メンバー単位に集計する。inPeriod は "yyyy-MM-dd" を受け取り期間内かを返す。</summary>
        public static Result Build(TeamConfig team, IEnumerable<TeamReport> reports, Func<string, bool> inPeriod)
        {
            var res = new Result { TeamName = team.TeamName, FeesReadable = team.PrivateOpened, UsdJpy = team.UsdJpy };
            var usable = reports.Where(r => r.Error == null && r.Approved).ToList();

            // メンバー（または未登録グループ）ごとに報告をまとめる
            var groups = new Dictionary<string, List<TeamReport>>();
            var groupMember = new Dictionary<string, MemberEntry>();
            foreach (var r in usable)
            {
                bool ambiguous;
                var m = team.ResolveMember(r.DetectedEmails, r.DeclaredEmail, out ambiguous);
                if (ambiguous) continue;
                string key = m != null ? "m:" + m.Id : "u:" + (r.AllEmails.FirstOrDefault() ?? r.InstallId);
                List<TeamReport> g;
                if (!groups.TryGetValue(key, out g)) { groups[key] = g = new List<TeamReport>(); groupMember[key] = m; }
                g.Add(r);
            }

            var dailyMap = new SortedDictionary<string, double[]>(StringComparer.Ordinal);   // date -> [claudeTok, codexTok, jpy]
            foreach (var kv in groups)
            {
                var m = groupMember[kv.Key];
                var reps = kv.Value;
                var days = reps.SelectMany(x => x.Daily).Where(d => inPeriod(d.Date)).ToList();
                long claude = days.Where(d => d.Tool == Tool.ClaudeCode).Sum(d => d.Sum.Total);
                long codexLocal = days.Where(d => d.Tool == Tool.Codex).Sum(d => d.Sum.Total);
                var svcDaily = ServiceDaily(reps, inPeriod);
                long codexService = svcDaily.Values.Sum();
                bool useService = codexLocal == 0 && codexService > 0;
                var cost = CostCalc.Sum(days, team);
                var activeDates = new HashSet<string>(days.Select(d => d.Date));
                foreach (var d in svcDaily) activeDates.Add(d.Key);
                int prompts = 0, sessions = 0;
                foreach (var rep in reps)
                {
                    foreach (var p in rep.Prompts)
                    {
                        long t = Json.Long(p, "t") ?? 0;
                        if (!Fmt.ValidUnixMs(t)) continue;
                        string date = Fmt.FromUnixMs(t).ToLocalTime().ToString("yyyy-MM-dd");
                        if (inPeriod(date)) { prompts++; activeDates.Add(date); }
                    }
                    foreach (var s in rep.Sessions)
                    {
                        long st = Json.Long(s, "start") ?? 0, en = Json.Long(s, "end") ?? 0;
                        string sd = Fmt.ValidUnixMs(st) ? Fmt.FromUnixMs(st).ToLocalTime().ToString("yyyy-MM-dd") : null;
                        string ed = Fmt.ValidUnixMs(en) ? Fmt.FromUnixMs(en).ToLocalTime().ToString("yyyy-MM-dd") : null;
                        if ((sd != null && inPeriod(sd)) || (ed != null && inPeriod(ed))) sessions++;
                    }
                }
                string name = m != null ? m.Name : (reps.Select(x => x.DeclaredName).FirstOrDefault(x => !string.IsNullOrEmpty(x)) ?? "（未登録）");
                res.Rows.Add(new ExecRow
                {
                    Name = name,
                    IsMember = m != null,
                    Claude = claude,
                    Codex = useService ? codexService : codexLocal,
                    Output = days.Sum(d => d.Sum.Output),
                    Jpy = cost.Jpy,
                    Unpriced = cost.UnpricedTokens > 0,
                    PlanJpy = m != null && team.PrivateOpened ? m.MonthlyFeeJpy : 0,
                    ActiveDays = activeDates.Count,
                    Prompts = prompts,
                    Sessions = sessions,
                    Models = days.Select(d => d.Model).Where(x => !string.IsNullOrEmpty(x)).Distinct().Count(),
                    UsesClaude = claude > 0,
                    UsesCodex = (useService ? codexService : codexLocal) > 0,
                    LastActive = activeDates.Count == 0 ? null : activeDates.Max(),
                });
                foreach (var d in days)
                {
                    double[] a;
                    if (!dailyMap.TryGetValue(d.Date, out a)) { a = new double[3]; dailyMap[d.Date] = a; }
                    if (d.Tool == Tool.ClaudeCode) a[0] += d.Sum.Total; else a[1] += d.Sum.Total;
                    var usd = CostCalc.Usd(d, team);
                    if (usd != null) a[2] += usd.Value * team.UsdJpy;
                }
                if (useService)
                    foreach (var d in svcDaily)
                    {
                        double[] a;
                        if (!dailyMap.TryGetValue(d.Key, out a)) { a = new double[3]; dailyMap[d.Key] = a; }
                        a[1] += d.Value;
                    }
            }
            res.Daily = dailyMap.Select(kv => new DailyPoint { Date = kv.Key, Claude = (long)kv.Value[0], Codex = (long)kv.Value[1], Jpy = kv.Value[2] }).ToList();
            int unregistered = res.Rows.Count(r => !r.IsMember);
            res.TeamSize = Math.Max(team.Members.Count(x => !x.IsMaster) + unregistered, res.Rows.Count);
            return res;
        }

        static Dictionary<string, long> ServiceDaily(List<TeamReport> reps, Func<string, bool> inPeriod)
        {
            var svc = new Dictionary<string, long>();
            foreach (var rep in reps)
                foreach (var x in rep.CodexServiceDaily.Where(x => x.Date != null && inPeriod(x.Date)))
                {
                    long cur;
                    svc.TryGetValue(x.Date, out cur);
                    svc[x.Date] = Math.Max(cur, x.Tokens);   // 同じアカウントを複数PCで使う場合も重複して数えない
                }
            return svc;
        }

        // ------------------------------------------------------------------ 顧客別スタートライン（基準値）

        /// <summary>
        /// 顧客（チーム）ごとの「スタートライン」＝AI導入段階に応じた基準値。
        /// マスターの手元だけに保存し（`baseline.json`）、共有・署名対象には含めない。既定オフ。
        /// ダッシュボードは、この基準に対する相対（目標達成度）を表示する。
        /// </summary>
        public class TeamBaseline
        {
            public bool Enabled;
            public int StageIndex = 2;          // 0:導入前 1:試験導入 2:一部活用 3:全社活用
            public int TargetScore = 50;        // 目標とする平均AI力スコア（0-100）
            public int TargetAdoptionPct = 70;  // 目標とする活用率（メンバーのうちAIを使う割合 %）

            public static readonly string[] StageNames = { "導入前（これから）", "試験導入", "一部活用", "全社活用" };
            static readonly int[] StageScore = { 20, 35, 50, 65 };
            static readonly int[] StageAdopt = { 30, 50, 70, 90 };

            /// <summary>段階を選ぶと、その段階の目安値を目標に入れる（あとで個別に調整可）。</summary>
            public void ApplyStage(int i)
            {
                StageIndex = Math.Max(0, Math.Min(StageNames.Length - 1, i));
                TargetScore = StageScore[StageIndex];
                TargetAdoptionPct = StageAdopt[StageIndex];
            }

            static string Path(AppPaths paths) { return System.IO.Path.Combine(paths.DataDir, "baseline.json"); }

            public static TeamBaseline Load(AppPaths paths)
            {
                var s = new TeamBaseline();
                try
                {
                    string p = Path(paths);
                    if (!System.IO.File.Exists(p)) return s;
                    var o = Json.Parse(FileUtil.ReadShared(p));
                    s.Enabled = Json.Bool(o, "enabled") ?? false;
                    s.StageIndex = (int)Math.Max(0, Math.Min(StageNames.Length - 1, Json.Long(o, "stageIndex") ?? 2));
                    s.TargetScore = (int)Math.Max(0, Math.Min(100, Json.Long(o, "targetScore") ?? 50));
                    s.TargetAdoptionPct = (int)Math.Max(0, Math.Min(100, Json.Long(o, "targetAdoptionPct") ?? 70));
                }
                catch { }
                return s;
            }

            public void Save(AppPaths paths)
            {
                Hardening.SecureDirectory(paths.DataDir);
                FileUtil.WriteAtomic(Path(paths), Json.Serialize(new Dictionary<string, object>
                {
                    { "enabled", Enabled }, { "stageIndex", (long)StageIndex },
                    { "targetScore", (long)TargetScore }, { "targetAdoptionPct", (long)TargetAdoptionPct },
                }, true));
            }

            public string StageName { get { return StageNames[Math.Max(0, Math.Min(StageNames.Length - 1, StageIndex))]; } }
            public string Describe()
            {
                if (!Enabled) return "未設定";
                return "スタートライン：" + StageName + "（目標：平均スコア " + TargetScore + "・活用率 " + TargetAdoptionPct + "%）";
            }
        }

        // ------------------------------------------------------------------ 気づき

        public struct Insight
        {
            public int Severity;   // 0:見出し 1:良い 2:注意(低) 3:未活用 4:提案(中) 5:補足
            public string Text;
            public Insight(int sev, string text) { Severity = sev; Text = text; }
        }

        public static List<Insight> Insights(List<ExecRow> rows, int teamSize) { return Insights(rows, teamSize, null); }

        /// <summary>取得済みデータからの「気づき・次の一手」（表示にも、AI提案の材料にも使う）。baseline があれば目標との相対も加える。</summary>
        public static List<Insight> Insights(List<ExecRow> rows, int teamSize, TeamBaseline baseline)
        {
            var list = new List<Insight>();
            int active = rows.Count(r => r.Tokens > 0);
            double avg = rows.Count == 0 ? 0 : rows.Average(r => r.Score);
            if (baseline != null && baseline.Enabled)
            {
                int adopt = teamSize > 0 ? (int)Math.Round(active * 100.0 / teamSize) : 0;
                int avgI = (int)Math.Round(avg);
                bool scoreOk = avgI >= baseline.TargetScore, adoptOk = adopt >= baseline.TargetAdoptionPct;
                list.Add(new Insight(0, baseline.Describe() + "　→　現状 平均" + avgI + "点・活用率" + adopt + "%。"));
                if (scoreOk && adoptOk)
                    list.Add(new Insight(1, "スタートラインの目標を達成。次の段階（" + NextStage(baseline) + "）へ、活用の幅（用途・部署）を広げましょう。"));
                else
                {
                    var gaps = new List<string>();
                    if (!scoreOk) gaps.Add("平均スコアがあと " + (baseline.TargetScore - avgI) + " 点");
                    if (!adoptOk) gaps.Add("活用率があと " + (baseline.TargetAdoptionPct - adopt) + " ポイント");
                    list.Add(new Insight(4, "目標まで：" + string.Join("、", gaps) + "。使いこなし層からの横展開と、未活用者への着手支援で近づけます。"));
                }
            }
            long totalTokens = rows.Sum(r => r.Tokens);
            double totalJpy = rows.Sum(r => r.Jpy);
            int totalPrompts = rows.Sum(r => r.Prompts);
            bool hasPrompts = rows.Any(r => r.Prompts > 0);
            var used = rows.Where(r => r.Tokens > 0 || r.Prompts > 0).ToList();
            var masters = rows.Where(r => r.Score >= 70).OrderByDescending(r => r.Score).ToList();
            var unused = rows.Where(r => r.IsMember && r.Score < 20).ToList();

            list.Add(new Insight(0, teamSize > 0
                ? "今期は " + teamSize + "人中 " + active + "人（" + Pct(active, teamSize) + "）がAIを利用。平均AI力は " + (int)Math.Round(avg) + "点。"
                : "利用データを集計しました。"));
            if (masters.Count > 0)
                list.Add(new Insight(1, "使いこなし層：" + Join(masters.Take(3).Select(r => r.Name)) + "。AI特務・勉強会の講師役に任命し、他メンバーへ横展開を。"));
            if (unused.Count > 0)
                list.Add(new Insight(3, "未活用：" + unused.Count + "人（" + Join(unused.Take(3).Select(r => r.Name)) + (unused.Count > 3 ? " ほか" : "") + "）。ハンズオンや業務テーマの割り当てで着手を後押し。"));
            if (used.Count >= 3)
            {
                var topR = used.OrderByDescending(r => r.Tokens).First();
                if (totalTokens > 0 && topR.Tokens > totalTokens * 0.5)
                    list.Add(new Insight(2, "活用が " + topR.Name + " に集中（全体の" + Pct(topR.Tokens, totalTokens) + "）。属人化を避け、ノウハウの共有を。"));
            }
            var oneTool = used.Where(r => r.UsesClaude ^ r.UsesCodex).Select(r => r.Name).Take(3).ToList();
            if (oneTool.Count > 0)
                list.Add(new Insight(4, "片方のツールのみ：" + Join(oneTool) + "。もう一方（Codex／Claude）も試すと用途が広がります。"));
            if (!hasPrompts && used.Count > 0)
                list.Add(new Insight(4, "「何をしているか」を見るには、メンバーの閲覧範囲を『作業の概要／依頼文まで』にし、本人の同意を得てください。"));
            if (totalJpy > 0 && totalPrompts > 0)
                list.Add(new Insight(5, "1依頼あたり約 ¥" + Math.Round(totalJpy / Math.Max(1, totalPrompts)).ToString("#,0") + "（API換算の目安）。費用対効果の目安として。"));
            if (list.Count == 1)
                list.Add(new Insight(5, "まずは1ヶ月の利用感を観察し、部門長へ展開していく段階です。"));
            return list;
        }

        static string Pct(double a, double b) { return b <= 0 ? "0%" : Math.Round(a / b * 100).ToString("0") + "%"; }
        static string Join(IEnumerable<string> names) { return string.Join("、", names); }
        static string NextStage(TeamBaseline b) { return b.StageIndex + 1 < TeamBaseline.StageNames.Length ? TeamBaseline.StageNames[b.StageIndex + 1] : "全社定着"; }

        // ------------------------------------------------------------------ 期間

        /// <summary>期間の選択肢（管理画面と定期レポートで共通）。indexは管理画面のコンボと同じ順。</summary>
        public static Func<string, bool> InPeriod(int index)
        {
            var today = DateTime.Now.Date;
            return date =>
            {
                DateTime d;
                if (!DateTime.TryParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out d)) return false;
                switch (index)
                {
                    case 0: return d == today;
                    case 1: return d > today.AddDays(-7) && d <= today;
                    case 2: return d > today.AddDays(-30) && d <= today;
                    case 3: return d.Year == today.Year && d.Month == today.Month;
                    default: var last = today.AddMonths(-1); return d.Year == last.Year && d.Month == last.Month;
                }
            };
        }

        public static readonly string[] PeriodNames = { "今日", "直近7日", "直近30日", "今月", "先月" };
    }
}
