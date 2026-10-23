using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AIUsageBar
{
    public enum Tool { ClaudeCode, Codex }

    /// <summary>1応答ぶんのトークン（重複除去済みの単位）</summary>
    public class TokenRecord
    {
        public string Session;   // セッションキー（claude:xxx / codex:xxx）
        public long T;           // UTCミリ秒
        public long Input;       // Claude：キャッシュ以外の入力 / Codex：入力（キャッシュ含む）
        public long Output;      // 出力（Codexは推論を含む）
        public long CacheRead;   // Claude：キャッシュ読み取り / Codex：キャッシュ済み入力
        public long CacheWrite;  // Claude：キャッシュ作成（合計）
        public long CacheWrite1h;// Claude：うち1時間キャッシュ（料金が異なる）
        public long Reasoning;   // Codex：推論出力
        public long Total;       // 合計
        public bool Fallback;    // Codex：旧形式の累計値から推定した記録
        public string Model;     // 応答したモデル（料金の換算に使う）

        public void MaxWith(TokenRecord o)
        {
            Input = Math.Max(Input, o.Input);
            Output = Math.Max(Output, o.Output);
            CacheRead = Math.Max(CacheRead, o.CacheRead);
            CacheWrite = Math.Max(CacheWrite, o.CacheWrite);
            CacheWrite1h = Math.Max(CacheWrite1h, o.CacheWrite1h);
            if (Model == null) Model = o.Model;
            Reasoning = Math.Max(Reasoning, o.Reasoning);
            Total = Math.Max(Total, o.Total);
            if (o.T != 0 && (T == 0 || o.T < T)) T = o.T;
        }
    }

    public class TokenSum
    {
        public long Input, Output, CacheRead, CacheWrite, CacheWrite1h, Reasoning, Total;
        public int Responses;

        public void Add(TokenRecord r)
        {
            Input += r.Input; Output += r.Output; CacheRead += r.CacheRead; CacheWrite += r.CacheWrite; CacheWrite1h += r.CacheWrite1h;
            Reasoning += r.Reasoning; Total += r.Total; Responses++;
        }

        public void Add(TokenSum s)
        {
            Input += s.Input; Output += s.Output; CacheRead += s.CacheRead; CacheWrite += s.CacheWrite; CacheWrite1h += s.CacheWrite1h;
            Reasoning += s.Reasoning; Total += s.Total; Responses += s.Responses;
        }
    }

    /// <summary>日別・ツール別・モデル別の集計（記録を消した後も長期間残す）</summary>
    public class DailyUsage
    {
        public string Date;   // yyyy-MM-dd（この端末の現地時刻）
        public Tool Tool;
        public string Model;
        public TokenSum Sum = new TokenSum();
    }

    /// <summary>ファイル内の位置だけを覚える（本文はキャッシュに保存しない）</summary>
    public class LineRef
    {
        public string Path;
        public long Offset;
        public long T;
        public string Source;   // 入力経路（Claude：entrypoint / Codex：originator）
        public string Hash;     // 重複判定用（本文のハッシュ。本文そのものは保存しない）
    }

    public class SessionInfo
    {
        public string Key;
        public Tool Tool;
        public string Id;
        public string Cwd;
        public long Start;
        public long End;
        public string Originator;   // Codex Desktop / codex_exec など
        public string ThreadSource; // user / automation / subagent
        public string Parent;       // 親セッションのキー（サブエージェント）
        public string Nickname;
        public string Model;
        public string Entrypoint;   // Claude Code：claude-desktop / cli / sdk-cli など
        public string OwnerAccount; // Claude Code：リモート接続時のアカウントUUID
        public List<LineRef> Prompts = new List<LineRef>();
        public LineRef CustomTitle;
        public LineRef AiTitle;
        public HashSet<string> Files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public void Touch(long t)
        {
            if (t <= 0) return;
            if (Start == 0 || t < Start) Start = t;
            if (t > End) End = t;
        }
    }

    public class FileState
    {
        public long Size;
        public long MTime;
        public long Offset;
        public string Session;
        // Codex旧形式用：直前の累計
        public long LastTotal;
    }

    /// <summary>
    /// Claude Code（~/.claude/projects）とCodex（~/.codex/sessions）のローカル記録を
    /// 差分読み取りで集計する。キャッシュに保存するのは使用量・時刻・識別子・ファイル位置のみ。
    /// </summary>
    public class LocalLogs
    {
        public const int RetentionDays = 14;
        const int CacheVersion = 4;
        public const int HistoryDays = 400;
        const int MaxPromptRefs = 300;

        readonly AppPaths paths;
        readonly object gate = new object();

        Dictionary<string, FileState> files = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, SessionInfo> sessions = new Dictionary<string, SessionInfo>();
        Dictionary<string, TokenRecord> records = new Dictionary<string, TokenRecord>();
        Dictionary<string, DailyUsage> history = new Dictionary<string, DailyUsage>();

        public DateTime? LastScanUtc;
        public int ExaminedFiles;
        public int DiscardedLines;
        public int SkippedRecords;
        public long LastScanBytes;
        public TimeSpan LastScanDuration;
        public bool ClaudeAvailable;
        public bool CodexAvailable;

        /// <summary>false：作業フォルダ・依頼文の位置・タイトル・入力経路を保持しない（トークン数だけ集計）</summary>
        public bool CollectActivity = true;

        public LocalLogs(AppPaths paths)
        {
            this.paths = paths;
        }

        /// <summary>依頼文・タイトルを読んでよいファイルか（ログフォルダ配下のローカルファイルだけ）</summary>
        public bool IsAllowedLogFile(string path)
        {
            if (string.IsNullOrEmpty(path) || Hardening.IsUncPath(path)) return false;
            if (!path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return false;
            return Hardening.IsUnder(path, paths.ClaudeProjects)
                || Hardening.IsUnder(path, Path.Combine(paths.CodexHome, "sessions"))
                || Hardening.IsUnder(path, Path.Combine(paths.CodexHome, "archived_sessions"));
        }

        static void StripActivity(SessionInfo s)
        {
            s.Prompts.Clear();
            s.CustomTitle = null;
            s.AiTitle = null;
            s.Cwd = null;
            s.Entrypoint = null;
            s.OwnerAccount = null;
            s.Nickname = null;
        }

        /// <summary>ポリシーで詳細ログが無効になったとき、保持している作業情報を消す</summary>
        public void ApplyActivityCollection(bool collect)
        {
            CollectActivity = collect;
            if (collect) return;
            lock (gate) foreach (var s in sessions.Values) StripActivity(s);
        }

        string CachePath { get { return Path.Combine(paths.DataDir, "local-logs-v4.json"); } }

        // ------------------------------------------------------------------ スキャン

        public void Scan(Action<long, long> progress)
        {
            var started = DateTime.UtcNow;
            long cutoff = Fmt.ToUnixMs(DateTime.UtcNow.AddDays(-RetentionDays));
            var targets = new List<KeyValuePair<string, Tool>>();

            ClaudeAvailable = Directory.Exists(paths.ClaudeProjects);
            CodexAvailable = Directory.Exists(Path.Combine(paths.CodexHome, "sessions")) || Directory.Exists(Path.Combine(paths.CodexHome, "archived_sessions"));

            if (ClaudeAvailable) foreach (var f in SafeFiles(paths.ClaudeProjects)) targets.Add(new KeyValuePair<string, Tool>(f, Tool.ClaudeCode));
            foreach (var sub in new[] { "sessions", "archived_sessions" })
            {
                string dir = Path.Combine(paths.CodexHome, sub);
                if (Directory.Exists(dir)) foreach (var f in SafeFiles(dir)) targets.Add(new KeyValuePair<string, Tool>(f, Tool.Codex));
            }

            // 変更のあったファイルだけ読む
            var work = new List<Tuple<string, Tool, FileInfo, FileState>>();
            long totalBytes = 0;
            bool needFullRebuild = false;
            lock (gate)
            {
                foreach (var t in targets)
                {
                    FileInfo fi;
                    try { fi = new FileInfo(t.Key); if (!fi.Exists) continue; } catch { continue; }
                    long mtime = Fmt.ToUnixMs(fi.LastWriteTimeUtc);
                    if (mtime < cutoff) continue;
                    FileState st;
                    files.TryGetValue(t.Key, out st);
                    if (st != null && st.Size == fi.Length && st.MTime == mtime) continue;
                    if (st != null && fi.Length < st.Offset) { needFullRebuild = true; }
                    work.Add(Tuple.Create(t.Key, t.Value, fi, st));
                }
                if (needFullRebuild)
                {
                    // ファイルが書き換えられて短くなった：安全のため全体を読み直す
                    files.Clear(); sessions.Clear(); records.Clear();
                    work.Clear();
                    foreach (var t in targets)
                    {
                        FileInfo fi;
                        try { fi = new FileInfo(t.Key); if (!fi.Exists) continue; } catch { continue; }
                        if (Fmt.ToUnixMs(fi.LastWriteTimeUtc) < cutoff) continue;
                        work.Add(Tuple.Create(t.Key, t.Value, fi, (FileState)null));
                    }
                }
                foreach (var w in work) totalBytes += w.Item3.Length - (w.Item4 == null ? 0 : w.Item4.Offset);
            }

            long done = 0;
            int discarded = 0, skipped = 0;
            foreach (var w in work)
            {
                var reader = new LineReader();
                var st = w.Item4 ?? new FileState();
                var newState = new FileState { Session = st.Session, LastTotal = st.LastTotal, Offset = st.Offset };
                var batch = new Batch();
                try
                {
                    long end;
                    if (w.Item2 == Tool.ClaudeCode)
                        end = ScanClaude(reader, w.Item1, newState, batch, cutoff, b => { done += b; if (progress != null) progress(done, totalBytes); });
                    else
                        end = ScanCodex(reader, w.Item1, newState, batch, cutoff, b => { done += b; if (progress != null) progress(done, totalBytes); });
                    newState.Offset = end;
                    newState.Size = w.Item3.Length;
                    newState.MTime = Fmt.ToUnixMs(w.Item3.LastWriteTimeUtc);
                    discarded += reader.DiscardedLines;
                    skipped += batch.Skipped;
                    lock (gate) { Merge(batch); files[w.Item1] = newState; }
                }
                catch (IOException) { skipped++; }
                catch (UnauthorizedAccessException) { skipped++; }
            }

            lock (gate)
            {
                Prune(cutoff);
                RebuildHistory(cutoff);
                ExaminedFiles = work.Count;
                DiscardedLines = discarded;
                SkippedRecords = skipped;
                LastScanBytes = totalBytes;
                LastScanUtc = DateTime.UtcNow;
                LastScanDuration = DateTime.UtcNow - started;
            }
        }

        /// <summary>
        /// 記録が残っている期間の日別集計を作り直し、それより前の日は前回までの集計を残す。
        /// 記録は14日で消すが、日別集計は400日残すので、月単位の利用量・金額を出せる。
        /// </summary>
        void RebuildHistory(long cutoff)
        {
            string coverFrom = Fmt.FromUnixMs(cutoff).ToLocalTime().Date.AddDays(1).ToString("yyyy-MM-dd");
            foreach (var k in history.Where(kv => string.CompareOrdinal(kv.Value.Date, coverFrom) >= 0).Select(kv => kv.Key).ToList()) history.Remove(k);
            var realCodex = CodexSessionsWithRealRecords();
            foreach (var r in records.Values)
            {
                if (r.Fallback && realCodex.Contains(r.Session)) continue;
                string date = Fmt.FromUnixMs(r.T).ToLocalTime().ToString("yyyy-MM-dd");
                if (string.CompareOrdinal(date, coverFrom) < 0) continue;
                Tool tool = r.Session.StartsWith("codex:") ? Tool.Codex : Tool.ClaudeCode;
                string key = date + "|" + (tool == Tool.Codex ? "codex" : "claude") + "|" + (r.Model ?? "");
                DailyUsage d;
                if (!history.TryGetValue(key, out d)) { d = new DailyUsage { Date = date, Tool = tool, Model = r.Model }; history[key] = d; }
                d.Sum.Add(r);
            }
            string keepFrom = DateTime.Now.Date.AddDays(-HistoryDays).ToString("yyyy-MM-dd");
            foreach (var k in history.Where(kv => string.CompareOrdinal(kv.Value.Date, keepFrom) < 0).Select(kv => kv.Key).ToList()) history.Remove(k);
        }

        public List<DailyUsage> History()
        {
            lock (gate)
                return history.Values.Select(d => { var c = new DailyUsage { Date = d.Date, Tool = d.Tool, Model = d.Model }; c.Sum.Add(d.Sum); return c; })
                    .OrderBy(d => d.Date).ToList();
        }

        static IEnumerable<string> SafeFiles(string root)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                string[] fs = null, ds = null;
                try { fs = Directory.GetFiles(dir, "*.jsonl"); } catch { }
                try { ds = Directory.GetDirectories(dir); } catch { }
                if (fs != null) foreach (var f in fs) yield return f;
                if (ds != null) foreach (var d in ds) stack.Push(d);
            }
        }

        class Batch
        {
            public Dictionary<string, SessionInfo> Sessions = new Dictionary<string, SessionInfo>();
            public Dictionary<string, TokenRecord> Records = new Dictionary<string, TokenRecord>();
            public int Skipped;

            public SessionInfo Session(string key, Tool tool, string id)
            {
                SessionInfo s;
                if (!Sessions.TryGetValue(key, out s))
                {
                    s = new SessionInfo { Key = key, Tool = tool, Id = id };
                    Sessions[key] = s;
                }
                return s;
            }
        }

        void Merge(Batch b)
        {
            if (!CollectActivity) foreach (var ns in b.Sessions.Values) StripActivity(ns);
            foreach (var kv in b.Sessions)
            {
                SessionInfo cur;
                if (!sessions.TryGetValue(kv.Key, out cur)) { sessions[kv.Key] = kv.Value; continue; }
                var n = kv.Value;
                cur.Touch(n.Start); cur.Touch(n.End);
                if (cur.Cwd == null) cur.Cwd = n.Cwd;
                if (n.Originator != null) cur.Originator = n.Originator;
                if (n.ThreadSource != null) cur.ThreadSource = n.ThreadSource;
                if (n.Parent != null) cur.Parent = n.Parent;
                if (n.Nickname != null) cur.Nickname = n.Nickname;
                if (n.Model != null) cur.Model = n.Model;
                if (n.Entrypoint != null) cur.Entrypoint = n.Entrypoint;
                if (n.OwnerAccount != null) cur.OwnerAccount = n.OwnerAccount;
                if (n.CustomTitle != null) cur.CustomTitle = n.CustomTitle;
                if (n.AiTitle != null) cur.AiTitle = n.AiTitle;
                foreach (var f in n.Files) cur.Files.Add(f);
                foreach (var p in n.Prompts)
                    if (!cur.Prompts.Any(x => SamePrompt(x, p)))
                        cur.Prompts.Add(p);
                if (cur.Prompts.Count > MaxPromptRefs) cur.Prompts = cur.Prompts.OrderBy(x => x.T).Skip(cur.Prompts.Count - MaxPromptRefs).ToList();
            }
            foreach (var kv in b.Records)
            {
                TokenRecord cur;
                if (records.TryGetValue(kv.Key, out cur)) cur.MaxWith(kv.Value);
                else records[kv.Key] = kv.Value;
            }
        }

        static bool SamePrompt(LineRef a, LineRef b)
        {
            if (a.Offset == b.Offset && string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase)) return true;
            // 作業中に送ったメッセージは「予約」と「通常の入力」の両方に残ることがある
            return a.Hash != null && a.Hash == b.Hash && Math.Abs(a.T - b.T) < 120000;
        }

        static void AddPrompt(SessionInfo s, LineRef r)
        {
            if (s.Prompts.Any(x => SamePrompt(x, r))) return;
            s.Prompts.Add(r);
        }

        void Prune(long cutoff)
        {
            foreach (var k in records.Where(kv => kv.Value.T < cutoff).Select(kv => kv.Key).ToList()) records.Remove(k);
            var alive = new HashSet<string>(records.Values.Select(r => r.Session));
            foreach (var k in sessions.Where(kv => kv.Value.End < cutoff && !alive.Contains(kv.Key)).Select(kv => kv.Key).ToList()) sessions.Remove(k);
            foreach (var k in files.Where(kv => kv.Value.MTime < cutoff).Select(kv => kv.Key).ToList()) files.Remove(k);
            foreach (var s in sessions.Values) s.Prompts.RemoveAll(p => p.T < cutoff);
        }

        // ---------- Claude Code ----------

        static readonly byte[] PAssistant = LineReader.B("\"role\":\"assistant\"");
        static readonly byte[] PUserType = LineReader.B("\"type\":\"user\"");
        static readonly byte[] PToolResult = LineReader.B("\"tool_result\"");
        static readonly byte[] PToolUseId = LineReader.B("\"tool_use_id\"");
        static readonly byte[] PAiTitle = LineReader.B("\"type\":\"ai-title\"");
        static readonly byte[] PCustomTitle = LineReader.B("\"type\":\"custom-title\"");
        static readonly byte[] PSidechainTrue = LineReader.B("\"isSidechain\":true");
        static readonly byte[] PIsMeta = LineReader.B("\"isMeta\":true");

        static readonly byte[] PBridge = LineReader.B("\"type\":\"bridge-session\"");
        static readonly byte[] PQueued = LineReader.B("\"type\":\"queued_command\"");

        const int KAssistant = 1, KUser = 2, KAiTitle = 3, KCustomTitle = 4, KBridge = 5, KQueued = 6;

        long ScanClaude(LineReader reader, string path, FileState st, Batch batch, long cutoff, Action<long> progress)
        {
            // projects/<project>/<session>.jsonl または projects/<project>/<session>/subagents/agent-x.jsonl
            string fileSession = null;
            string dir = Path.GetDirectoryName(path);
            if (string.Equals(Path.GetFileName(dir), "subagents", StringComparison.OrdinalIgnoreCase))
                fileSession = Path.GetFileName(Path.GetDirectoryName(dir));
            else
                fileSession = Path.GetFileNameWithoutExtension(path);
            bool isSubagentFile = fileSession != Path.GetFileNameWithoutExtension(path);

            reader.LimitForKind = k => k == KAssistant ? 32 * 1024 * 1024 : (k == KUser || k == KQueued ? 256 * 1024 : 64 * 1024);
            LineReader.Classifier classify = (h, n) =>
            {
                if (LineReader.Contains(h, n, PAiTitle)) return KAiTitle;
                if (LineReader.Contains(h, n, PCustomTitle)) return KCustomTitle;
                if (LineReader.Contains(h, n, PBridge)) return KBridge;
                if (LineReader.Contains(h, n, PQueued)) return isSubagentFile || LineReader.Contains(h, n, PSidechainTrue) ? 0 : KQueued;
                if (LineReader.Contains(h, n, PAssistant)) return KAssistant;
                if (LineReader.Contains(h, n, PUserType))
                {
                    if (isSubagentFile || LineReader.Contains(h, n, PSidechainTrue)) return 0;
                    if (LineReader.Contains(h, n, PToolResult) || LineReader.Contains(h, n, PToolUseId)) return 0;
                    if (LineReader.Contains(h, n, PIsMeta)) return 0;
                    return KUser;
                }
                return 0;
            };

            return reader.Scan(path, st.Offset, classify, (offset, data, len, truncated, kind) =>
            {
                if (kind == KUser && truncated)
                {
                    // 大きな添付を含む依頼。時刻が分からないので位置だけ覚える
                    var s0 = batch.Session("claude:" + fileSession, Tool.ClaudeCode, fileSession);
                    s0.Files.Add(path);
                    AddPrompt(s0, new LineRef { Path = path, Offset = offset, T = s0.End });
                    return;
                }
                if (truncated) { batch.Skipped++; return; }
                object o;
                try { o = Json.Parse(Encoding.UTF8.GetString(data, 0, len)); }
                catch { batch.Skipped++; return; }

                string sid = Json.Str(o, "sessionId") ?? fileSession;
                var s = batch.Session("claude:" + sid, Tool.ClaudeCode, sid);
                s.Files.Add(path);
                if (kind == KAiTitle) { s.AiTitle = new LineRef { Path = path, Offset = offset }; return; }
                if (kind == KCustomTitle) { s.CustomTitle = new LineRef { Path = path, Offset = offset }; return; }
                if (kind == KBridge) { string owner = Json.Str(o, "ownerAccountUuid"); if (owner != null) s.OwnerAccount = owner; return; }
                string entry = Json.Str(o, "entrypoint");
                if (entry != null && !isSubagentFile) s.Entrypoint = entry;

                var ts = Fmt.ParseIsoUtc(Json.Str(o, "timestamp"));
                long t = ts == null ? 0 : Fmt.ToUnixMs(ts.Value);
                if (s.Cwd == null) s.Cwd = Json.Str(o, "cwd");

                if (kind == KUser || kind == KQueued)
                {
                    if (!IsHumanClaudePrompt(o)) return;
                    if (t < cutoff) return;
                    s.Touch(t);
                    AddPrompt(s, new LineRef { Path = path, Offset = offset, T = t, Source = entry ?? s.Entrypoint, Hash = CollectActivity ? Hardening.PromptHmac(paths.DataDir, ClaudePromptText(o)) : null });
                    return;
                }

                // assistant
                var msg = Json.Obj(o, "message");
                var usage = Json.Obj(msg, "usage");
                if (usage == null) return;
                if (t != 0 && t < cutoff) return;
                string model = Json.Str(msg, "model");
                if (model != null && !model.StartsWith("<")) s.Model = model;
                s.Touch(t);
                string msgId = Json.Str(msg, "id");
                string reqId = Json.Str(o, "requestId");
                string key = msgId != null || reqId != null
                    ? "c:" + (msgId ?? "") + ":" + (reqId ?? "")
                    : "c:" + Json.Str(o, "uuid");
                var r = new TokenRecord
                {
                    Session = s.Key,
                    T = t,
                    Input = Json.Long(usage, "input_tokens") ?? 0,
                    Output = Json.Long(usage, "output_tokens") ?? 0,
                    CacheRead = Json.Long(usage, "cache_read_input_tokens") ?? 0,
                    CacheWrite = Json.Long(usage, "cache_creation_input_tokens") ?? 0,
                    Model = model != null && !model.StartsWith("<") ? model : null,
                };
                var cc = Json.Obj(usage, "cache_creation");
                r.CacheWrite1h = Json.Long(cc, "ephemeral_1h_input_tokens") ?? 0;
                if (r.CacheWrite == 0)
                    r.CacheWrite = (Json.Long(cc, "ephemeral_5m_input_tokens") ?? 0) + r.CacheWrite1h;
                if (r.CacheWrite1h > r.CacheWrite) r.CacheWrite1h = r.CacheWrite;
                r.Total = r.Input + r.Output + r.CacheRead + r.CacheWrite;
                if (r.Total == 0) return;
                TokenRecord cur;
                if (batch.Records.TryGetValue(key, out cur)) cur.MaxWith(r); else batch.Records[key] = r;
            }, progress);
        }

        static bool IsHumanClaudePrompt(object o)
        {
            if (Json.Bool(o, "isMeta") == true || Json.Bool(o, "isSidechain") == true) return false;
            var origin = Json.Obj(o, "origin") ?? Json.Obj(Json.Obj(o, "attachment"), "origin");
            string kind = Json.Str(origin, "kind");
            if (kind != null && kind != "human") return false;
            string text = ClaudePromptText(o);
            if (text == null) return false;
            string t = text.TrimStart();
            if (t.Length == 0) return false;
            if (t.StartsWith("<command-") || t.StartsWith("<local-command") || t.StartsWith("<task-notification") ||
                t.StartsWith("<system-reminder") || t.StartsWith("Caveat:") || t.StartsWith("[Request interrupted"))
                return false;
            return true;
        }

        public static string ClaudePromptText(object o)
        {
            var att = Json.Obj(o, "attachment");
            if (att != null)
                return Json.Str(att, "type") == "queued_command" ? Json.Str(att, "prompt") : null;
            var msg = Json.Obj(o, "message");
            if (msg == null) return null;
            object content;
            if (!msg.TryGetValue("content", out content)) return null;
            if (content is string) return (string)content;
            var list = content as List<object>;
            if (list == null) return null;
            var sb = new StringBuilder();
            int images = 0;
            foreach (var c in list)
            {
                string type = Json.Str(c, "type");
                if (type == "text") { if (sb.Length > 0) sb.Append('\n'); sb.Append(Json.Str(c, "text")); }
                else if (type == "image") images++;
                else if (type == "tool_result") return null;
            }
            if (sb.Length == 0 && images > 0) return "［画像 " + images + "枚］";
            return sb.ToString();
        }

        // ---------- Codex ----------

        static readonly byte[] PSessionMeta = LineReader.B("\"type\":\"session_meta\"");
        static readonly byte[] PUsageRecord = LineReader.B("\"type\":\"token_usage_record\"");
        static readonly byte[] PTokenCount = LineReader.B("\"type\":\"token_count\"");
        static readonly byte[] PTurnContext = LineReader.B("\"type\":\"turn_context\"");
        static readonly byte[] PResponseItem = LineReader.B("\"type\":\"response_item\"");
        static readonly byte[] PRoleUser = LineReader.B("\"role\":\"user\"");
        static readonly byte[] PTextInjected1 = LineReader.B("\"text\":\"<");
        static readonly byte[] PTextInjected2 = LineReader.B("\"text\":\"# AGENTS.md");
        static readonly byte[] PTextEmpty = LineReader.B("\"text\":\"\"");
        static readonly byte[] PTimestamp = LineReader.B("{\"timestamp\":\"");

        const int KMeta = 11, KRecord = 12, KCount = 13, KTurn = 14, KPrompt = 15;

        long ScanCodex(LineReader reader, string path, FileState st, Batch batch, long cutoff, Action<long> progress)
        {
            reader.LimitForKind = k => k == KMeta ? 4 * 1024 * 1024 : (k == KTurn ? 4 * 1024 * 1024 : 256 * 1024);
            LineReader.Classifier classify = (h, n) =>
            {
                if (LineReader.Contains(h, n, PUsageRecord)) return KRecord;
                if (LineReader.Contains(h, n, PTokenCount)) return KCount;
                if (LineReader.Contains(h, n, PResponseItem) && LineReader.Contains(h, n, PRoleUser))
                {
                    if (LineReader.Contains(h, n, PTextInjected1) || LineReader.Contains(h, n, PTextInjected2) || LineReader.Contains(h, n, PTextEmpty)) return 0;
                    return KPrompt;
                }
                if (LineReader.Contains(h, n, PSessionMeta)) return KMeta;
                if (LineReader.Contains(h, n, PTurnContext)) return KTurn;
                return 0;
            };

            string sid = st.Session;
            // ファイル名の末尾がセッションID（rollout-日時-<id>.jsonl）
            if (sid == null)
            {
                string name = Path.GetFileNameWithoutExtension(path);
                if (name.Length >= 36) sid = name.Substring(name.Length - 36);
            }

            string originator = null;
            SessionInfo known;
            lock (gate) { if (sid != null && sessions.TryGetValue("codex:" + sid, out known)) originator = known.Originator; }

            return reader.Scan(path, st.Offset, classify, (offset, data, len, truncated, kind) =>
            {
                long t = 0;
                // 先頭の {"timestamp":"..." を直接読む（プロンプト行は全体を解析しない）
                if (len > 40 && StartsWith(data, PTimestamp))
                {
                    int start = PTimestamp.Length;
                    int end = Array.IndexOf(data, (byte)'"', start, Math.Min(64, len - start));
                    if (end > start)
                    {
                        var ts = Fmt.ParseIsoUtc(Encoding.ASCII.GetString(data, start, end - start));
                        if (ts != null) t = Fmt.ToUnixMs(ts.Value);
                    }
                }

                if (kind == KPrompt)
                {
                    if (sid == null || (t != 0 && t < cutoff)) return;
                    var sp = batch.Session("codex:" + sid, Tool.Codex, sid);
                    sp.Files.Add(path);
                    sp.Touch(t);
                    AddPrompt(sp, new LineRef { Path = path, Offset = offset, T = t, Source = originator });
                    return;
                }
                if (truncated) { batch.Skipped++; return; }

                object o;
                try { o = Json.Parse(Encoding.UTF8.GetString(data, 0, len)); }
                catch { batch.Skipped++; return; }
                var p = Json.Obj(o, "payload");
                if (p == null) return;

                if (kind == KMeta)
                {
                    string id = Json.Str(p, "id") ?? Json.Str(p, "session_id");
                    if (id == null) return;
                    // 同じファイルに別セッションのメタ情報（フォーク元）が入ることがあるので最初の1件だけ使う
                    if (st.Session != null && st.Session != id) return;
                    sid = id;
                    st.Session = id;
                    var sm = batch.Session("codex:" + id, Tool.Codex, id);
                    sm.Files.Add(path);
                    sm.Cwd = Json.Str(p, "cwd");
                    sm.Originator = Json.Str(p, "originator");
                    originator = sm.Originator;
                    sm.ThreadSource = Json.Str(p, "thread_source");
                    var metaTs = Fmt.ParseIsoUtc(Json.Str(p, "timestamp"));
                    if (metaTs != null && Fmt.ToUnixMs(metaTs.Value) >= cutoff) sm.Touch(Fmt.ToUnixMs(metaTs.Value));
                    var spawn = Json.Obj(Json.Obj(Json.Obj(p, "source"), "subagent"), "thread_spawn");
                    if (spawn != null)
                    {
                        string parent = Json.Str(spawn, "parent_thread_id");
                        if (parent != null) sm.Parent = "codex:" + parent;
                        sm.Nickname = Json.Str(spawn, "agent_nickname");
                        if (sm.ThreadSource == null) sm.ThreadSource = "subagent";
                    }
                    return;
                }

                if (sid == null) return;
                var s = batch.Session("codex:" + sid, Tool.Codex, sid);
                s.Files.Add(path);

                if (kind == KTurn)
                {
                    string model = Json.Str(p, "model");
                    if (model != null) s.Model = model;
                    if (s.Cwd == null) s.Cwd = Json.Str(p, "cwd");
                    return;
                }

                if (t != 0 && t < cutoff) return;

                if (kind == KRecord)
                {
                    string respId = Json.Str(p, "response_id");
                    var usage = Json.Obj(p, "usage");
                    if (usage == null) return;
                    string owner = Json.Str(p, "thread_id") ?? sid;
                    var so = owner == sid ? s : batch.Session("codex:" + owner, Tool.Codex, owner);
                    var r = CodexRecord(usage, so.Key, t, false);
                    r.Model = so.Model ?? s.Model;
                    if (r.Total == 0) return;
                    so.Touch(t);
                    string key = "x:" + (respId ?? (sid + ":" + offset));
                    TokenRecord cur;
                    if (batch.Records.TryGetValue(key, out cur)) cur.MaxWith(r); else batch.Records[key] = r;
                    return;
                }

                if (kind == KCount)
                {
                    // 旧形式：累計値の差分を使う（累計が減ったらリセットとみなす）
                    var info = Json.Obj(p, "info");
                    var total = Json.Obj(info, "total_token_usage");
                    if (total == null) return;
                    long tot = Json.Long(total, "total_tokens") ?? 0;
                    if (tot == st.LastTotal) return;
                    long delta = tot > st.LastTotal ? tot - st.LastTotal : tot;
                    st.LastTotal = tot;
                    if (delta <= 0) return;
                    var last = Json.Obj(info, "last_token_usage");
                    TokenRecord r = last != null ? CodexRecord(last, s.Key, t, true) : null;
                    if (r == null || r.Total != delta)
                        r = new TokenRecord { Session = s.Key, T = t, Fallback = true, Total = delta };
                    r.Model = s.Model;
                    s.Touch(t);
                    batch.Records["xf:" + sid + ":" + offset] = r;
                }
            }, progress);
        }

        static bool StartsWith(byte[] data, byte[] prefix)
        {
            for (int i = 0; i < prefix.Length; i++) if (data[i] != prefix[i]) return false;
            return true;
        }

        static TokenRecord CodexRecord(Dictionary<string, object> u, string session, long t, bool fallback)
        {
            var r = new TokenRecord
            {
                Session = session,
                T = t,
                Fallback = fallback,
                Input = Json.Long(u, "input_tokens") ?? 0,
                CacheRead = Json.Long(u, "cached_input_tokens") ?? 0,
                CacheWrite = Json.Long(u, "cache_write_input_tokens") ?? 0,
                Output = Json.Long(u, "output_tokens") ?? 0,
                Reasoning = Json.Long(u, "reasoning_output_tokens") ?? 0,
                Total = Json.Long(u, "total_tokens") ?? 0,
            };
            if (r.Total == 0) r.Total = r.Input + r.Output;
            return r;
        }

        public static string CodexPromptText(object o)
        {
            var p = Json.Obj(o, "payload");
            var content = Json.Arr(p, "content");
            if (content == null) return null;
            var sb = new StringBuilder();
            int images = 0;
            foreach (var c in content)
            {
                string type = Json.Str(c, "type");
                if (type == "input_text" || type == "text")
                {
                    string text = Json.Str(c, "text") ?? "";
                    string tt = text.TrimStart();
                    if (tt.StartsWith("<") || tt.StartsWith("# AGENTS.md")) continue;
                    // 添付ファイルがあると「## My request for Codex:」の後ろが本人の依頼
                    int idx = text.IndexOf("## My request for Codex:", StringComparison.Ordinal);
                    if (idx >= 0) text = text.Substring(idx + "## My request for Codex:".Length);
                    else if (tt.StartsWith("# Files mentioned by the user")) continue;
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(text.Trim());
                }
                else if (type == "input_image") images++;
            }
            if (sb.Length == 0 && images > 0) return "［画像 " + images + "枚］";
            return sb.Length == 0 ? null : sb.ToString();
        }

        // ------------------------------------------------------------------ 集計（表示用）

        public TokenSum DayTotal(Tool tool, DateTime localDay)
        {
            long from = Fmt.ToUnixMs(localDay.Date.ToUniversalTime());
            long to = Fmt.ToUnixMs(localDay.Date.AddDays(1).ToUniversalTime());
            var sum = new TokenSum();
            lock (gate)
            {
                var realCodex = CodexSessionsWithRealRecords();
                foreach (var r in records.Values)
                {
                    if (r.T < from || r.T >= to) continue;
                    SessionInfo s;
                    if (!sessions.TryGetValue(r.Session, out s) || s.Tool != tool) continue;
                    if (r.Fallback && realCodex.Contains(r.Session)) continue;
                    sum.Add(r);
                }
            }
            return sum;
        }

        HashSet<string> CodexSessionsWithRealRecords()
        {
            return new HashSet<string>(records.Values.Where(r => !r.Fallback && r.Session.StartsWith("codex:")).Select(r => r.Session));
        }

        public bool HasAnyRecords(Tool tool)
        {
            lock (gate) return sessions.Values.Any(s => s.Tool == tool);
        }

        public class SessionView
        {
            public SessionInfo Info;
            public TokenSum Tokens = new TokenSum();      // この日の合計（サブエージェント含む）
            public TokenSum OwnTokens = new TokenSum();
            public long First, Last;                     // この日の最初と最後の記録
            public int PromptsInDay;
            public List<SessionInfo> Subagents = new List<SessionInfo>();
            public bool Estimated;
        }

        /// <summary>指定日に動きのあったセッション（サブエージェントは親にまとめる）</summary>
        public List<SessionView> SessionsForDay(DateTime localDay)
        {
            long from = Fmt.ToUnixMs(localDay.Date.ToUniversalTime());
            long to = Fmt.ToUnixMs(localDay.Date.AddDays(1).ToUniversalTime());
            var views = new Dictionary<string, SessionView>();
            lock (gate)
            {
                var realCodex = CodexSessionsWithRealRecords();
                Func<string, SessionView> viewFor = key =>
                {
                    SessionInfo s;
                    if (!sessions.TryGetValue(key, out s)) return null;
                    string rootKey = key;
                    SessionInfo root = s;
                    int guard = 0;
                    while (root.Parent != null && guard++ < 8)
                    {
                        SessionInfo p;
                        if (!sessions.TryGetValue(root.Parent, out p)) break;
                        root = p;
                    }
                    SessionView v;
                    if (!views.TryGetValue(root.Key, out v))
                    {
                        v = new SessionView { Info = root };
                        views[root.Key] = v;
                    }
                    if (root != s && !v.Subagents.Contains(s)) v.Subagents.Add(s);
                    return v;
                };

                foreach (var r in records.Values)
                {
                    if (r.T < from || r.T >= to) continue;
                    if (r.Fallback && realCodex.Contains(r.Session)) continue;
                    var v = viewFor(r.Session);
                    if (v == null) continue;
                    v.Tokens.Add(r);
                    if (r.Session == v.Info.Key) v.OwnTokens.Add(r);
                    if (r.Fallback) v.Estimated = true;
                    if (v.First == 0 || r.T < v.First) v.First = r.T;
                    if (r.T > v.Last) v.Last = r.T;
                }
                foreach (var s in sessions.Values)
                {
                    foreach (var p in s.Prompts)
                    {
                        if (p.T < from || p.T >= to) continue;
                        var v = viewFor(s.Key);
                        if (v == null) continue;
                        if (s.Key == v.Info.Key) v.PromptsInDay++;
                        if (v.First == 0 || p.T < v.First) v.First = p.T;
                        if (p.T > v.Last) v.Last = p.T;
                    }
                }
            }
            return views.Values.OrderByDescending(v => v.Last).ToList();
        }

        public List<LineRef> PromptsForDay(SessionInfo s, DateTime localDay)
        {
            long from = Fmt.ToUnixMs(localDay.Date.ToUniversalTime());
            long to = Fmt.ToUnixMs(localDay.Date.AddDays(1).ToUniversalTime());
            lock (gate) return s.Prompts.Where(p => p.T >= from && p.T < to).OrderBy(p => p.T).ToList();
        }

        public LineRef FirstPrompt(SessionInfo s)
        {
            lock (gate) return s.Prompts.OrderBy(p => p.T).FirstOrDefault();
        }

        // ---------- 本文の読み取り（表示時のみ。キャッシュしない） ----------

        public string ReadPrompt(Tool tool, LineRef r)
        {
            if (r == null || !IsAllowedLogFile(r.Path)) return null;
            string line = LineReader.ReadLineAt(r.Path, r.Offset, 2 * 1024 * 1024);
            if (line == null) return "［大きな添付を含む依頼］";
            var o = Json.TryParse(line);
            if (o == null) return null;
            return tool == Tool.ClaudeCode ? ClaudePromptText(o) : CodexPromptText(o);
        }

        public string ReadTitle(SessionInfo s, Dictionary<string, string> codexIndex)
        {
            if (s.Tool == Tool.Codex)
            {
                string name;
                if (codexIndex != null && codexIndex.TryGetValue(s.Id, out name) && !string.IsNullOrWhiteSpace(name)) return name;
                return null;
            }
            foreach (var r in new[] { s.CustomTitle, s.AiTitle })
            {
                if (r == null || !IsAllowedLogFile(r.Path)) continue;
                var o = Json.TryParse(LineReader.ReadLineAt(r.Path, r.Offset, 64 * 1024) ?? "");
                string t = Json.Str(o, "customTitle") ?? Json.Str(o, "aiTitle");
                if (!string.IsNullOrWhiteSpace(t)) return t;
            }
            return null;
        }

        public Dictionary<string, string> ReadCodexIndex()
        {
            var map = new Dictionary<string, string>();
            string path = Path.Combine(paths.CodexHome, "session_index.jsonl");
            try
            {
                if (!File.Exists(path)) return map;
                foreach (var line in FileUtil.ReadShared(path).Split('\n'))
                {
                    var o = Json.TryParse(line);
                    string id = Json.Str(o, "id");
                    string name = Json.Str(o, "thread_name");
                    if (id != null && name != null) map[id] = name; // 後の行が新しい
                }
            }
            catch { }
            return map;
        }

        // ------------------------------------------------------------------ キャッシュ

        public void Load()
        {
            try
            {
                if (!File.Exists(CachePath)) return;
                var o = Json.Parse(FileUtil.ReadShared(CachePath));
                if (Json.Long(o, "version") != CacheVersion) return;
                var nf = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
                var ns = new Dictionary<string, SessionInfo>();
                var nr = new Dictionary<string, TokenRecord>();

                var fo = Json.Obj(o, "files");
                if (fo != null) foreach (var kv in fo)
                    nf[kv.Key] = new FileState
                    {
                        Size = Json.Long(kv.Value, "size") ?? 0,
                        MTime = Json.Long(kv.Value, "mtime") ?? 0,
                        Offset = Json.Long(kv.Value, "offset") ?? 0,
                        Session = Json.Str(kv.Value, "session"),
                        LastTotal = Json.Long(kv.Value, "lastTotal") ?? 0,
                    };

                var so = Json.Obj(o, "sessions");
                if (so != null) foreach (var kv in so)
                {
                    var v = kv.Value;
                    var s = new SessionInfo
                    {
                        Key = kv.Key,
                        Tool = Json.Str(v, "tool") == "codex" ? Tool.Codex : Tool.ClaudeCode,
                        Id = Json.Str(v, "id"),
                        Cwd = Json.Str(v, "cwd"),
                        Start = Json.Long(v, "start") ?? 0,
                        End = Json.Long(v, "end") ?? 0,
                        Originator = Json.Str(v, "originator"),
                        ThreadSource = Json.Str(v, "threadSource"),
                        Parent = Json.Str(v, "parent"),
                        Nickname = Json.Str(v, "nickname"),
                        Model = Json.Str(v, "model"),
                        Entrypoint = Json.Str(v, "entrypoint"),
                        OwnerAccount = Json.Str(v, "ownerAccount"),
                        CustomTitle = RefFrom(Json.Obj(v, "customTitle")),
                        AiTitle = RefFrom(Json.Obj(v, "aiTitle")),
                    };
                    var ps = Json.Arr(v, "prompts");
                    if (ps != null) foreach (var p in ps) { var r = RefFrom(p); if (r != null) s.Prompts.Add(r); }
                    var fl = Json.Arr(v, "files");
                    if (fl != null) foreach (var f in fl) if (f is string) s.Files.Add((string)f);
                    ns[kv.Key] = s;
                }

                var ro = Json.Obj(o, "records");
                if (ro != null) foreach (var kv in ro)
                {
                    var a = kv.Value as List<object>;
                    if (a == null || a.Count < 11) continue;
                    nr[kv.Key] = new TokenRecord
                    {
                        Session = a[0] as string,
                        T = ToLong(a[1]), Input = ToLong(a[2]), Output = ToLong(a[3]), CacheRead = ToLong(a[4]),
                        CacheWrite = ToLong(a[5]), Reasoning = ToLong(a[6]), Total = ToLong(a[7]), Fallback = ToLong(a[8]) != 0,
                        CacheWrite1h = ToLong(a[9]), Model = a[10] as string,
                    };
                }
                var nh = new Dictionary<string, DailyUsage>();
                var ho = Json.Arr(o, "history");
                if (ho != null) foreach (var item in ho)
                {
                    var a = item as List<object>;
                    if (a == null || a.Count < 11 || !(a[0] is string)) continue;
                    var d = new DailyUsage { Date = (string)a[0], Tool = (a[1] as string) == "codex" ? Tool.Codex : Tool.ClaudeCode, Model = a[2] as string };
                    d.Sum.Input = ToLong(a[3]); d.Sum.Output = ToLong(a[4]); d.Sum.CacheRead = ToLong(a[5]); d.Sum.CacheWrite = ToLong(a[6]);
                    d.Sum.CacheWrite1h = ToLong(a[7]); d.Sum.Reasoning = ToLong(a[8]); d.Sum.Total = ToLong(a[9]); d.Sum.Responses = (int)ToLong(a[10]);
                    nh[d.Date + "|" + (d.Tool == Tool.Codex ? "codex" : "claude") + "|" + (d.Model ?? "")] = d;
                }
                foreach (var k in nr.Where(kv => !Fmt.ValidUnixMs(kv.Value.T)).Select(kv => kv.Key).ToList()) nr.Remove(k);
                if (!CollectActivity) foreach (var x in ns.Values) StripActivity(x);
                lock (gate) { files = nf; sessions = ns; records = nr; history = nh; }
            }
            catch
            {
                // 壊れたキャッシュは使わず、読み直す
            }
        }

        static long ToLong(object o)
        {
            if (o is long) return (long)o;
            if (o is double) return (long)(double)o;
            return 0;
        }

        static LineRef RefFrom(object o)
        {
            if (o == null) return null;
            string p = Json.Str(o, "p");
            if (p == null) return null;
            return new LineRef { Path = p, Offset = Json.Long(o, "o") ?? 0, T = Json.Long(o, "t") ?? 0, Source = Json.Str(o, "s"), Hash = Json.Str(o, "h") };
        }

        static object RefTo(LineRef r)
        {
            if (r == null) return null;
            var d = new Dictionary<string, object> { { "p", r.Path }, { "o", r.Offset }, { "t", r.T } };
            if (r.Source != null) d["s"] = r.Source;
            if (r.Hash != null) d["h"] = r.Hash;
            return d;
        }

        public void Save()
        {
            string text;
            lock (gate)
            {
                var d = new Dictionary<string, object>();
                d["version"] = CacheVersion;
                d["note"] = "AI Usage Bar の集計キャッシュ。使用量・時刻・識別子・作業フォルダ・ログ内の位置・依頼文のHMAC（重複判定用）のみを保存し、会話本文や認証情報は含めません。";
                var fo = new Dictionary<string, object>();
                foreach (var kv in files)
                    fo[kv.Key] = new Dictionary<string, object> { { "size", kv.Value.Size }, { "mtime", kv.Value.MTime }, { "offset", kv.Value.Offset }, { "session", kv.Value.Session }, { "lastTotal", kv.Value.LastTotal } };
                d["files"] = fo;
                var so = new Dictionary<string, object>();
                foreach (var s in sessions.Values)
                {
                    so[s.Key] = new Dictionary<string, object> {
                        { "tool", s.Tool == Tool.Codex ? "codex" : "claude" }, { "id", s.Id }, { "cwd", s.Cwd },
                        { "start", s.Start }, { "end", s.End }, { "originator", s.Originator }, { "threadSource", s.ThreadSource },
                        { "parent", s.Parent }, { "nickname", s.Nickname }, { "model", s.Model },
                        { "entrypoint", s.Entrypoint }, { "ownerAccount", s.OwnerAccount },
                        { "customTitle", RefTo(s.CustomTitle) }, { "aiTitle", RefTo(s.AiTitle) },
                        { "prompts", s.Prompts.Select(RefTo).ToList() }, { "files", s.Files.ToList() } };
                }
                d["sessions"] = so;
                var ro = new Dictionary<string, object>();
                foreach (var kv in records)
                {
                    var r = kv.Value;
                    ro[kv.Key] = new List<object> { r.Session, r.T, r.Input, r.Output, r.CacheRead, r.CacheWrite, r.Reasoning, r.Total, r.Fallback ? 1 : 0, r.CacheWrite1h, r.Model };
                }
                d["records"] = ro;
                d["history"] = history.Values.Select(h => (object)new List<object> {
                    h.Date, h.Tool == Tool.Codex ? "codex" : "claude", h.Model, h.Sum.Input, h.Sum.Output, h.Sum.CacheRead, h.Sum.CacheWrite,
                    h.Sum.CacheWrite1h, h.Sum.Reasoning, h.Sum.Total, h.Sum.Responses }).ToList();
                text = Json.Serialize(d);
            }
            try
            {
                Hardening.SecureDirectory(paths.DataDir);
                FileUtil.WriteAtomic(CachePath, text);
                // 旧版のキャッシュ（素のハッシュを含む）を消す
                foreach (var old in new[] { "local-logs-v1.json", "local-logs-v2.json", "local-logs-v3.json" })
                {
                    string op = Path.Combine(paths.DataDir, old);
                    if (File.Exists(op)) File.Delete(op);
                }
            }
            catch { }
        }

        public int SessionCount { get { lock (gate) return sessions.Count; } }
        public int RecordCount { get { lock (gate) return records.Count; } }
    }
}
