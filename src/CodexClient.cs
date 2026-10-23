using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace AIUsageBar
{
    public class QuotaWindow
    {
        public string Label;          // 5時間 / 週間
        public double UsedPercent;
        public long? WindowMinutes;
        public DateTime? ResetsAtUtc;

        public double RemainingPercent { get { return Math.Max(0, Math.Min(100, 100 - UsedPercent)); } }
    }

    public class QuotaGroup
    {
        public string LimitId;
        public string Title;
        public List<QuotaWindow> Windows = new List<QuotaWindow>();
    }

    public class DailyTokens
    {
        public string Date; // yyyy-MM-dd（サービスの集計日）
        public long Tokens;
    }

    public class CodexSnapshot
    {
        public DateTime ObservedAtUtc;
        public string PlanType;
        public string AccountType;   // chatgpt / apiKey など
        public string AccountEmail;
        public bool? OrdinaryUsageAllowed;
        public QuotaGroup Main;                     // limitId = codex
        public List<QuotaGroup> Others = new List<QuotaGroup>();
        public List<DailyTokens> Daily;             // null = トークン履歴は未取得
        public string HistoryError;

        public Dictionary<string, object> ToJson()
        {
            var d = new Dictionary<string, object>();
            d["observedAt"] = Fmt.ToUnixMs(ObservedAtUtc);
            d["planType"] = PlanType;
            d["accountType"] = AccountType;
            // メールアドレスは保存しない（取得のたびにメモリ上でだけ保持）
            d["ordinaryUsageAllowed"] = OrdinaryUsageAllowed;
            d["main"] = GroupToJson(Main);
            d["others"] = Others.Select(g => (object)GroupToJson(g)).ToList();
            if (Daily != null)
                d["daily"] = Daily.Select(x => (object)new Dictionary<string, object> { { "date", x.Date }, { "tokens", x.Tokens } }).ToList();
            return d;
        }

        static object GroupToJson(QuotaGroup g)
        {
            if (g == null) return null;
            var d = new Dictionary<string, object>();
            d["limitId"] = g.LimitId;
            d["title"] = g.Title;
            d["windows"] = g.Windows.Select(w => (object)new Dictionary<string, object> {
                { "label", w.Label }, { "usedPercent", w.UsedPercent }, { "windowMinutes", w.WindowMinutes },
                { "resetsAt", w.ResetsAtUtc == null ? (object)null : Fmt.ToUnixMs(w.ResetsAtUtc.Value) } }).ToList();
            return d;
        }

        public static CodexSnapshot FromJson(object o)
        {
            if (o == null) return null;
            var s = new CodexSnapshot();
            long? t = Json.Long(o, "observedAt");
            if (t == null) return null;
            s.ObservedAtUtc = Fmt.FromUnixMs(t.Value);
            s.PlanType = Json.Str(o, "planType");
            s.AccountType = Json.Str(o, "accountType");
            s.OrdinaryUsageAllowed = Json.Bool(o, "ordinaryUsageAllowed");
            s.Main = GroupFromJson(Json.Obj(o, "main"));
            var others = Json.Arr(o, "others");
            if (others != null) foreach (var g in others) { var q = GroupFromJson(g); if (q != null) s.Others.Add(q); }
            var daily = Json.Arr(o, "daily");
            if (daily != null)
            {
                s.Daily = new List<DailyTokens>();
                foreach (var x in daily) s.Daily.Add(new DailyTokens { Date = Json.Str(x, "date"), Tokens = Json.Long(x, "tokens") ?? 0 });
            }
            return s;
        }

        static QuotaGroup GroupFromJson(object o)
        {
            if (o == null) return null;
            var g = new QuotaGroup { LimitId = Json.Str(o, "limitId"), Title = Json.Str(o, "title") };
            var ws = Json.Arr(o, "windows");
            if (ws != null)
                foreach (var w in ws)
                {
                    long? r = Json.Long(w, "resetsAt");
                    if (r != null && !Fmt.ValidUnixMs(r.Value)) r = null;
                    g.Windows.Add(new QuotaWindow
                    {
                        Label = Json.Str(w, "label"),
                        UsedPercent = Json.Num(w, "usedPercent") ?? 0,
                        WindowMinutes = Json.Long(w, "windowMinutes"),
                        ResetsAtUtc = r == null ? (DateTime?)null : Fmt.FromUnixMs(r.Value)
                    });
                }
            return g;
        }
    }

    public class CodexException : Exception
    {
        public CodexException(string message) : base(message) { }
    }

    /// <summary>
    /// Codex CLI の app-server（JSON-RPC over stdio）から利用枠とトークン履歴を読む。
    /// 生成リクエストは送らない。
    /// </summary>
    public static class CodexClient
    {
        public const string MsgNotFound = "Codex CLIが見つかりません";
        public const string MsgNeedLogin = "ChatGPTアカウントでのCodexログインが必要です";
        public const string MsgFailed = "Codexから取得できませんでした。ログイン・通信状態をご確認ください";
        public const string MsgClosed = "Codexへの接続が終了しました。CLIのログイン状態をご確認ください";
        public const string MsgTimeout = "Codexへの接続がタイムアウトしました";
        public const string MsgOldCli = "Codex CLIが古いため利用枠を取得できません。Codexを最新版に更新してください";

        /// <summary>Codex CLI を探す。PATH → ~/.local/bin → npm → Codexアプリ同梱版。</summary>
        public static string FindCli(AppPaths paths)
        {
            var candidates = new List<string>();
            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var raw in pathEnv.Split(';'))
            {
                string dir = raw.Trim().Trim('"');
                // 「.」などの相対パスは、カレントフォルダ（Downloadsなど）を指すので使わない
                if (string.IsNullOrWhiteSpace(dir) || Hardening.IsUncPath(dir)) continue;
                try
                {
                    if (!Path.IsPathRooted(dir) || Path.GetPathRoot(dir).Length < 3) continue;
                    candidates.Add(Path.Combine(dir, "codex.exe"));
                    candidates.Add(Path.Combine(dir, "codex.cmd"));
                }
                catch { }
            }
            candidates.Add(Path.Combine(paths.Home, ".local", "bin", "codex.exe"));
            candidates.Add(Path.Combine(paths.RoamingAppData, "npm", "codex.cmd"));
            foreach (var c in candidates)
            {
                try { if (File.Exists(c)) return ResolveNpmShim(c); } catch { }
            }
            // Codex デスクトップアプリに同梱されたCLI（最新のもの）
            try
            {
                string bin = Path.Combine(paths.LocalAppData, "OpenAI", "Codex", "bin");
                if (Directory.Exists(bin))
                {
                    var found = Directory.GetDirectories(bin)
                        .Select(d => Path.Combine(d, "codex.exe"))
                        .Where(File.Exists)
                        .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                        .FirstOrDefault();
                    if (found != null) return found;
                }
            }
            catch { }
            return null;
        }

        /// <summary>npmのcodex.cmdなら、同梱されているネイティブのcodex.exeを直接使う</summary>
        static string ResolveNpmShim(string path)
        {
            if (!path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)) return path;
            try
            {
                string pkg = Path.Combine(Path.GetDirectoryName(path), "node_modules", "@openai", "codex");
                if (Directory.Exists(pkg))
                {
                    var exe = Directory.GetFiles(pkg, "codex.exe", SearchOption.AllDirectories)
                        .OrderByDescending(f => File.GetLastWriteTimeUtc(f)).FirstOrDefault();
                    if (exe != null) return exe;
                }
            }
            catch { }
            return path;
        }

        public static CodexSnapshot Fetch(string cliPath, TimeSpan timeout, string workDir)
        {
            if (cliPath == null || !File.Exists(cliPath)) throw new CodexException(MsgNotFound);
            // cmd.exe 経由では %VAR% が展開されるため、% を含むパスは使わない
            if (cliPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) && cliPath.IndexOf('%') >= 0) throw new CodexException(MsgNotFound);

            var psi = new ProcessStartInfo();
            string args = "-c analytics.enabled=false app-server";
            if (cliPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
            {
                psi.FileName = Hardening.CmdPath;
                psi.Arguments = "/d /s /c \"\"" + cliPath + "\" " + args + "\"";
            }
            else
            {
                psi.FileName = cliPath;
                psi.Arguments = args;
            }
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            // 空の専用フォルダで起動し、git が上位フォルダのリポジトリ設定を読まないようにする
            if (workDir != null)
            {
                try { Directory.CreateDirectory(workDir); } catch { }
                psi.WorkingDirectory = workDir;
                psi.EnvironmentVariables["GIT_CEILING_DIRECTORIES"] = Path.GetDirectoryName(workDir.TrimEnd('\\'));
            }
            else psi.WorkingDirectory = Path.GetTempPath();

            var responses = new Dictionary<long, object>();
            var gate = new object();
            bool exited = false;
            var stderr = new StringBuilder();

            Process proc;
            PreventStdinBom();
            try { proc = Process.Start(psi); }
            catch (Exception) { throw new CodexException(MsgNotFound); }
            Hardening.AttachToKillOnCloseJob(proc); // アプリが強制終了されても子プロセスを残さない

            try
            {
                proc.OutputDataReceived += (s, e) =>
                {
                    if (e.Data == null) { lock (gate) { exited = true; Monitor.PulseAll(gate); } return; }
                    var msg = Json.TryParse(e.Data);
                    long? id = Json.Long(msg, "id");
                    if (id != null && (Json.Has(msg, "result") || Json.Has(msg, "error")))
                        lock (gate) { responses[id.Value] = msg; Monitor.PulseAll(gate); }
                };
                proc.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (stderr) { if (stderr.Length < 4000) stderr.AppendLine(e.Data); } };
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                var stdin = new StreamWriter(proc.StandardInput.BaseStream, new UTF8Encoding(false));
                stdin.NewLine = "\n";
                stdin.AutoFlush = true;
                DateTime deadline = DateTime.UtcNow + timeout;

                Send(stdin, new Dictionary<string, object> {
                    { "jsonrpc", "2.0" }, { "id", 1L }, { "method", "initialize" },
                    { "params", new Dictionary<string, object> {
                        { "clientInfo", new Dictionary<string, object> { { "name", AppInfo.ClientName }, { "title", AppInfo.Name }, { "version", AppInfo.Version } } },
                        { "capabilities", null } } } });
                var init = Wait(responses, gate, 1, deadline, () => exited);
                if (Json.Has(init, "error")) throw new CodexException(MsgFailed);

                Send(stdin, new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "method", "initialized" } });
                Send(stdin, new Dictionary<string, object> {
                    { "jsonrpc", "2.0" }, { "id", 2L }, { "method", "account/rateLimits/read" },
                    { "params", new Dictionary<string, object> { { "excludeResetCreditDetails", true } } } });
                Send(stdin, new Dictionary<string, object> {
                    { "jsonrpc", "2.0" }, { "id", 3L }, { "method", "account/usage/read" },
                    { "params", new Dictionary<string, object>() } });
                Send(stdin, new Dictionary<string, object> {
                    { "jsonrpc", "2.0" }, { "id", 4L }, { "method", "account/read" },
                    { "params", new Dictionary<string, object> { { "refreshToken", false } } } });

                var limits = Wait(responses, gate, 2, deadline, () => exited);
                if (Json.Has(limits, "error"))
                {
                    long code = Json.Long(Json.Obj(limits, "error"), "code") ?? 0;
                    if (code == -32601) throw new CodexException(MsgOldCli); // メソッドがない
                    if (code == -32602 || code == -32600)
                    {
                        // 古いCLIは追加パラメーターを受け付けないことがあるので、パラメーターなしで再試行
                        Send(stdin, new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", 5L }, { "method", "account/rateLimits/read" } });
                        limits = Wait(responses, gate, 5, deadline, () => exited);
                    }
                }
                if (Json.Has(limits, "error"))
                {
                    string m = (Json.Str(Json.Obj(limits, "error"), "message") ?? "").ToLowerInvariant();
                    if (m.Contains("auth") || m.Contains("login") || m.Contains("chatgpt") || m.Contains("api key") || m.Contains("token"))
                        throw new CodexException(MsgNeedLogin);
                    throw new CodexException(MsgFailed);
                }
                var snapshot = ParseRateLimits(Json.Obj(limits, "result"), DateTime.UtcNow);
                try
                {
                    DateTime acctDeadline = DateTime.UtcNow.AddSeconds(5) < deadline ? DateTime.UtcNow.AddSeconds(5) : deadline;
                    var acct = Wait(responses, gate, 4, acctDeadline, () => exited);
                    var a = Json.Obj(Json.Obj(acct, "result"), "account");
                    snapshot.AccountType = Json.Str(a, "type");
                    snapshot.AccountEmail = Json.Str(a, "email");
                }
                catch (CodexException) { }

                // トークン履歴は取れなくても利用枠は表示する
                try
                {
                    var usage = Wait(responses, gate, 3, deadline, () => exited);
                    if (Json.Has(usage, "error")) snapshot.HistoryError = "利用枠は取得できましたが、トークン履歴は未取得です";
                    else snapshot.Daily = ParseDaily(Json.Obj(usage, "result"));
                }
                catch (CodexException)
                {
                    snapshot.HistoryError = "利用枠は取得できましたが、トークン履歴は未取得です";
                }
                return snapshot;
            }
            finally
            {
                Shutdown(proc);
            }
        }

        /// <summary>
        /// .NET Framework の Process は標準入力の書き込み口を Console.InputEncoding で作り、
        /// それがUTF-8（BOM付き）だと起動直後にBOMを送ってしまう。app-server はBOMを
        /// JSONとして読めずに終了するため、BOMなしUTF-8に差し替えてから起動する。
        /// </summary>
        public static void PreventStdinBom()
        {
            var utf8 = new UTF8Encoding(false);
            try
            {
                var f = typeof(Console).GetField("_inputEncoding", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                if (f != null) { f.SetValue(null, utf8); return; }
            }
            catch { }
            try { Console.InputEncoding = utf8; } catch { }
        }

        static void Send(StreamWriter w, object msg)
        {
            try { w.WriteLine(Json.Serialize(msg)); }
            catch (IOException) { throw new CodexException(MsgClosed); }
        }

        static object Wait(Dictionary<long, object> responses, object gate, long id, DateTime deadline, Func<bool> exited)
        {
            lock (gate)
            {
                while (true)
                {
                    object r;
                    if (responses.TryGetValue(id, out r)) return r;
                    if (exited()) throw new CodexException(MsgClosed);
                    var left = deadline - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero) throw new CodexException(MsgTimeout);
                    Monitor.Wait(gate, left);
                }
            }
        }

        static void Shutdown(Process proc)
        {
            try { proc.StandardInput.Close(); } catch { }
            try
            {
                if (!proc.WaitForExit(3000))
                {
                    // 子プロセスごと終了
                    try
                    {
                        var kill = new ProcessStartInfo(Hardening.TaskkillPath, "/T /F /PID " + proc.Id) { CreateNoWindow = true, UseShellExecute = false };
                        using (var k = Process.Start(kill)) k.WaitForExit(5000);
                    }
                    catch { }
                    try { if (!proc.HasExited) proc.Kill(); } catch { }
                }
            }
            catch { }
            try { proc.Dispose(); } catch { }
        }

        public static CodexSnapshot ParseRateLimits(object result, DateTime observedUtc)
        {
            if (result == null) throw new CodexException("対応していないデータ形式です");
            var snap = new CodexSnapshot { ObservedAtUtc = observedUtc };
            snap.OrdinaryUsageAllowed = Json.Bool(result, "ordinaryUsageAllowed");

            var byId = Json.Obj(result, "rateLimitsByLimitId");
            object main = null;
            if (byId != null && byId.ContainsKey("codex")) main = byId["codex"];
            if (main == null) main = Json.Obj(result, "rateLimits");
            if (main == null) throw new CodexException("対応していないデータ形式です");

            snap.Main = ParseGroup(main);
            snap.PlanType = Json.Str(main, "planType");
            if (byId != null)
            {
                foreach (var kv in byId.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    if (kv.Key == "codex" || kv.Value == null) continue;
                    var g = ParseGroup(kv.Value);
                    if (g.Windows.Count > 0) snap.Others.Add(g);
                }
            }
            return snap;
        }

        static QuotaGroup ParseGroup(object o)
        {
            var g = new QuotaGroup();
            g.LimitId = Json.Str(o, "limitId");
            g.Title = Json.Str(o, "limitName") ?? (g.LimitId == "codex" ? "Codex" : g.LimitId);
            foreach (var key in new[] { "primary", "secondary" })
            {
                var w = Json.Obj(o, key);
                if (w == null) continue;
                double? used = Json.Num(w, "usedPercent");
                if (used == null) continue;
                long? mins = Json.Long(w, "windowDurationMins");
                long? resets = Json.Long(w, "resetsAt");
                if (resets != null && (resets.Value < 0 || resets.Value > 253402300799L)) resets = null;
                g.Windows.Add(new QuotaWindow
                {
                    UsedPercent = used.Value,
                    WindowMinutes = mins,
                    Label = Fmt.WindowLabel(mins),
                    ResetsAtUtc = resets == null ? (DateTime?)null : Fmt.FromUnixSeconds(resets.Value)
                });
            }
            // 5時間 → 週間 の順に並べる
            g.Windows = g.Windows.OrderBy(w => w.WindowMinutes ?? long.MaxValue).ToList();
            return g;
        }

        public static List<DailyTokens> ParseDaily(object result)
        {
            var list = new List<DailyTokens>();
            var buckets = Json.Arr(result, "dailyUsageBuckets");
            if (buckets == null) return null;
            foreach (var b in buckets)
            {
                string date = Json.Str(b, "startDate");
                long? tokens = Json.Long(b, "tokens");
                if (date == null || tokens == null) continue;
                list.Add(new DailyTokens { Date = date.Length >= 10 ? date.Substring(0, 10) : date, Tokens = tokens.Value });
            }
            return list;
        }
    }
}
