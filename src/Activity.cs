using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AIUsageBar
{
    /// <summary>「誰が・いつ・どこで・何を入力したか」の1件</summary>
    public class PromptEntry
    {
        public DateTime Local;
        public Tool Tool;
        public string SessionKey;
        public string SessionTitle;
        public string Cwd;
        public string Via;        // 入力経路
        public string InputKind;  // 本人の入力 / 自動実行 / 他のプログラム経由
        public string Actor;      // Windowsユーザー@PC
        public string Account;    // AIのアカウント
        public string Text;       // 依頼文（表示時に読み取り）
    }

    public class ClaudeAccountInfo
    {
        public string Email;
        public string DisplayName;
        public string Organization;
        public string AccountUuid;
    }

    /// <summary>作業ログの組み立てと書き出し。本文は必要なときにだけログから読む。</summary>
    public class Activity
    {
        readonly UsageStore store;
        ClaudeAccountInfo claudeAccount;
        DateTime claudeAccountMTime;
        Dictionary<string, string> codexIndex;
        DateTime codexIndexMTime;
        readonly Dictionary<string, string> titleCache = new Dictionary<string, string>();
        readonly Dictionary<string, string> promptCache = new Dictionary<string, string>();

        public Activity(UsageStore store)
        {
            this.store = store;
        }

        /// <summary>見本画像の生成時だけ差し替える表示名（本番では null）。実PCのユーザー名・PC名を画像に写さないため。</summary>
        public static string LocalActorOverride;
        public static string LocalActor
        {
            get { return LocalActorOverride ?? (Environment.UserName + "@" + Environment.MachineName); }
        }

        public ClaudeAccountInfo ClaudeAccount
        {
            get
            {
                string path = Path.Combine(store.Paths.Home, ".claude.json");
                try
                {
                    if (!File.Exists(path)) return null;
                    var mt = File.GetLastWriteTimeUtc(path);
                    if (claudeAccount != null && mt == claudeAccountMTime) return claudeAccount;
                    // 認証トークンは別ファイル。ここではアカウントの表示名・メールだけを使う
                    var o = Json.Parse(FileUtil.ReadShared(path));
                    var a = Json.Obj(o, "oauthAccount");
                    claudeAccount = a == null ? null : new ClaudeAccountInfo
                    {
                        Email = Json.Str(a, "emailAddress"),
                        DisplayName = Json.Str(a, "displayName") ?? Json.Str(a, "fullName"),
                        Organization = Json.Str(a, "organizationName"),
                        AccountUuid = Json.Str(a, "accountUuid"),
                    };
                    claudeAccountMTime = mt;
                    return claudeAccount;
                }
                catch { return claudeAccount; }
            }
        }

        public string CodexAccountLabel
        {
            get
            {
                var c = store.Codex;
                if (c == null) return null;
                if (c.AccountType == "apiKey") return "APIキー";
                return c.AccountEmail;
            }
        }

        Dictionary<string, string> CodexIndex()
        {
            string path = Path.Combine(store.Paths.CodexHome, "session_index.jsonl");
            try
            {
                var mt = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
                if (codexIndex == null || mt != codexIndexMTime)
                {
                    codexIndex = store.Logs.ReadCodexIndex();
                    codexIndexMTime = mt;
                    lock (titleCache) titleCache.Clear();
                }
            }
            catch { if (codexIndex == null) codexIndex = new Dictionary<string, string>(); }
            return codexIndex;
        }

        public string Title(SessionInfo s)
        {
            string cached;
            lock (titleCache) if (titleCache.TryGetValue(s.Key, out cached)) return cached;
            if (!store.PromptsAllowed) return "（タイトル非表示）";
            string t = store.Logs.ReadTitle(s, CodexIndex());
            if (string.IsNullOrWhiteSpace(t))
            {
                var first = store.Logs.FirstPrompt(s);
                string p = first == null ? null : PromptText(s.Tool, first);
                t = p == null ? null : Fmt.OneLine(p, 60);
            }
            if (string.IsNullOrWhiteSpace(t)) t = s.Tool == Tool.Codex ? "（タイトルなし）" : "（タイトルなし）";
            lock (titleCache) titleCache[s.Key] = t;
            return t;
        }

        public void InvalidateTitles()
        {
            lock (titleCache) titleCache.Clear();
        }

        public string PromptText(Tool tool, LineRef r)
        {
            string key = r.Path + "|" + r.Offset;
            string cached;
            lock (promptCache) if (promptCache.TryGetValue(key, out cached)) return cached;
            if (!store.PromptsAllowed) return null;
            string text = store.Logs.ReadPrompt(tool, r);
            if (text != null && text.Length > 20000) text = text.Substring(0, 20000) + "…";
            lock (promptCache) { if (promptCache.Count > 500) promptCache.Clear(); promptCache[key] = text; }
            return text;
        }

        // ---------- 表示用の名前 ----------

        public static string ViaLabel(SessionInfo s, string source)
        {
            if (s.Tool == Tool.ClaudeCode)
            {
                string e = source ?? s.Entrypoint;
                string label;
                switch (e)
                {
                    case "claude-desktop": label = "Claudeアプリ（Code）"; break;
                    case "cli": label = "ターミナル（CLI）"; break;
                    case "sdk-cli": label = "他のプログラムから実行"; break;
                    case "sdk-ts":
                    case "sdk-py": label = "SDK"; break;
                    case "claude-vscode": label = "VS Code"; break;
                    case "claude-jetbrains": label = "JetBrains"; break;
                    case null: label = "Claude Code"; break;
                    default: label = e; break;
                }
                return label;
            }
            string via;
            switch (s.Originator)
            {
                case "Codex Desktop": via = "Codexアプリ"; break;
                case "codex_exec": via = "codex exec"; break;
                case "codex_cli_rs": via = "ターミナル（CLI）"; break;
                case "codex_vscode": via = "VS Code"; break;
                case null: via = "Codex"; break;
                default: via = s.Originator; break;
            }
            if (s.ThreadSource == "automation") via += "・オートメーション";
            if (s.ThreadSource == "subagent") via += "・サブエージェント" + (s.Nickname != null ? "（" + s.Nickname + "）" : "");
            return via;
        }

        public static string InputKindLabel(SessionInfo s, string source)
        {
            if (s.Tool == Tool.ClaudeCode)
            {
                string e = source ?? s.Entrypoint;
                if (e == "sdk-cli" || e == "sdk-ts" || e == "sdk-py") return "他のプログラム経由";
                return "本人の入力";
            }
            if (s.ThreadSource == "subagent") return "AIが入力（サブエージェント）";
            if (s.ThreadSource == "automation") return "自動実行";
            if (s.Originator == "codex_exec") return "コマンド実行";
            return "本人の入力";
        }

        public string AccountLabel(SessionInfo s)
        {
            if (s.Tool == Tool.Codex)
            {
                string a = CodexAccountLabel;
                return a == null ? "未取得" : a + "（現在のログイン）";
            }
            var acc = ClaudeAccount;
            if (acc == null) return "未取得";
            if (s.OwnerAccount != null && acc.AccountUuid != null && !string.Equals(s.OwnerAccount, acc.AccountUuid, StringComparison.OrdinalIgnoreCase))
                return "別のアカウント（" + s.OwnerAccount.Substring(0, Math.Min(8, s.OwnerAccount.Length)) + "…）";
            string email = acc.Email ?? acc.DisplayName ?? "アカウント";
            return s.OwnerAccount != null ? email : email + "（現在のログイン）";
        }

        public static string FolderName(string cwd)
        {
            if (string.IsNullOrEmpty(cwd)) return "（フォルダ不明）";
            string trimmed = cwd.TrimEnd('\\', '/');
            string name = Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? cwd : name;
        }

        // ---------- 一覧 ----------

        public List<PromptEntry> Entries(IEnumerable<LocalLogs.SessionView> views, DateTime localDay, bool includeText)
        {
            var list = new List<PromptEntry>();
            foreach (var v in views)
            {
                var s = v.Info;
                string title = Title(s);
                foreach (var p in store.Logs.PromptsForDay(s, localDay))
                    list.Add(MakeEntry(s, title, p, includeText));
            }
            return list.OrderBy(e => e.Local).ToList();
        }

        PromptEntry MakeEntry(SessionInfo s, string title, LineRef p, bool includeText)
        {
            return new PromptEntry
            {
                Local = Fmt.FromUnixMs(p.T).ToLocalTime(),
                Tool = s.Tool,
                SessionKey = s.Key,
                SessionTitle = title,
                Cwd = s.Cwd,
                Via = ViaLabel(s, p.Source),
                InputKind = InputKindLabel(s, p.Source),
                Actor = LocalActor,
                Account = AccountLabel(s),
                Text = includeText ? PromptText(s.Tool, p) : null,
            };
        }

        // ---------- 書き出し ----------

        public static string ToolName(Tool t) { return t == Tool.Codex ? "Codex" : "Claude Code"; }

        public string ExportMarkdown(List<DateTime> days, Tool? filter, bool includeText)
        {
            includeText = includeText && store.PromptsAllowed;
            Func<string, string> E = Hardening.MdInline;
            var sb = new StringBuilder();
            sb.AppendLine("# AI作業ログ");
            sb.AppendLine();
            sb.AppendLine("- 期間：" + days.Min().ToString("yyyy-MM-dd") + (days.Count > 1 ? " 〜 " + days.Max().ToString("yyyy-MM-dd") : ""));
            sb.AppendLine("- 対象：" + (filter == null ? "Claude Code・Codex" : ToolName(filter.Value)));
            sb.AppendLine("- 入力者（このPCのユーザー）：" + E(LocalActor));
            var ca = ClaudeAccount;
            sb.AppendLine("- Claudeアカウント：" + E(ca == null ? "未取得" : (ca.Email ?? ca.DisplayName)));
            sb.AppendLine("- Codexアカウント：" + E(CodexAccountLabel ?? "未取得"));
            sb.AppendLine("- 依頼文：" + (includeText ? "含む" : "含まない"));
            sb.AppendLine("- 書き出し：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "（" + AppInfo.Name + " " + AppInfo.Version + "）");
            sb.AppendLine();
            sb.AppendLine("> このPCに保存されたClaude Code・Codexの記録から作成。ChatGPT・Claudeの通常チャットは含みません。アカウントは現在のログイン情報です。依頼文は書式として解釈されないようコードブロックに入れています。");
            sb.AppendLine();

            foreach (var day in days.OrderByDescending(d => d))
            {
                var views = store.Logs.SessionsForDay(day).Where(v => filter == null || v.Info.Tool == filter.Value).ToList();
                sb.AppendLine("## " + day.ToString("yyyy-MM-dd (ddd)", new CultureInfo("ja-JP")));
                sb.AppendLine();
                if (views.Count == 0) { sb.AppendLine("この日付のデータはありません"); sb.AppendLine(); continue; }

                foreach (Tool t in new[] { Tool.ClaudeCode, Tool.Codex })
                {
                    if (filter != null && filter.Value != t) continue;
                    var sum = store.Logs.DayTotal(t, day);
                    if (sum.Responses == 0) continue;
                    sb.AppendLine("- " + ToolName(t) + " 合計 " + Fmt.Full(sum.Total) + " トークン（" + E(BreakdownText(t, sum)) + "）");
                }
                sb.AppendLine();

                sb.AppendLine("### セッション");
                sb.AppendLine();
                sb.AppendLine("| 時間 | ツール | タイトル | 作業フォルダ | 経路 | 依頼 | 合計 | 内訳 |");
                sb.AppendLine("|---|---|---|---|---|---:|---:|---|");
                foreach (var v in views.OrderBy(x => x.First))
                {
                    sb.AppendLine("| " + TimeRange(v) + " | " + ToolName(v.Info.Tool) + " | " + E(Title(v.Info)) + " | " + E(v.Info.Cwd ?? "") + " | " + E(ViaLabel(v.Info, null)) +
                        " | " + v.PromptsInDay + " | " + Fmt.Full(v.Tokens.Total) + (v.Estimated ? "（推定）" : "") + " | " + E(BreakdownText(v.Info.Tool, v.Tokens)) +
                        (v.Subagents.Count > 0 ? "・サブエージェント" + v.Subagents.Count + "件" : "") + " |");
                }
                sb.AppendLine();

                sb.AppendLine("### 入力（誰が・何を）");
                sb.AppendLine();
                var entries = Entries(views, day, includeText);
                if (entries.Count == 0) sb.AppendLine("この日の入力記録はありません（トークン記録のみ）");
                foreach (var e in entries)
                {
                    sb.AppendLine("- **" + e.Local.ToString("HH:mm") + "** " + ToolName(e.Tool) + "／" + E(e.Via) + "／" + E(e.InputKind));
                    sb.AppendLine("  - 入力者：" + E(e.Actor) + "　アカウント：" + E(e.Account));
                    sb.AppendLine("  - セッション：" + E(e.SessionTitle) + "　フォルダ：" + E(e.Cwd ?? "不明"));
                    if (includeText)
                    {
                        sb.AppendLine();
                        sb.Append(Hardening.MdFence(e.Text ?? "（読み取れませんでした）", "    "));
                        sb.AppendLine();
                    }
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        public string ExportCsv(List<DateTime> days, Tool? filter, bool includeText)
        {
            includeText = includeText && store.PromptsAllowed;
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", new[] { "日時", "ツール", "経路", "入力の種類", "入力者", "アカウント", "セッション", "作業フォルダ", "依頼文" }.Select(Hardening.CsvCell)));
            foreach (var day in days.OrderBy(d => d))
            {
                var views = store.Logs.SessionsForDay(day).Where(v => filter == null || v.Info.Tool == filter.Value).ToList();
                foreach (var e in Entries(views, day, includeText))
                {
                    sb.AppendLine(string.Join(",", new[] {
                        e.Local.ToString("yyyy-MM-dd HH:mm:ss"), ToolName(e.Tool), e.Via, e.InputKind, e.Actor, e.Account,
                        e.SessionTitle, e.Cwd ?? "", includeText ? (e.Text ?? "") : "" }.Select(Hardening.CsvCell)));
                }
            }
            return sb.ToString();
        }

        public static string BreakdownText(Tool t, TokenSum s)
        {
            if (t == Tool.Codex)
                return "入力 " + Fmt.Tokens(s.Input) + "（うちキャッシュ " + Fmt.Tokens(s.CacheRead) + "）・出力 " + Fmt.Tokens(s.Output) + "（うち推論 " + Fmt.Tokens(s.Reasoning) + "）";
            return "入力 " + Fmt.Tokens(s.Input) + "・出力 " + Fmt.Tokens(s.Output) + "・キャッシュ読取 " + Fmt.Tokens(s.CacheRead) + "・キャッシュ作成 " + Fmt.Tokens(s.CacheWrite);
        }

        public static string TimeRange(LocalLogs.SessionView v)
        {
            if (v.First == 0) return "--:--";
            string a = Fmt.FromUnixMs(v.First).ToLocalTime().ToString("HH:mm");
            string b = Fmt.FromUnixMs(v.Last).ToLocalTime().ToString("HH:mm");
            return a == b ? a : a + "–" + b;
        }

    }
}
