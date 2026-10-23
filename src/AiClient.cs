using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace AIUsageBar
{
    public class AiException : Exception
    {
        public AiException(string message) : base(message) { }
    }

    /// <summary>
    /// AI提案機能の設定。マスターの Anthropic API キーを DPAPI で保護して保存する。
    /// この機能は既定でオフで、有効にしたときだけ Anthropic API に集計サマリーを送信する（依頼文の本文は送らない）。
    /// </summary>
    public class AiConfig
    {
        public string Provider = "anthropic";   // "anthropic" または "openai"（OpenAI互換API：他社もこれで動くことが多い）
        public string Model = "claude-opus-5";
        public string BaseUrl = "";              // OpenAI互換のときのエンドポイント（例：https://api.openai.com/v1）
        string keyProtected;   // DPAPI 保護済み（平文は保持しない）

        public bool IsOpenAi { get { return Provider == "openai"; } }
        public bool HasKey { get { return !string.IsNullOrEmpty(keyProtected); } }
        public bool IsComplete
        {
            get { return HasKey && !string.IsNullOrWhiteSpace(Model) && (!IsOpenAi || !string.IsNullOrWhiteSpace(BaseUrl)); }
        }

        public static readonly string[] Providers = { "anthropic", "openai" };
        public static readonly string[] Models =
        {
            "claude-opus-5", "claude-sonnet-5", "claude-haiku-4-5",
        };

        public void SetKey(string plain)
        {
            keyProtected = string.IsNullOrWhiteSpace(plain) ? null : TeamCrypto.DpapiProtect(plain.Trim(), "ai-api-key");
        }

        public string GetKey()
        {
            if (string.IsNullOrEmpty(keyProtected)) return "";
            try { return TeamCrypto.DpapiUnprotect(keyProtected, "ai-api-key"); }
            catch (TeamException) { throw new AiException("保存したAPIキーを読み取れません（別のWindowsアカウントでは使えません）。設定し直してください。"); }
        }

        static string Path(AppPaths paths) { return System.IO.Path.Combine(paths.DataDir, "ai.json"); }

        public static AiConfig Load(AppPaths paths)
        {
            var c = new AiConfig();
            try
            {
                string p = Path(paths);
                if (!File.Exists(p)) return c;
                var o = Json.Parse(FileUtil.ReadShared(p));
                c.Provider = Json.Str(o, "provider") ?? c.Provider;
                c.Model = Json.Str(o, "model") ?? c.Model;
                c.BaseUrl = Json.Str(o, "baseUrl") ?? c.BaseUrl;
                c.keyProtected = Json.Str(o, "apiKey");
            }
            catch { }
            return c;
        }

        public void Save(AppPaths paths)
        {
            Hardening.SecureDirectory(paths.DataDir);
            FileUtil.WriteAtomic(Path(paths), Json.Serialize(new Dictionary<string, object> { { "provider", Provider }, { "model", Model }, { "baseUrl", BaseUrl }, { "apiKey", keyProtected } }, true));
        }
    }

    /// <summary>
    /// 定期レポートの設定（毎日／毎週／毎月、対象期間、時刻）。既定オフ。`ai-schedule.json`。
    /// </summary>
    public class AiSchedule
    {
        public bool Enabled;
        public int Frequency = 1;      // 0:毎日 1:毎週 2:毎月
        public int PeriodIndex = 1;    // 集計期間（TeamAnalytics のインデックス。既定=直近7日）
        public int Hour = 9;           // 生成する時刻（時）
        public int Minute = 0;         // 生成する時刻（分）
        public int Weekday = 1;        // 毎週の曜日（0=日〜6=土）。既定=月曜
        public int DayOfMonth = 1;     // 毎月の日
        public string LastRunDate;     // 最後に生成した日（yyyy-MM-dd）。同日二重実行の防止

        public static readonly string[] FreqNames = { "毎日", "毎週", "毎月" };
        public static readonly string[] WeekdayNames = { "日", "月", "火", "水", "木", "金", "土" };

        static string Path(AppPaths paths) { return System.IO.Path.Combine(paths.DataDir, "ai-schedule.json"); }

        public static AiSchedule Load(AppPaths paths)
        {
            var s = new AiSchedule();
            try
            {
                string p = Path(paths);
                if (!File.Exists(p)) return s;
                var o = Json.Parse(FileUtil.ReadShared(p));
                s.Enabled = Json.Bool(o, "enabled") ?? false;
                s.Frequency = (int)Math.Max(0, Math.Min(2, Json.Long(o, "frequency") ?? 1));
                s.PeriodIndex = (int)Math.Max(0, Math.Min(4, Json.Long(o, "periodIndex") ?? 1));
                s.Hour = (int)Math.Max(0, Math.Min(23, Json.Long(o, "hour") ?? 9));
                s.Minute = (int)Math.Max(0, Math.Min(59, Json.Long(o, "minute") ?? 0));
                s.Weekday = (int)Math.Max(0, Math.Min(6, Json.Long(o, "weekday") ?? 1));
                s.DayOfMonth = (int)Math.Max(1, Math.Min(28, Json.Long(o, "dayOfMonth") ?? 1));
                s.LastRunDate = Json.Str(o, "lastRunDate");
            }
            catch { }
            return s;
        }

        public void Save(AppPaths paths)
        {
            Hardening.SecureDirectory(paths.DataDir);
            FileUtil.WriteAtomic(Path(paths), Json.Serialize(new Dictionary<string, object>
            {
                { "enabled", Enabled }, { "frequency", (long)Frequency }, { "periodIndex", (long)PeriodIndex },
                { "hour", (long)Hour }, { "minute", (long)Minute }, { "weekday", (long)Weekday }, { "dayOfMonth", (long)DayOfMonth }, { "lastRunDate", LastRunDate },
            }, true));
        }

        /// <summary>今この瞬間に生成すべきか（同じ日には1回だけ）。</summary>
        public bool IsDue(DateTime now)
        {
            if (!Enabled) return false;
            string today = now.ToString("yyyy-MM-dd");
            if (LastRunDate == today) return false;
            if (now.Hour * 60 + now.Minute < Hour * 60 + Minute) return false;
            switch (Frequency)
            {
                case 0: return true;                                   // 毎日
                case 1: return (int)now.DayOfWeek == Weekday;          // 毎週
                default:                                               // 毎月（その月の日数を超える指定は月末に丸める）
                    int day = Math.Min(DayOfMonth, DateTime.DaysInMonth(now.Year, now.Month));
                    return now.Day == day;
            }
        }

        public void MarkRan(AppPaths paths, DateTime now)
        {
            LastRunDate = now.ToString("yyyy-MM-dd");
            Save(paths);
        }

        public string Describe()
        {
            if (!Enabled) return "オフ";
            string when = Frequency == 0 ? "毎日" : Frequency == 1 ? "毎週" + WeekdayNames[Weekday] + "曜" : "毎月" + DayOfMonth + "日";
            return when + " " + Hour.ToString("00") + ":" + Minute.ToString("00") + "ごろ／対象：" + TeamAnalytics.PeriodNames[PeriodIndex];
        }
    }

    /// <summary>
    /// Anthropic Messages API を生の HTTPS で呼ぶ最小クライアント（外部ライブラリを使わない）。
    /// 送るのは集計サマリー（数値と傾向）だけで、依頼文の本文や個人の会話は送らない。
    /// </summary>
    public static class AiClient
    {
        const string Endpoint = "https://api.anthropic.com/v1/messages";
        const string SystemPrompt =
            "あなたは社内のAI活用推進を支援するアナリストです。与えられたチームのAI利用の集計（数値と傾向）だけをもとに、"
            + "日本語で、上層部・部門長がそのまま読める簡潔なレポートを書いてください。構成は「現状サマリー（3〜5行）」と「次の一手（箇条書き4〜6個、"
            + "担当や具体アクションを含める）」。使いこなしている人は横展開の講師役、未活用の人はフォロー、といった提案を心がけます。"
            + "数値は与えられた範囲だけで述べ、憶測で新しい数字を作らないこと。AI活用度スコアは活動量の目安であり、"
            + "人事評価そのものではない点に留意し、断定的な人物評価は避けること。マークダウンの見出しは使わず、プレーンな日本語で。";

        /// <summary>集計サマリーを送って提案文を得る。UIスレッドから呼ばないこと（同期・ブロッキング）。</summary>
        public static string Suggest(AiConfig cfg, string dataSummary)
        {
            if (cfg == null || !cfg.IsComplete) throw new AiException("APIキー等が設定されていません");
            if (cfg.IsOpenAi)
            {
                string url = cfg.BaseUrl.TrimEnd('/');
                if (!url.EndsWith("/chat/completions")) url += "/chat/completions";
                if (!(url.StartsWith("https://") || url.StartsWith("http://localhost"))) throw new AiException("エンドポイントは https:// で始まる必要があります");
                string body = Json.Serialize(new Dictionary<string, object>
                {
                    { "model", cfg.Model }, { "max_tokens", 1500L },
                    { "messages", new List<object> {
                        new Dictionary<string, object> { { "role", "system" }, { "content", SystemPrompt } },
                        new Dictionary<string, object> { { "role", "user" }, { "content", dataSummary } } } },
                });
                return ParseOpenAiText(Post(url, body, "Bearer", cfg.GetKey()));
            }
            else
            {
                string body = Json.Serialize(new Dictionary<string, object>
                {
                    { "model", cfg.Model }, { "max_tokens", 1500L },
                    { "output_config", new Dictionary<string, object> { { "effort", "low" } } },
                    { "system", SystemPrompt },
                    { "messages", new List<object> {
                        new Dictionary<string, object> { { "role", "user" }, { "content", dataSummary } } } },
                });
                return ParseText(Post(Endpoint, body, "x-api-key", cfg.GetKey()));
            }
        }

        static string Post(string url, string body, string authKind, string apiKey)
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072 | (SecurityProtocolType)12288; } catch { }   // TLS 1.2/1.3
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/json";
            if (authKind == "Bearer") req.Headers["Authorization"] = "Bearer " + apiKey;
            else { req.Headers["x-api-key"] = apiKey; req.Headers["anthropic-version"] = "2023-06-01"; }
            req.Accept = "application/json";
            req.Timeout = 90000;
            req.ReadWriteTimeout = 90000;
            req.UserAgent = AppInfo.ClientName + "/" + AppInfo.Version;
            byte[] data = Encoding.UTF8.GetBytes(body);
            req.ContentLength = data.Length;
            try
            {
                using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return r.ReadToEnd();
            }
            catch (WebException ex)
            {
                throw new AiException(FriendlyError(ex));
            }
        }

        /// <summary>OpenAI互換 API の応答から本文（choices[0].message.content）を取り出す。</summary>
        public static string ParseOpenAiText(string json)
        {
            object o;
            try { o = Json.Parse(json); } catch { throw new AiException("AIの応答を解釈できませんでした。"); }
            var err = Json.Obj(o, "error");
            if (err != null) throw new AiException("AIからエラーが返りました：" + (Json.Str(err, "message") ?? "不明"));
            var choices = Json.Arr(o, "choices");
            if (choices == null || choices.Count == 0) throw new AiException("AIから本文が返りませんでした（choices が空）。エンドポイント・モデル名をご確認ください。");
            var msg = Json.Obj(choices[0], "message");
            object contentObj = msg == null ? null : (msg.ContainsKey("content") ? msg["content"] : null);
            string text = contentObj as string;
            if (text == null && contentObj is List<object>)
            {
                var sb = new StringBuilder();
                foreach (var c in (List<object>)contentObj) { string t = Json.Str(c, "text"); if (t != null) sb.Append(t); }
                text = sb.ToString();
            }
            if (string.IsNullOrWhiteSpace(text)) throw new AiException("AIから本文が返りませんでした。");
            return text.Trim();
        }

        static string FriendlyError(WebException ex)
        {
            var resp = ex.Response as HttpWebResponse;
            if (resp != null)
            {
                int code = (int)resp.StatusCode;
                string detail = "";
                try { using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) detail = ParseErrorMessage(r.ReadToEnd()); } catch { }
                if (code == 401 || code == 403) return "APIキーが正しくないか、権限がありません。キーを確認してください。" + Tail(detail);
                if (code == 429) return "APIの利用上限に達しました。時間をおいて再度お試しください。" + Tail(detail);
                if (code == 400) return "リクエストを受け付けてもらえませんでした。" + Tail(detail);
                if (code >= 500) return "Anthropic 側で一時的なエラーが発生しました。時間をおいて再度お試しください。" + Tail(detail);
                return "AIの呼び出しに失敗しました（HTTP " + code + "）。" + Tail(detail);
            }
            if (ex.Status == WebExceptionStatus.NameResolutionFailure || ex.Status == WebExceptionStatus.ConnectFailure)
                return "インターネットに接続できませんでした。ネットワークとプロキシ設定をご確認ください。";
            if (ex.Status == WebExceptionStatus.Timeout) return "AIの応答がタイムアウトしました。時間をおいて再度お試しください。";
            return "AIの呼び出しに失敗しました：" + ex.Message;
        }

        static string Tail(string s) { return string.IsNullOrEmpty(s) ? "" : "\n\n" + Hardening.SafeText(s, 400); }

        /// <summary>応答 JSON から本文（text ブロック）を取り出す。拒否・エラーは分かりやすい例外にする。</summary>
        public static string ParseText(string json)
        {
            object o;
            try { o = Json.Parse(json); } catch { throw new AiException("AIの応答を解釈できませんでした。"); }
            if (Json.Str(o, "type") == "error") throw new AiException("AIからエラーが返りました：" + (Json.Str(Json.Obj(o, "error"), "message") ?? "不明"));
            string stop = Json.Str(o, "stop_reason");
            var content = Json.Arr(o, "content");
            var sb = new StringBuilder();
            if (content != null)
                foreach (var c in content)
                    if (Json.Str(c, "type") == "text")
                    {
                        if (sb.Length > 0) sb.Append("\n");
                        sb.Append(Json.Str(c, "text"));
                    }
            if (stop == "refusal")
                throw new AiException("この内容はAIが回答を控えました。送信するサマリーを見直してください。");
            string text = sb.ToString().Trim();
            if (text.Length == 0) throw new AiException("AIから本文が返りませんでした。");
            return text;
        }

        static string ParseErrorMessage(string json)
        {
            try { return Json.Str(Json.Obj(Json.Parse(json), "error"), "message") ?? ""; }
            catch { return ""; }
        }

        /// <summary>
        /// 定期レポート：マスターとして報告を読み、集計→AI提案を生成し、テキストファイルに保存してパスを返す。
        /// バックグラウンドスレッドから呼ぶ（同期・ブロッキング）。
        /// </summary>
        public static string GenerateReport(TeamService svc, AiConfig cfg, int periodIndex)
        {
            if (svc == null || !svc.IsMaster) throw new AiException("マスターだけがレポートを生成できます");
            if (cfg == null || !cfg.IsComplete) throw new AiException("AIの設定が未完了です");
            var team = svc.LoadTeam();
            var reports = svc.ReadReports();
            var res = TeamAnalytics.Build(team, reports, TeamAnalytics.InPeriod(periodIndex));
            var baseline = TeamAnalytics.TeamBaseline.Load(svc.Store.Paths);
            var insights = TeamAnalytics.Insights(res.Rows, res.TeamSize, baseline).Select(i => i.Text).ToList();
            string period = TeamAnalytics.PeriodNames[periodIndex];
            string summary = BuildSummary(res.TeamName, period, res.Rows, res.Daily, res.TeamSize, insights, baseline);
            string proposal = Suggest(cfg, summary);

            var sb = new StringBuilder();
            sb.AppendLine("AI活用レポート（自動生成）");
            sb.AppendLine("チーム：" + res.TeamName + "　期間：" + period);
            sb.AppendLine("生成：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "（" + AppInfo.Name + " " + AppInfo.Version + "）");
            sb.AppendLine();
            sb.AppendLine(proposal);
            sb.AppendLine();
            sb.AppendLine("----------------------------------------------------------------");
            sb.AppendLine("【参考】この提案の材料にした集計サマリー（数値と傾向のみ・依頼文の本文は含みません）");
            sb.AppendLine();
            sb.AppendLine(summary);

            string dir = System.IO.Path.Combine(svc.Store.Paths.DataDir, "ai-reports");
            Hardening.SecureDirectory(dir);
            string file = System.IO.Path.Combine(dir, "AI活用レポート_" + DateTime.Now.ToString("yyyy-MM-dd_HHmm") + ".txt");
            File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
            return file;
        }

        public static string BuildSummary(string teamName, string period, IEnumerable<ExecRow> rows, IEnumerable<DailyPoint> daily,
            int teamSize, IEnumerable<string> insights) { return BuildSummary(teamName, period, rows, daily, teamSize, insights, null); }

        /// <summary>ダッシュボードの集計から、AIに渡す要約テキストを組み立てる（依頼文の本文は含めない）。baseline があれば目標も添える。</summary>
        public static string BuildSummary(string teamName, string period, IEnumerable<ExecRow> rows, IEnumerable<DailyPoint> daily,
            int teamSize, IEnumerable<string> insights, TeamAnalytics.TeamBaseline baseline)
        {
            var list = rows.OrderByDescending(r => r.Score).ToList();
            var sb = new StringBuilder();
            sb.AppendLine("チーム名：" + teamName);
            sb.AppendLine("期間：" + period);
            sb.AppendLine("対象人数：" + teamSize + "、うち今期AIを利用：" + list.Count(r => r.Tokens > 0) + "人");
            sb.AppendLine("平均AI活用度スコア：" + (list.Count == 0 ? 0 : (int)Math.Round(list.Average(r => r.Score))) + "（0-100）");
            if (baseline != null && baseline.Enabled)
                sb.AppendLine("顧客のスタートライン（基準）：" + baseline.StageName + "／目標 平均スコア " + baseline.TargetScore + "・活用率 " + baseline.TargetAdoptionPct + "%");
            sb.AppendLine();
            sb.AppendLine("■ メンバー別（名前／レベル／スコア／稼働日数／依頼数／合計トークン／API換算円／使用ツール）");
            foreach (var r in list.Take(30))
                sb.AppendLine(string.Format("- {0}／{1}／{2}／稼働{3}日／依頼{4}／{5}tok／{6}円／{7}",
                    r.Name, r.Level, r.Score, r.ActiveDays, r.Prompts, r.Tokens, (long)Math.Round(r.Jpy),
                    r.UsesClaude && r.UsesCodex ? "Claude+Codex" : r.UsesClaude ? "Claudeのみ" : r.UsesCodex ? "Codexのみ" : "—"));
            var dl = daily.Where(d => d.Tokens > 0).OrderBy(d => d.Date).ToList();
            if (dl.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("■ 日別トークン（date, claude, codex）");
                foreach (var d in dl.Skip(Math.Max(0, dl.Count - 14)))
                    sb.AppendLine(string.Format("- {0}: claude {1}, codex {2}", d.Date, d.Claude, d.Codex));
            }
            var ins = insights.ToList();
            if (ins.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("■ ツールが自動抽出した気づき（参考）");
                foreach (var s in ins) sb.AppendLine("- " + s);
            }
            sb.AppendLine();
            sb.AppendLine("上記だけをもとに、現状サマリーと次の一手を日本語で提案してください。");
            return sb.ToString();
        }
    }
}
