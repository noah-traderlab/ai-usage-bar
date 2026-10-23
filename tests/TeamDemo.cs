using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace AIUsageBar.Tests
{
    /// <summary>
    /// 実際の流れの再現：このPCの利用者（実データ）がマスターになってメンバーをメールで追加し、
    /// 相手のPC（見本データ・指定のメールで Claude にログインした状態）が招待コードで参加・報告し、
    /// マスターの管理画面に自動で表示されるかを確認する。保存先はすべて一時フォルダ（実際の設定は変更しない）。
    /// </summary>
    public static partial class TestMain
    {
        static int TeamDemo(string outDir, string memberEmail, string masterEmail)
        {
            Directory.CreateDirectory(outDir);
            string work = Tmp();
            string shared = Path.Combine(work, "共有フォルダ");
            Directory.CreateDirectory(shared);
            string pass = "demo-" + Guid.NewGuid().ToString("N");

            Console.WriteLine("== マスター（このPCの実際の記録。保存先は一時フォルダ）");
            var mp = AppPaths.Default();
            mp.DataDir = Path.Combine(work, "master-data");
            var mstore = new UsageStore(mp, new Settings(), null);
            mstore.RefreshClaude();
            mstore.Logs.Scan(null);
            var mact = new Activity(mstore);
            var master = new Install { Paths = mp, Store = mstore, Activity = mact, Svc = new TeamService(mstore, mact) };
            master.Svc.CreateTeam(shared, "テストチーム", "マスター", masterEmail, pass);
            Check(master.Svc.IsMaster, "チームを作成し、" + masterEmail + " がマスター");

            var skipped = new List<string>();
            int added = master.Svc.AddMembers(TeamConfig.ParseMemberList(memberEmail, null), ShareScope.Usage, "マスター", skipped);
            Check(added == 1, memberEmail + " を一般ユーザーとして追加（閲覧範囲：利用量のみ）");
            string invite = TeamInvite.Message(master.Svc.Team, master.Svc.Local.SharedFolder);
            File.WriteAllText(Path.Combine(outDir, "招待文.txt"), invite);

            Console.WriteLine("== 相手のPC（再現：" + memberEmail + " でログイン。数値は相手の Mac 版の画面と同じ）");
            // 相手の画面の値：Claude Code の日別トークン（9/11〜9/17）、Codex のサービス集計（9/12〜9/16）、残り枠
            var today = DateTime.Now.Date;
            long[] claudeDaily = { 2670000000, 3450000000, 3550000000, 2400000000, 1480000000, 1020000000, 7400000 };
            long[] codexService = { 0, 15100000, 49200000, 4600000, 15200000, 7500000, 0 };
            var friend = NewInstall(p =>
            {
                File.WriteAllText(Path.Combine(p.Home, ".claude.json"), "{\"oauthAccount\":{\"emailAddress\":\"" + memberEmail + "\"}}");
                for (int d = 0; d < 7; d++)
                {
                    var day = today.AddDays(d - 6);
                    string sid = Guid.NewGuid().ToString();
                    var t = (d == 6 ? day.AddHours(Math.Min(8, Math.Max(0, DateTime.Now.Hour - 1))) : day.AddHours(12)).ToUniversalTime();
                    long inp = 100, outp = 20000, cw = 300000;
                    WriteLines(Path.Combine(p.ClaudeProjects, "C--work", sid + ".jsonl"), ClaudeUser(sid, t, "（見本）"),
                        ClaudeAssistant(sid, "m" + sid, "r" + sid, t.AddSeconds(5), inp, outp, claudeDaily[d] - inp - outp - cw, cw));
                }
                // Claude Desktop の保存履歴：5時間 1% 使用（残り99%）、週間 100% 使用（残り0%）
                string hist = Path.Combine(p.RoamingAppData, "Claude", "plan-usage-history.json");
                Directory.CreateDirectory(Path.GetDirectoryName(hist));
                File.WriteAllText(hist, "{\"samples\":[{\"t\":" + Fmt.ToUnixMs(DateTime.UtcNow.AddMinutes(-2)) + ",\"org\":\"x\",\"u\":{\"fh\":1,\"sd\":100}}]}");
            });
            friend.Store.RefreshClaude();
            var nowUtc = DateTime.UtcNow;
            friend.Store.Codex = new CodexSnapshot
            {
                ObservedAtUtc = nowUtc, PlanType = "plus", AccountEmail = memberEmail,
                Main = new QuotaGroup { LimitId = "codex", Windows = {
                    new QuotaWindow { Label = "5時間", UsedPercent = 100, WindowMinutes = 300, ResetsAtUtc = nowUtc.AddMinutes(25) },
                    new QuotaWindow { Label = "週間", UsedPercent = 16, WindowMinutes = 10080, ResetsAtUtc = nowUtc.AddDays(6.8) } } },
                Daily = Enumerable.Range(0, 7).Where(d => codexService[d] > 0).Select(d => new DailyTokens { Date = today.AddDays(d - 6).ToString("yyyy-MM-dd"), Tokens = codexService[d] }).ToList(),
            };
            string f, n, fp;
            Check(TeamInvite.TryDecode(invite, out f, out n, out fp), "招待文から招待コードを読み取れる");
            friend.Svc.Join(f, fp, "知り合い（再現）", memberEmail, ShareScope.Usage);
            TeamService.DemoMachine = "相手のPC（再現）";
            string up = friend.Svc.Upload(true);
            TeamService.DemoMachine = null;
            Check(up == null, "相手のPCが参加して報告", up);
            Check(master.Svc.Upload(true) == null, "マスターのPCも報告");

            Console.WriteLine("== マスターの確認");
            var reports = master.Svc.ReadReports();
            foreach (var r in reports)
                Console.WriteLine("  報告：" + r.Machine + "／ログイン中のメール " + string.Join(",", r.DetectedEmails) + "／" + (r.Error ?? (r.AutoApproved ? "自動で集計" : r.Approved ? "承認済み" : "承認待ち")) + "／トークン " + r.Daily.Sum(d => d.Sum.Total).ToString("#,0"));
            var fr = reports.FirstOrDefault(r => r.InstallId == friend.Svc.Local.InstallId);
            Check(fr != null && fr.Error == null && fr.AutoApproved, memberEmail + " の利用量が、承認の操作なしで集計される");
            var mr = reports.FirstOrDefault(r => r.InstallId == master.Svc.Local.InstallId);
            Check(mr != null && mr.AutoApproved, masterEmail + "（マスター自身）の利用量も集計される");

            System.Threading.SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            using (var m = new TeamManagerForm(master.Svc, master.Store))
            {
                m.Size = new Size(1280, 760);
                m.StartPosition = FormStartPosition.Manual;
                m.Location = new Point(-5000, -5000);
                m.ShowInTaskbar = false;
                m.Show();
                var until = DateTime.Now.AddSeconds(3);
                while (DateTime.Now < until) { Application.DoEvents(); System.Threading.Thread.Sleep(30); }
                m.LoadNow();
                m.SelectRowByEmail(memberEmail);
                m.SelectDetailTab(0);
                Application.DoEvents();
                using (var bmp = new Bitmap(m.Width, m.Height)) { m.DrawToBitmap(bmp, new Rectangle(0, 0, m.Width, m.Height)); bmp.Save(Path.Combine(outDir, "管理画面.png")); }

                Console.WriteLine("== 相手の Mac 版の画面との照合（管理画面の表）");
                var row = m.GridText(0).FirstOrDefault(x => x.Length > 1 && x[1].Contains(memberEmail));
                Check(row != null && row[2] == "一般ユーザー", "一般ユーザーとして一覧に表示");
                if (row != null)
                {
                    Console.WriteLine("  一覧の行：" + string.Join(" | ", row));
                    Check(row[6] == Fmt.Full(claudeDaily.Sum()), "Claude Code トークン（今月）＝ 7日間の合計 14.58B", row[6]);
                    Check(row[7] == "91,600,000（サービス集計）", "Codex トークン（今月）＝ サービス集計の合計 91.6M", row[7]);
                    Check(row[11] == "0%／84%", "Codex 残り：5時間 0%・週間 84%", row[11]);
                    Check(row[12] == "99%／0%", "Claude 残り：5時間 99%・週間 0%", row[12]);
                }
                var totals = m.GridText(1);
                string[] macClaude = { "2.67B", "3.45B", "3.55B", "2.40B", "1.48B", "1.02B", "7.4M" };
                string[] macCodex = { "—", "15.1M", "49.2M", "4.6M", "15.2M", "7.5M", "—" };
                for (int d = 0; d < 7; d++)
                {
                    string date = today.AddDays(d - 6).ToString("yyyy-MM-dd");
                    var t = totals.FirstOrDefault(x => x[0] == date);
                    string cc = t == null ? "(行なし)" : t[1].Split('（')[0];
                    string cs = t == null ? "(行なし)" : t[3].Split('（')[0];
                    Check(cc == macClaude[d] && cs == macCodex[d], date.Substring(5) + "：Claude Code " + macClaude[d] + "・Codex " + macCodex[d] + " が Mac 版と一致", "表示 " + cc + " / " + cs);
                }
            }

            // 参加画面に招待文を貼り付けた状態
            var timer = new Timer { Interval = 800 };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                var form = Application.OpenForms.Cast<Form>().LastOrDefault();
                if (form == null) return;
                var box = FindControls<TextBox>(form).FirstOrDefault(x => x.Multiline);
                if (box != null) box.Text = invite;
                Application.DoEvents();
                using (var bmp = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height)); bmp.Save(Path.Combine(outDir, "参加画面_招待コード貼り付け.png")); }
                form.DialogResult = DialogResult.Cancel;
                form.Close();
            };
            timer.Start();
            TeamUi.JoinTeam(null, NewInstall(null).Svc);
            timer.Dispose();

            Console.WriteLine("出力：" + outDir);
            Console.WriteLine("結果: " + passes + " passed, " + failures + " failed");
            return failures == 0 ? 0 : 1;
        }

        static IEnumerable<T> FindControls<T>(Control root) where T : Control
        {
            foreach (Control c in root.Controls)
            {
                if (c is T) yield return (T)c;
                foreach (var x in FindControls<T>(c)) yield return x;
            }
        }
    }
}
