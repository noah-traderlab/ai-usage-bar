using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace AIUsageBar
{
    public enum Level { Unknown, Good, Warn, Danger }

    public class DetectedItem
    {
        public string Name;
        public bool Found;
        public string Detail;
    }

    /// <summary>取得・集計・通知判定をまとめる。UIスレッド以外から呼ばれても安全。</summary>
    public class UsageStore
    {
        public readonly AppPaths Paths;
        public readonly Settings Settings;
        public readonly LocalLogs Logs;
        public Policy Policy = new Policy();

        public const string MsgCodexPolicy = "組織のポリシーでCodexの取得は無効になっています";

        /// <summary>詳細ログを使えるか（設定とポリシーの両方）</summary>
        public bool ActivityAllowed { get { return Settings.ActivityEnabled && !Policy.DisableActivityLog; } }
        /// <summary>依頼文・タイトルを表示・書き出ししてよいか</summary>
        public bool PromptsAllowed { get { return ActivityAllowed && Settings.ShowPrompts && !Policy.HidePromptText; } }
        public bool ExportAllowed { get { return ActivityAllowed && !Policy.DisableExport; } }

        readonly SynchronizationContext ui;
        readonly object gate = new object();

        public string CodexCliPath;
        public CodexSnapshot Codex;
        public string CodexError;
        public bool CodexFetching;
        public DateTime? CodexAttemptUtc;

        public ClaudeSnapshot Claude;
        public string ClaudeError;

        public bool Scanning;
        public long ScanDone, ScanTotal;
        public string ScanError;

        public event Action Changed;
        public event Action<string, string> Notify;
        /// <summary>お知らせ（リセット通知など。警告ではない）</summary>
        public event Action<string, string> Info;
        public readonly ResetWatcher Resets = new ResetWatcher();

        int scanRunning;
        int codexRunning;
        readonly Dictionary<string, bool> notified = new Dictionary<string, bool>();

        public UsageStore(AppPaths paths, Settings settings, SynchronizationContext ui)
        {
            Paths = paths;
            Settings = settings;
            this.ui = ui;
            Logs = new LocalLogs(paths);
        }

        void RaiseChanged()
        {
            var h = Changed;
            if (h == null) return;
            if (ui != null) ui.Post(_ => h(), null); else h();
        }

        // ------------------------------------------------------------------ Codex

        public void LoadCodexCache()
        {
            if (Policy.DisableCodexFetch) { CodexError = MsgCodexPolicy; return; }
            try
            {
                string path = Path.Combine(Paths.DataDir, "codex-snapshot.json");
                if (File.Exists(path)) Codex = CodexSnapshot.FromJson(Json.Parse(FileUtil.ReadShared(path)));
                // 前回のリセット時刻を覚えるだけ（起動時にすでに過ぎていた分は知らせない）
                Resets.CheckCodex(Codex, DateTime.UtcNow, ResetWatcher.ModeOff, 0);
            }
            catch { }
        }

        public void RefreshCodexAsync()
        {
            if (Interlocked.CompareExchange(ref codexRunning, 1, 0) != 0) return;
            CodexFetching = true;
            RaiseChanged();
            var th = new Thread(() =>
            {
                try { RefreshCodex(); }
                finally { Interlocked.Exchange(ref codexRunning, 0); CodexFetching = false; RaiseChanged(); }
            });
            th.IsBackground = true;
            th.Name = "codex-refresh";
            th.Start();
        }

        public void RefreshCodex()
        {
            CodexAttemptUtc = DateTime.UtcNow;
            if (Policy.DisableCodexFetch) { Codex = null; CodexCliPath = null; CodexError = MsgCodexPolicy; return; }
            CodexCliPath = CodexClient.FindCli(Paths);
            try
            {
                try { Hardening.SecureDirectory(Paths.DataDir); } catch { }
                var snap = CodexClient.Fetch(CodexCliPath, TimeSpan.FromSeconds(45), Path.Combine(Paths.DataDir, "codex-work"));
                // 履歴だけ失敗したときは前回の履歴を残す
                if (snap.Daily == null && Codex != null && Codex.Daily != null) snap.Daily = Codex.Daily;
                Codex = snap;
                CodexError = null;
                try { FileUtil.WriteAtomic(Path.Combine(Paths.DataDir, "codex-snapshot.json"), Json.Serialize(snap.ToJson(), true)); } catch { }
                RaiseResets(Resets.CheckCodex(snap, DateTime.UtcNow, Settings.ResetNotify, Settings.NotificationThreshold));
                // 保存しないメールアドレスは、前回値を引き継がない（今回取得できた値だけ使う）
                CheckNotify();
            }
            catch (CodexException ex) { CodexError = ex.Message; }
            catch (Exception) { CodexError = CodexClient.MsgFailed; }
        }

        /// <summary>取得情報が古いか（更新間隔の2倍＋2分、最低15分）</summary>
        public bool CodexStale(DateTime nowUtc)
        {
            if (Codex == null) return true;
            double maxMin = Math.Max(15, Settings.CodexIntervalMinutes * 2 + 2);
            return (nowUtc - Codex.ObservedAtUtc).TotalMinutes > maxMin;
        }

        public static bool WindowExpired(QuotaWindow w, DateTime nowUtc)
        {
            return w.ResetsAtUtc != null && w.ResetsAtUtc.Value <= nowUtc;
        }

        public double? CodexMinRemaining(DateTime nowUtc)
        {
            if (Codex == null || Codex.Main == null || CodexStale(nowUtc)) return null;
            var ws = Codex.Main.Windows.Where(w => !WindowExpired(w, nowUtc)).ToList();
            if (ws.Count == 0) return null;
            return ws.Min(w => w.RemainingPercent);
        }

        public string CodexMinLabel(DateTime nowUtc)
        {
            if (Codex == null || Codex.Main == null) return null;
            var w = Codex.Main.Windows.Where(x => !WindowExpired(x, nowUtc)).OrderBy(x => x.RemainingPercent).FirstOrDefault();
            return w == null ? null : w.Label;
        }

        // ------------------------------------------------------------------ Claude

        public void RefreshClaude()
        {
            string err;
            var snap = ClaudeUsage.Read(Paths, out err);
            var before = Claude;
            if (snap != null) { Claude = snap; ClaudeError = null; RaiseResets(Resets.CheckClaude(before, snap, DateTime.UtcNow, Settings.ResetNotify, Settings.NotificationThreshold)); }
            else { ClaudeError = err; if (err == ClaudeUsage.MsgNoFile) Claude = null; }
            CheckNotify();
            RaiseChanged();
        }

        public double? ClaudeMinRemaining(DateTime nowUtc)
        {
            if (Claude == null || Claude.IsStale(nowUtc, Settings.ClaudeIntervalMinutes)) return null;
            return Claude.MinRemaining;
        }

        // ------------------------------------------------------------------ ローカル記録

        public void ScanLogsAsync(bool loadCacheFirst)
        {
            Logs.ApplyActivityCollection(!Policy.DisableActivityLog);
            if (Interlocked.CompareExchange(ref scanRunning, 1, 0) != 0) return;
            Scanning = true;
            RaiseChanged();
            var th = new Thread(() =>
            {
                var lastRaise = DateTime.MinValue;
                try
                {
                    if (loadCacheFirst) { Logs.Load(); RaiseChanged(); }
                    Logs.Scan((done, total) =>
                    {
                        ScanDone = done; ScanTotal = total;
                        if ((DateTime.UtcNow - lastRaise).TotalMilliseconds > 400) { lastRaise = DateTime.UtcNow; RaiseChanged(); }
                    });
                    Logs.Save();
                    ScanError = null;
                }
                catch (Exception ex) { ScanError = "一部の記録を読み取れず、集計に含められていません（" + ex.GetType().Name + "）"; }
                finally
                {
                    Scanning = false;
                    Interlocked.Exchange(ref scanRunning, 0);
                    RaiseChanged();
                }
            });
            th.IsBackground = true;
            th.Priority = ThreadPriority.BelowNormal;
            th.Name = "log-scan";
            th.Start();
        }

        // ------------------------------------------------------------------ 状態

        /// <summary>1分ごとに呼ぶ：Codex のリセット時刻を過ぎた枠を知らせる（再取得を待たない）</summary>
        public void CheckResets()
        {
            RaiseResets(Resets.CheckCodex(Codex, DateTime.UtcNow, Settings.ResetNotify, Settings.NotificationThreshold));
        }

        void RaiseResets(List<ResetNotice> notices)
        {
            var h = Info;
            if (h == null || notices == null) return;
            foreach (var n in notices)
            {
                var title = n.Title; var text = n.Text;
                if (ui != null) ui.Post(_ => h(title, text), null); else h(title, text);
            }
        }

        public static Level LevelFor(double? remaining)
        {
            if (remaining == null) return Level.Unknown;
            if (remaining.Value <= 10) return Level.Danger;
            if (remaining.Value <= 20) return Level.Warn;
            return Level.Good;
        }

        void CheckNotify()
        {
            if (!Settings.NotificationsEnabled) return;
            var now = DateTime.UtcNow;
            var items = new List<Tuple<string, string, double?>>();
            if (Codex != null && Codex.Main != null && !CodexStale(now))
                foreach (var w in Codex.Main.Windows)
                    if (!WindowExpired(w, now)) items.Add(Tuple.Create("codex:" + w.WindowMinutes, "Codex（" + w.Label + "）", (double?)w.RemainingPercent));
            if (Claude != null && !Claude.IsStale(now, Settings.ClaudeIntervalMinutes))
            {
                items.Add(Tuple.Create("claude:5h", "Claude（5時間）", Claude.FiveHourRemaining));
                items.Add(Tuple.Create("claude:week", "Claude（週間）", Claude.WeeklyRemaining));
            }
            foreach (var it in items)
            {
                if (it.Item3 == null) continue;
                bool low = it.Item3.Value <= Settings.NotificationThreshold;
                bool was;
                notified.TryGetValue(it.Item1, out was);
                if (low && !was)
                {
                    notified[it.Item1] = true;
                    var h = Notify;
                    string title = it.Item2 + "の残りが少なくなっています";
                    string text = "残り" + Math.Round(it.Item3.Value) + "%（通知する残量：" + Settings.NotificationThreshold + "%以下）";
                    if (h != null) { if (ui != null) ui.Post(_ => h(title, text), null); else h(title, text); }
                }
                else if (!low && was) notified[it.Item1] = false;
            }
        }

        public List<DetectedItem> Detect()
        {
            var list = new List<DetectedItem>();
            string cli = Policy.DisableCodexFetch ? null : (CodexCliPath ?? CodexClient.FindCli(Paths));
            list.Add(new DetectedItem { Name = "Codex CLI", Found = cli != null, Detail = cli });
            list.Add(new DetectedItem { Name = "ChatGPTアプリ", Found = PackageExists("OpenAI.ChatGPT-Desktop_*") || Directory.Exists(Path.Combine(Paths.LocalAppData, "Programs", "ChatGPT")) });
            list.Add(new DetectedItem { Name = "Codexアプリ", Found = PackageExists("OpenAI.Codex_*") || Directory.Exists(Path.Combine(Paths.LocalAppData, "OpenAI", "Codex")) });
            list.Add(new DetectedItem { Name = "Claudeアプリ", Found = PackageExists("Claude_*") || Directory.Exists(Path.Combine(Paths.LocalAppData, "AnthropicClaude")) || ClaudeUsage.FindHistory(Paths) != null });
            list.Add(new DetectedItem { Name = "Claude Code", Found = Directory.Exists(Paths.ClaudeProjects), Detail = Paths.ClaudeProjects });
            return list;
        }

        bool PackageExists(string pattern)
        {
            try
            {
                string packages = Path.Combine(Paths.LocalAppData, "Packages");
                return Directory.Exists(packages) && Directory.GetDirectories(packages, pattern).Length > 0;
            }
            catch { return false; }
        }
    }
}
