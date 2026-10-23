using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace AIUsageBar.Tests
{
    /// <summary>チーム画面の見本を画像に書き出す（見本データのみ使用）</summary>
    public static partial class TestMain
    {
        static void RenderTeam(string outDir)
        {
            Directory.CreateDirectory(outDir);
            Activity.LocalActorOverride = "user01@PC-DEMO";   // 見本画像に実PCのユーザー名・PC名を写さない
            string shared = Path.Combine(Tmp(), "Share");
            Directory.CreateDirectory(shared);
            const string pass = "demo passphrase for render";
            var now = DateTime.UtcNow;

            Func<string, int, string, Action<AppPaths>> claudeLogs = (who, n, title) => p =>
            {
                for (int d = 0; d < 6; d++)
                {
                    string sid = Guid.NewGuid().ToString();
                    var t = now.AddDays(-d).AddHours(-2);
                    var lines = new List<string> { "{\"type\":\"ai-title\",\"aiTitle\":\"" + title + (d + 1) + "\",\"sessionId\":\"" + sid + "\"}", ClaudeUser(sid, t, who + "の依頼：資料の" + (d + 1) + "章を要約して、表にまとめて") };
                    for (int i = 0; i < n; i++) lines.Add(ClaudeAssistant(sid, "m" + sid + i, "r" + sid + i, t.AddMinutes(i), 20, 900 + i * 10, 40000 + i * 1000, 3000));
                    WriteLines(Path.Combine(p.ClaudeProjects, "C--Users-" + who + "-work", sid + ".jsonl"), lines.ToArray());
                }
                string cid = "01a0cccc-0000-7000-8000-" + Math.Abs(who.GetHashCode()).ToString("000000000000").Substring(0, 12);
                var ct = now.AddHours(-5);
                var dir = Path.Combine(p.CodexHome, "sessions", ct.ToString("yyyy"), ct.ToString("MM"), ct.ToString("dd"));
                WriteLines(Path.Combine(dir, "rollout-x-" + cid + ".jsonl"), CodexMeta(cid, ct), CodexUserMsg(ct.AddSeconds(1), who + "：ログイン画面のテストを追加"),
                    "{\"timestamp\":\"" + Iso(ct.AddSeconds(2)) + "\",\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-5.6-sol\"}}",
                    CodexRecord(cid, "resp_" + cid + "1", ct.AddSeconds(3), 120000, 100000, 3000, 900), CodexRecord(cid, "resp_" + cid + "2", ct.AddSeconds(9), 140000, 120000, 2500, 700));
            };

            var boss = NewInstall(claudeLogs("boss", 30, "月次報告の作成"));
            boss.Svc.CreateTeam(shared, "開発部", "山田（マスター）", "yamada@example.co.jp", pass);
            var alice = NewInstall(claudeLogs("alice", 60, "見積もりの作成"));
            string fp = boss.Svc.Team.Fingerprint;
            alice.Svc.Join(shared, fp, "佐藤", "sato@example.co.jp", ShareScope.Prompts);
            var bob = NewInstall(claudeLogs("bob", 12, "調査メモ"));
            bob.Svc.Join(shared, fp, "鈴木", "suzuki@example.co.jp", ShareScope.Summary);

            var edit = boss.Svc.Team.Clone();
            edit.Members[0].Plans.Add(new PlanEntry { Name = "Claude Max", MonthlyFeeJpy = 30000 });
            edit.Members.Add(new MemberEntry { Id = "a", Name = "佐藤", Emails = { "sato@example.co.jp" }, Scope = ShareScope.Prompts, Plans = { new PlanEntry { Name = "Claude Max", MonthlyFeeJpy = 30000 }, new PlanEntry { Name = "ChatGPT Pro", MonthlyFeeJpy = 30000 } } });
            edit.Members.Add(new MemberEntry { Id = "b", Name = "鈴木", Emails = { "suzuki@example.co.jp" }, Scope = ShareScope.Summary, Plans = { new PlanEntry { Name = "Claude Pro", MonthlyFeeJpy = 3000 } } });
            boss.Svc.SaveTeam(edit, "山田");
            var demoNames = new[] { new[] { "PC-YAMADA", "yamada" }, new[] { "PC-SATO", "sato" }, new[] { "PC-SUZUKI", "suzuki" } };
            var all = new[] { boss, alice, bob };
            for (int i = 0; i < all.Length; i++) { TeamService.DemoMachine = demoNames[i][0]; TeamService.DemoUser = demoNames[i][1]; all[i].Svc.Upload(true); }
            TeamService.DemoMachine = "PC-TEMP01"; TeamService.DemoUser = "user01";
            foreach (var r in boss.Svc.ReadReports().Where(r => r.Machine != null)) boss.Svc.SetApproval(r, true);
            // 承認待ちを1台作る
            var unknown = NewInstall(claudeLogs("unknown", 3, "不明"));
            unknown.Svc.Join(shared, fp, "不明なPC", "sato@example.co.jp", ShareScope.Usage);
            unknown.Svc.Upload(true);

            Func<Install, string, string> dashShot = (inst, name) =>
            {
                // 見本画像に実際のPCのフォルダ名（ユーザー名）を写さない（表示だけ差し替え、保存はしない）
                string realFolder = inst.Svc.Local.SharedFolder;
                if (realFolder != null) inst.Svc.Local.SharedFolder = @"\\fileserver\share\" +TeamService.FolderName;
                using (var d = new Dashboard(inst.Store, inst.Activity, inst.Svc))
                {
                    d.ApplyTheme(false);
                    d.Tab = DashTab.Team;
                    using (var probe = new Bitmap(Dashboard.LogicalWidth, 700)) using (var g = Graphics.FromImage(probe)) d.RenderTo(g, probe.Size);
                    int h = (int)(d.ContentHeight + d.ChromeHeight + 2);
                    using (var bmp = new Bitmap(Dashboard.LogicalWidth, h)) using (var g = Graphics.FromImage(bmp)) { d.RenderTo(g, bmp.Size); bmp.Save(Path.Combine(outDir, name)); }
                }
                inst.Svc.Local.SharedFolder = realFolder;
                return name;
            };
            dashShot(boss, "team-tab-master.png");
            dashShot(alice, "team-tab-general.png");
            var notJoined = NewInstall(null);
            dashShot(notJoined, "team-tab-not-joined.png");

            // 利用枠・履歴・詳細ログ（見本データ・新デザイン）。個人フォルダ名は表示だけ差し替え。
            Action<Install, DashTab, bool, string> tabShot = (inst, tab, byPrompt, name) =>
            {
                using (var d = new Dashboard(inst.Store, inst.Activity, inst.Svc))
                {
                    d.ApplyTheme(false);
                    d.Tab = tab; d.LogByPrompt = byPrompt;
                    var views = inst.Store.Logs.SessionsForDay(DateTime.Now.Date);
                    if (views.Count > 0) d.Expanded.Add("sess:" + views[0].Info.Key);
                    using (var probe = new Bitmap(Dashboard.LogicalWidth, 700)) using (var g = Graphics.FromImage(probe)) d.RenderTo(g, probe.Size);
                    int h = (int)Math.Min(3000, d.ContentHeight + d.ChromeHeight + 2);
                    using (var bmp = new Bitmap(Dashboard.LogicalWidth, h)) using (var g = Graphics.FromImage(bmp)) { d.RenderTo(g, bmp.Size); bmp.Save(Path.Combine(outDir, name)); }
                }
            };
            // 見本の残り枠（Codex 5時間0%/週間84%、Claude 5時間99%/週間0%）を入れて、利用枠カードを埋める
            try
            {
                string hist = Path.Combine(alice.Store.Paths.RoamingAppData, "Claude", "plan-usage-history.json");
                Directory.CreateDirectory(Path.GetDirectoryName(hist));
                File.WriteAllText(hist, "{\"samples\":[{\"t\":" + Fmt.ToUnixMs(DateTime.UtcNow.AddMinutes(-2)) + ",\"org\":\"x\",\"u\":{\"fh\":1,\"sd\":100}}]}");
                alice.Store.RefreshClaude();
            }
            catch { }
            var uNow = DateTime.UtcNow;
            alice.Store.Codex = new CodexSnapshot
            {
                ObservedAtUtc = uNow, PlanType = "plus", AccountEmail = "sato@example.co.jp",
                Main = new QuotaGroup { LimitId = "codex", Windows = {
                    new QuotaWindow { Label = "5時間", UsedPercent = 100, WindowMinutes = 300, ResetsAtUtc = uNow.AddMinutes(25) },
                    new QuotaWindow { Label = "週間", UsedPercent = 16, WindowMinutes = 10080, ResetsAtUtc = uNow.AddDays(6.8) } } },
            };
            tabShot(alice, DashTab.Usage, false, "view-usage.png");
            tabShot(alice, DashTab.History, false, "view-history.png");
            tabShot(alice, DashTab.Log, false, "view-log.png");
            tabShot(alice, DashTab.Log, true, "view-log-prompts.png");
            // 設定画面は保存先・実行場所のパスに実PCのユーザー名が出るため、見本用の値に差し替えて描画する
            {
                string realData = alice.Store.Paths.DataDir;
                alice.Store.Paths.DataDir = @"C:\Users\user01\AppData\Local\AIUsageBar";
                Hardening.CurrentExePathOverride = @"C:\Users\user01\AppData\Local\Programs\AI Usage Bar\AIUsageBar.exe";
                tabShot(alice, DashTab.Settings, false, "view-settings.png");
                var savedPol = alice.Store.Policy;
                alice.Store.Policy = new Policy { DisableActivityLog = true, HidePromptText = true, DisableExport = true, DisableCodexFetch = true, DisableStartupRegistration = true, DisableTeamSharing = false, DisableAiSuggest = true };
                tabShot(alice, DashTab.Settings, false, "view-policy-settings.png");
                alice.Store.Policy = savedPol;
                alice.Store.Paths.DataDir = realData;
                Hardening.CurrentExePathOverride = null;
            }

            System.Threading.SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            using (var m = new TeamManagerForm(boss.Svc, boss.Store))
            {
                m.Size = new Size(1280, 820);
                m.StartPosition = FormStartPosition.Manual;
                m.Location = new Point(-5000, -5000);
                m.ShowInTaskbar = false;
                m.Show();
                var until = DateTime.Now.AddSeconds(4);
                while (DateTime.Now < until) { Application.DoEvents(); System.Threading.Thread.Sleep(30); }
                m.LoadNow();
                m.SelectFirstRow();
                Application.DoEvents();
                foreach (var tab in new[] { 0, 1, 3, 4 })
                {
                    m.SelectDetailTab(tab);
                    Application.DoEvents();
                    using (var bmp = new Bitmap(m.Width, m.Height))
                    {
                        m.DrawToBitmap(bmp, new Rectangle(0, 0, m.Width, m.Height));
                        bmp.Save(Path.Combine(outDir, "manager-tab" + tab + ".png"));
                    }
                }
                // 顧客別スタートライン（見本）：一部活用の基準を有効化してから描画
                new TeamAnalytics.TeamBaseline { Enabled = true, StageIndex = 2, TargetScore = 50, TargetAdoptionPct = 70 }.Save(boss.Store.Paths);
                // 上層部向けダッシュボード
                using (var dash = m.BuildExecDashboard())
                {
                    if (dash != null)
                    {
                        dash.StartPosition = FormStartPosition.Manual;
                        dash.Location = new Point(-5000, -5000);
                        dash.ShowInTaskbar = false;
                        dash.Size = new Size(1040, 720);
                        dash.Show();
                        Application.DoEvents();
                        using (var bmp = new Bitmap(dash.Width, dash.Height)) { dash.DrawToBitmap(bmp, new Rectangle(0, 0, dash.Width, dash.Height)); bmp.Save(Path.Combine(outDir, "dashboard.png")); }
                    }
                }
            }

            // ダイアログは表示直後に画像にして閉じる
            Action<string, Action> dialogShot = (name, show) =>
            {
                var timer = new Timer { Interval = 700 };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    var f = Application.OpenForms.Cast<Form>().LastOrDefault();
                    if (f == null) return;
                    using (var bmp = new Bitmap(f.Width, f.Height)) { f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height)); bmp.Save(Path.Combine(outDir, name)); }
                    f.DialogResult = DialogResult.Cancel;
                    f.Close();
                };
                timer.Start();
                show();
                timer.Dispose();
            };
            var member = boss.Svc.Team.Members.First(x => x.Name == "佐藤");
            dialogShot("dialog-member.png", () => TeamUi.EditMember(null, member, false));
            dialogShot("dialog-prices.png", () => TeamUi.EditPrices(null, boss.Svc.Team.Clone()));
            // 参加画面は「招待コードから自動入力された」状態を見せる（フォルダは見本用のUNCパスで個人情報を出さない）
            string demoInvite = TeamInvite.Encode(@"\\fileserver\share\" + TeamService.FolderName, boss.Svc.Team.TeamName, boss.Svc.Team.Fingerprint);
            dialogShot("dialog-join.png", () => TeamUi.JoinTeam(null, NewInstall(null).Svc, demoInvite));
            dialogShot("dialog-invite.png", () => TeamUi.ShowInvite(null, boss.Svc, new[] { "sato@example.co.jp" }));
            dialogShot("dialog-create.png", () => TeamUi.CreateTeam(null, NewInstall(null).Svc));
            {
                var sc = new AiSchedule { Enabled = true, Frequency = 1, Weekday = 4, Hour = 9, Minute = 30, PeriodIndex = 1 };
                sc.Save(boss.Store.Paths);
                using (var sf = TeamDashboardForm.BuildScheduleForm(boss.Store.Paths))
                {
                    sf.StartPosition = FormStartPosition.Manual;
                    sf.Location = new Point(-5000, -5000);
                    sf.Show(); Application.DoEvents();
                    using (var bmp = new Bitmap(sf.Width, sf.Height)) { sf.DrawToBitmap(bmp, new Rectangle(0, 0, sf.Width, sf.Height)); bmp.Save(Path.Combine(outDir, "dialog-schedule.png")); }
                    sf.Close();
                }
                using (var bf = TeamDashboardForm.BuildBaselineForm(boss.Store.Paths))
                {
                    bf.StartPosition = FormStartPosition.Manual;
                    bf.Location = new Point(-5000, -5000);
                    bf.Show(); Application.DoEvents();
                    using (var bmp = new Bitmap(bf.Width, bf.Height)) { bf.DrawToBitmap(bmp, new Rectangle(0, 0, bf.Width, bf.Height)); bmp.Save(Path.Combine(outDir, "dialog-baseline.png")); }
                    bf.Close();
                }
            }
            Console.WriteLine("rendered team screens to " + outDir);
        }
    }
}
