using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace AIUsageBar
{
    /// <summary>タスクトレイに常駐し、定期的に取得してアイコンを更新する</summary>
    public class TrayApp : ApplicationContext
    {
        readonly UsageStore store;
        readonly Activity activity;
        readonly TeamService team;
        readonly FriendsService friends;
        readonly System.Windows.Forms.Timer friendsTimer = new System.Windows.Forms.Timer();
        TeamManagerForm manager;
        readonly System.Windows.Forms.Timer teamTimer = new System.Windows.Forms.Timer();
        readonly NotifyIcon codexIcon;
        readonly NotifyIcon claudeIcon;
        readonly System.Windows.Forms.Timer codexTimer = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer claudeTimer = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer minuteTimer = new System.Windows.Forms.Timer();
        Dashboard dashboard;
        string lastCodexKey, lastClaudeKey;
        readonly EventWaitHandle showSignal;
        RegisteredWaitHandle showWait;
        RegisteredWaitHandle quitWait;

        public TrayApp(EventWaitHandle showSignal, EventWaitHandle quitSignal = null)
        {
            this.showSignal = showSignal;
            var paths = AppPaths.Default();
            var settings = Settings.Load(paths.DataDir);
            store = new UsageStore(paths, settings, SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext());
            store.Policy = Policy.Load();
            try { Hardening.SecureDirectory(paths.DataDir); } catch { }
            // ポリシーでログイン時起動が禁止されたら、既存の登録も外す
            if (store.Policy.DisableStartupRegistration && StartupRegistration.RegisteredValue() != null) StartupRegistration.Remove();
            activity = new Activity(store);
            team = new TeamService(store, activity);
            friends = new FriendsService(store);

            var menu = new ContextMenuStrip();
            menu.Items.Add("AI Usage Barを開く", null, (s, e) => ShowDashboard(DashTab.Usage));
            menu.Items.Add("詳細ログを開く", null, (s, e) => ShowDashboard(DashTab.Log));
            menu.Items.Add("今すぐ更新", null, (s, e) => RefreshAll());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("終了", null, (s, e) => Quit());

            // 追加した順にトレイへ並ぶことが多いので Claude → Codex の順で作る
            claudeIcon = new NotifyIcon { ContextMenuStrip = menu, Visible = false };
            codexIcon = new NotifyIcon { ContextMenuStrip = menu, Visible = false };
            foreach (var icon in new[] { codexIcon, claudeIcon })
            {
                icon.MouseUp += (s, e) => { if (e.Button == MouseButtons.Left) ShowDashboard(null); };
                icon.BalloonTipClicked += (s, e) => { if (lastAiReport != null && System.IO.File.Exists(lastAiReport)) Hardening.OpenFolder(System.IO.Path.GetDirectoryName(lastAiReport), lastAiReport); };
            }

            store.Changed += UpdateIcons;
            store.Notify += (title, text) => codexIcon.ShowBalloonTip(8000, title, text, ToolTipIcon.Warning);
            store.Info += (title, text) => codexIcon.ShowBalloonTip(8000, title, text, ToolTipIcon.Info);

            // 前回の取得結果を先に読み、アイコンを「未取得」で出さない
            store.LoadCodexCache();
            store.RefreshClaude();
            UpdateIcons();
            codexIcon.Visible = true;
            claudeIcon.Visible = true;
            store.RefreshCodexAsync();
            store.ScanLogsAsync(true);

            // タスクバー表示：起動時はタスクバーに収納（設定で変更可）
            if (settings.ShowInTaskbar)
            {
                var d = EnsureDashboard();
                d.StartInTaskbar(settings.StartMinimized);
                UpdateIcons();
            }

            codexTimer.Interval = settings.CodexIntervalMinutes * 60 * 1000;
            codexTimer.Tick += (s, e) => store.RefreshCodexAsync();
            codexTimer.Start();

            // Claude の利用枠履歴と Claude Code / Codex の記録（PC内のファイルを読むだけ）
            claudeTimer.Interval = settings.ClaudeIntervalMinutes * 60 * 1000;
            claudeTimer.Tick += (s, e) =>
            {
                store.RefreshClaude();
                store.ScanLogsAsync(false);
            };
            claudeTimer.Start();

            // 表示の鮮度（古い情報の灰色表示）は1分ごとに見直す
            minuteTimer.Interval = 60 * 1000;
            minuteTimer.Tick += (s, e) =>
            {
                // 組織ポリシーの変更は再起動を待たずに反映する
                try { store.Policy = Policy.Load(); } catch (Exception ex) { Hardening.AppendErrorLog(store.Paths.DataDir, ex.ToString()); }
                UpdateIcons();
                try { store.CheckResets(); } catch (Exception ex) { Hardening.AppendErrorLog(store.Paths.DataDir, ex.ToString()); }
                try { MaybeRunScheduledReport(); } catch (Exception ex) { Hardening.AppendErrorLog(store.Paths.DataDir, ex.ToString()); }
            };
            minuteTimer.Start();

            // チームへの報告：起動の約1.5分後に1回、その後は5分ごと（書き込みは変更がなくても最短5分間隔）
            teamTimer.Interval = 90 * 1000;
            teamTimer.Tick += (s, e) =>
            {
                teamTimer.Interval = 5 * 60 * 1000;
                UploadTeamAsync(false);
            };
            teamTimer.Start();
            if (team.Local.Joined) ThreadPool.QueueUserWorkItem(_ => { team.TryRefreshTeam(); PostUpdate(); });

            // フレンドの残り枠：起動の約20秒後に1回、その後は1分ごと（書き込みは変化があるときか5分おき）
            friendsTimer.Interval = 20 * 1000;
            friendsTimer.Tick += (s, e) =>
            {
                friendsTimer.Interval = 60 * 1000;
                TickFriendsAsync();
            };
            friendsTimer.Start();

            // 2つ目の起動からの「開いて」の合図
            if (showSignal != null)
            {
                var ctx = SynchronizationContext.Current;
                showWait = ThreadPool.RegisterWaitForSingleObject(showSignal, (o, timedOut) =>
                {
                    if (ctx != null) ctx.Post(_ => { if (!CheckPendingInvite()) ShowDashboard(DashTab.Usage); }, null);
                }, null, -1, false);
                if (quitSignal != null)
                    quitWait = ThreadPool.RegisterWaitForSingleObject(quitSignal, (o, timedOut) =>
                    {
                        if (ctx != null) ctx.Post(_ => Quit(), null);
                    }, null, -1, true);
            }

            Microsoft.Win32.SystemEvents.PowerModeChanged += (s, e) =>
            {
                if (e.Mode == Microsoft.Win32.PowerModes.Resume) RefreshAll();
            };

            // 招待リンク（aiusagebar:）で起動された場合は、参加画面を開く（メッセージループ開始後に実行）
            var startCtx = SynchronizationContext.Current;
            if (startCtx != null) startCtx.Post(_ => { try { CheckPendingInvite(); } catch { } }, null);
        }

        /// <summary>招待リンクの受け渡しファイルがあれば、参加画面を開く。開いたら true</summary>
        bool CheckPendingInvite()
        {
            string path = Program.PendingInvitePath;
            string url = null;
            try
            {
                if (!File.Exists(path)) return false;
                // 古い受け渡しは無視（60秒以内のもののみ）
                if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(path)).TotalSeconds <= 60)
                    url = FileUtil.ReadShared(path);
                File.Delete(path);
            }
            catch { return false; }
            if (string.IsNullOrEmpty(url)) return false;
            string folder, name, fp;
            if (!TeamInvite.TryDecodeLink(url, out folder, out name, out fp)) return false;
            if (!team.Allowed) { TeamUi.Error(dashboard, "組織のポリシーでチーム機能は無効になっています"); return true; }
            if (team.Local.Joined) { ShowDashboard(DashTab.Team); return true; }
            ShowDashboard(DashTab.Team);
            try { TeamUi.JoinTeam(dashboard, team, url); AfterTeamChange(); }
            catch (Exception ex) { Hardening.AppendErrorLog(store.Paths.DataDir, ex.ToString()); }
            return true;
        }

        void AfterTeamChange()
        {
            if (dashboard != null && !dashboard.IsDisposed) dashboard.Invalidate();
            UploadTeamAsync(true);
        }

        int aiBusy;

        /// <summary>定期レポート：条件がそろっていれば、AI提案を生成して保存し、タスクトレイに通知する（マスターのみ・既定オフ）。</summary>
        void MaybeRunScheduledReport()
        {
            if (!team.Local.Joined || store.Scanning) return;
            if (store.Policy.DisableAiSuggest || !team.IsMaster) return;
            var sched = AiSchedule.Load(store.Paths);
            var now = DateTime.Now;
            if (!sched.IsDue(now)) return;
            var cfg = AiConfig.Load(store.Paths);
            if (!cfg.IsComplete) return;
            if (System.Threading.Interlocked.CompareExchange(ref aiBusy, 1, 0) != 0) return;
            sched.MarkRan(store.Paths, now);   // 生成前に記録して、失敗しても同日に何度も呼ばない
            int period = sched.PeriodIndex;
            var ctx = System.Threading.SynchronizationContext.Current;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                string file = null, error = null;
                try { file = AIUsageBar.AiClient.GenerateReport(team, cfg, period); }
                catch (Exception ex) { error = ex.Message; Hardening.AppendErrorLog(store.Paths.DataDir, "AI定期レポート: " + ex); }
                finally { System.Threading.Interlocked.Exchange(ref aiBusy, 0); }
                if (ctx != null) ctx.Post(__ =>
                {
                    if (error == null && file != null)
                    {
                        lastAiReport = file;
                        try { codexIcon.ShowBalloonTip(10000, "AI活用レポートができました", "クリックで開きます（" + AiSchedule.FreqNames[sched.Frequency] + "の定期レポート）", ToolTipIcon.Info); } catch { }
                    }
                    else if (error != null)
                    {
                        try { codexIcon.ShowBalloonTip(8000, "AIレポートを生成できませんでした", error, ToolTipIcon.Warning); } catch { }
                    }
                }, null);
            });
        }

        string lastAiReport;

        int teamBusy;

        void UploadTeamAsync(bool force)
        {
            if (!team.Local.Joined || store.Scanning) return;
            if (Interlocked.CompareExchange(ref teamBusy, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { team.Upload(force); }
                catch (Exception ex) { Hardening.AppendErrorLog(store.Paths.DataDir, ex.ToString()); }
                finally { Interlocked.Exchange(ref teamBusy, 0); PostUpdate(); }
            });
        }

        int friendsBusy;

        void TickFriendsAsync()
        {
            if (Interlocked.CompareExchange(ref friendsBusy, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { friends.Tick(); }
                catch (Exception ex) { Hardening.AppendErrorLog(store.Paths.DataDir, ex.ToString()); }
                finally { Interlocked.Exchange(ref friendsBusy, 0); PostUpdate(); }
            });
        }

        void PostUpdate()
        {
            var d = dashboard;
            if (d == null || d.IsDisposed || !d.IsHandleCreated) return;
            try { d.BeginInvoke((Action)(() => d.Invalidate())); } catch { }
        }

        void ShowTeamManager()
        {
            if (!team.IsMaster) { TeamUi.Error(dashboard, "このPCではマスター権限が有効になっていません"); return; }
            if (manager == null || manager.IsDisposed)
            {
                manager = new TeamManagerForm(team, store);
                manager.Show();
            }
            else
            {
                if (manager.WindowState == FormWindowState.Minimized) manager.WindowState = FormWindowState.Normal;
                manager.Activate();
                manager.Reload();
            }
        }

        void RefreshAll()
        {
            store.RefreshCodexAsync();
            store.RefreshClaude();
            store.ScanLogsAsync(false);
            activity.InvalidateTitles();
            TickFriendsAsync();
        }

        Dashboard EnsureDashboard()
        {
            if (dashboard != null && !dashboard.IsDisposed) return dashboard;
            dashboard = new Dashboard(store, activity, team);
            dashboard.Friends = friends;
            dashboard.FriendsChanged += TickFriendsAsync;
            dashboard.OpenTeamManager += ShowTeamManager;
            dashboard.TeamChanged += () => UploadTeamAsync(true);
            dashboard.QuitRequested += Quit;
            dashboard.RefreshRequested += RefreshAll;
            dashboard.RelocateRequested += Relocate;
            dashboard.SettingsChanged += () =>
            {
                codexTimer.Interval = store.Settings.CodexIntervalMinutes * 60 * 1000;
                claudeTimer.Interval = store.Settings.ClaudeIntervalMinutes * 60 * 1000;
                if (store.Settings.ActivityEnabled) store.ScanLogsAsync(false);
                UpdateIcons();
                if (dashboard != null && dashboard.TaskbarMode != store.Settings.ShowInTaskbar)
                {
                    // 表示方式を切り替えたらウィンドウを作り直す（クリック処理の後で行う）
                    var ctx = SynchronizationContext.Current;
                    Action recreate = () =>
                    {
                        var old = dashboard;
                        var bounds = old.Bounds;
                        dashboard = null;
                        old.QuitRequested -= Quit;
                        old.Hide();
                        old.Dispose();
                        var d = EnsureDashboard();
                        d.Tab = DashTab.Settings;
                        if (d.TaskbarMode) d.StartInTaskbar(false);
                        else { d.ShowNear(new Point(bounds.X + bounds.Width / 2, bounds.Bottom)); }
                        UpdateIcons();
                    };
                    if (ctx != null) ctx.Post(_ => recreate(), null); else recreate();
                }
            };
            return dashboard;
        }

        void ShowDashboard(DashTab? tab)
        {
            var d = EnsureDashboard();
            if (tab != null)
            {
                d.Tab = tab.Value;
                if (d.TaskbarMode) { d.RestoreFromTaskbar(); return; }
                if (d.Visible) { d.Invalidate(); d.Activate(); return; }
            }
            d.ShowNear(Cursor.Position);
        }

        void UpdateIcons()
        {
            var now = DateTime.UtcNow;
            int size = TrayIcons.SmallIconSize;

            double? codex = store.CodexMinRemaining(now);
            string codexTip;
            if (codex != null) codexTip = "Codex 残り" + Math.Floor(codex.Value) + "%（" + store.CodexMinLabel(now) + "）";
            else if (store.CodexError != null) codexTip = "Codex — " + store.CodexError;
            else codexTip = "Codex — 未取得または古い情報";
            SetIcon(codexIcon, TrayKind.Codex, codex, size, codexTip + "\nクリックで詳細を開く", ref lastCodexKey);

            double? claude = store.ClaudeMinRemaining(now);
            string claudeTip;
            if (claude != null)
            {
                var c = store.Claude;
                claudeTip = "Claude 残り" + Math.Floor(claude.Value) + "%（5時間 " + Pct(c.FiveHourRemaining) + "・週間 " + Pct(c.WeeklyRemaining) + "）";
            }
            else claudeTip = "Claude — " + (store.ClaudeError ?? "未取得または古い情報");
            SetIcon(claudeIcon, TrayKind.Claude, claude, size, claudeTip + "\nクリックで詳細を開く", ref lastClaudeKey);

            if (dashboard != null && !dashboard.IsDisposed)
            {
                dashboard.UpdateTaskbar(codex, claude);
                if (dashboard.Visible) dashboard.Invalidate();
            }
        }

        static string Pct(double? v) { return v == null ? "—" : Math.Floor(v.Value) + "%"; }

        static void SetIcon(NotifyIcon icon, TrayKind kind, double? remaining, int size, string tip, ref string lastKey)
        {
            string key = kind + "|" + (remaining == null ? "x" : Math.Floor(remaining.Value).ToString()) + "|" + size;
            if (key != lastKey)
            {
                using (var bmp = TrayIcons.Render(kind, remaining, size))
                {
                    var old = icon.Icon;
                    icon.Icon = TrayIcons.ToIcon(bmp);
                    if (old != null) old.Dispose();
                }
                lastKey = key;
            }
            // NotifyIcon.Text は63文字まで
            icon.Text = tip.Length > 63 ? tip.Substring(0, 62) + "…" : tip;
        }

        /// <summary>exe を推奨の場所（%LOCALAPPDATA%\Programs\AI Usage Bar）へコピーし、そちらで起動し直す</summary>
        void Relocate()
        {
            string src = Hardening.CurrentExePath;
            string dir = Hardening.RecommendedInstallDir;
            string dest = Path.Combine(dir, "AIUsageBar.exe");
            var answer = MessageBox.Show("次の場所へコピーして、そちらで起動し直します。\n\n" + dest + "\n\n元のファイルは削除しないので、確認後にご自身で削除してください。", AppInfo.Name, MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (answer != DialogResult.OK) return;
            try
            {
                Directory.CreateDirectory(dir);
                File.Copy(src, dest, true);
                bool wasRegistered = StartupRegistration.RegisteredValue() != null;
                if (wasRegistered && !store.Policy.DisableStartupRegistration) StartupRegistration.Set(true, dest);
                var psi = new System.Diagnostics.ProcessStartInfo(dest, "--wait-for-pid " + System.Diagnostics.Process.GetCurrentProcess().Id);
                psi.UseShellExecute = false;
                psi.WorkingDirectory = dir;
                using (System.Diagnostics.Process.Start(psi)) { }
                Quit();
            }
            catch (Exception ex)
            {
                MessageBox.Show("移動できませんでした：" + ex.Message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        bool quitting;

        void Quit()
        {
            if (quitting) return;
            quitting = true;
            codexTimer.Stop();
            claudeTimer.Stop();
            minuteTimer.Stop();
            teamTimer.Stop();
            friendsTimer.Stop();
            if (manager != null && !manager.IsDisposed) manager.Close();
            codexIcon.Visible = false;
            claudeIcon.Visible = false;
            if (showWait != null) showWait.Unregister(null);
            if (quitWait != null) quitWait.Unregister(null);
            try { store.Logs.Save(); } catch { }
            if (dashboard != null) { dashboard.QuitRequested -= Quit; dashboard.Dispose(); }
            codexIcon.Dispose();
            claudeIcon.Dispose();
            ExitThread();
        }
    }
}
