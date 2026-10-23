using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AIUsageBar.Tests
{
    public static partial class TestMain
    {
        static string NewId() { return Guid.NewGuid().ToString("N"); }

        static FriendStatus SampleFriend(string id, string name, DateTime updated, double? c5, double? cw, params double[] codex)
        {
            var s = new FriendStatus { Id = id, Name = name, UpdatedAtUtc = updated, ClaudeFiveHour = c5, ClaudeWeekly = cw };
            for (int i = 0; i < codex.Length; i++)
                s.Codex.Add(new FriendWindow { Label = i == 0 ? "5時間" : "週間", Remaining = codex[i], ResetsAtUtc = updated.AddHours(i == 0 ? 2 : 90) });
            return s;
        }

        static void WriteFriendFile(string shared, FriendStatus s)
        {
            Friends.Publish(new FriendsConfig { Id = s.Id, Folder = shared, Enabled = true }, s);
        }

        static UsageStore FakeStoreWithQuota(string root, DateTime now)
        {
            var store = new UsageStore(FakePaths(root), new Settings(), null);
            long reset5 = (long)(now.AddHours(2) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            long resetW = (long)(now.AddDays(4) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            store.Codex = CodexClient.ParseRateLimits(Json.Parse("{\"rateLimitsByLimitId\":{\"codex\":{\"limitId\":\"codex\",\"primary\":{\"usedPercent\":64,\"windowDurationMins\":10080,\"resetsAt\":" + resetW + "},\"secondary\":{\"usedPercent\":12.5,\"windowDurationMins\":300,\"resetsAt\":" + reset5 + "},\"planType\":\"pro\"}},\"rateLimits\":{\"limitId\":\"codex\",\"primary\":{\"usedPercent\":64,\"windowDurationMins\":10080,\"resetsAt\":" + resetW + "},\"secondary\":{\"usedPercent\":12.5,\"windowDurationMins\":300,\"resetsAt\":" + reset5 + "},\"planType\":\"pro\"}}"), now);
            store.Codex.AccountEmail = "someone@example.com";
            store.Claude = new ClaudeSnapshot { SampleAtUtc = now.AddMinutes(-2), FiveHourUsed = 30, WeeklyUsed = 55 };
            return store;
        }

        static void FriendsUnit()
        {
            var now = DateTime.UtcNow;

            Console.WriteLine("== フレンド：形式と検証");
            {
                var a = SampleFriend(NewId(), "たなか", now, 70, 40, 88, 36);
                var back = FriendStatus.FromJson(Json.Parse(Json.Serialize(a.ToJson())));
                Check(back != null && back.Name == "たなか" && back.ClaudeFiveHour == 70 && back.ClaudeWeekly == 40 && back.Codex.Count == 2, "保存と復元");
                Check(back.Codex[0].ResetsAtUtc != null && Math.Abs((back.Codex[0].ResetsAtUtc.Value - a.Codex[0].ResetsAtUtc.Value).TotalSeconds) < 1, "リセット時刻の復元");
                Check(back.MinRemaining == 36 && back.ClaudeMin == 40 && back.CodexMin == 36, "最小の残り枠");

                var o = (Dictionary<string, object>)Json.Parse(Json.Serialize(a.ToJson()));
                o["name"] = "\u202Eevil\u0007name" + new string('x', 200);
                ((Dictionary<string, object>)o["claude"])["fiveHour"] = 150.0;
                ((Dictionary<string, object>)o["claude"])["weekly"] = -5.0;
                var bad = FriendStatus.FromJson(o);
                Check(bad != null && !bad.Name.Contains("\u202E") && !bad.Name.Contains("\u0007") && bad.Name.Length <= Friends.MaxNameLength + 1, "名前の制御文字・方向制御を除き、長さを制限");
                Check(bad.ClaudeFiveHour == 100 && bad.ClaudeWeekly == 0, "残り%は0〜100に丸める");

                o = (Dictionary<string, object>)Json.Parse(Json.Serialize(a.ToJson()));
                o["format"] = "other"; Check(FriendStatus.FromJson(o) == null, "形式が違えば読まない");
                o = (Dictionary<string, object>)Json.Parse(Json.Serialize(a.ToJson()));
                o["id"] = "../../x"; Check(FriendStatus.FromJson(o) == null, "不正なidは読まない");
                o = (Dictionary<string, object>)Json.Parse(Json.Serialize(a.ToJson()));
                o["updatedAt"] = -1L; Check(FriendStatus.FromJson(o) == null, "不正な時刻は読まない");
                o = (Dictionary<string, object>)Json.Parse(Json.Serialize(a.ToJson()));
                var many = new List<object>();
                for (int i = 0; i < 50; i++) many.Add(new Dictionary<string, object> { { "label", "w" + i }, { "remaining", 50.0 } });
                o["codex"] = many;
                Check(FriendStatus.FromJson(o).Codex.Count == Friends.MaxWindows, "枠の数を制限");
                Check(SampleFriend(NewId(), "x", now.AddMinutes(-31), 50, 50).IsStale(now) && !SampleFriend(NewId(), "x", now.AddMinutes(-5), 50, 50).IsStale(now), "30分以上更新なしで古い扱い");
            }

            Console.WriteLine("== フレンド：招待コード");
            {
                string code = FriendInvite.Encode(@"C:\Users\user01\OneDrive\友達と共有");
                Check(code.StartsWith(FriendInvite.Prefix), "接頭辞");
                Check(FriendInvite.TryDecode(code) == @"C:\Users\user01\OneDrive\友達と共有", "往復");
                Check(FriendInvite.TryDecode("これ使って → " + code + " よろしく") == @"C:\Users\user01\OneDrive\友達と共有", "文章の中から読み取る");
                Check(FriendInvite.TryDecode("AIUB-FRIEND-1:!!!!") == null && FriendInvite.TryDecode(null) == null && FriendInvite.TryDecode("AIUB-INVITE-1:abcdefgh") == null, "不正なコードは null");
            }

            Console.WriteLine("== フレンド：自分の公開内容");
            {
                string root = Tmp();
                var store = FakeStoreWithQuota(root, now);
                var cfg = new FriendsConfig { Id = NewId(), Folder = root, DisplayName = "user01", Enabled = true };
                var me = Friends.BuildMine(store, cfg, now);
                Check(me.ClaudeFiveHour == 70 && me.ClaudeWeekly == 45, "Claudeの残り");
                Check(me.Codex.Count == 2 && me.Codex.Any(w => w.Remaining == 87.5), "Codexの残り");
                string json = Json.Serialize(me.ToJson());
                Check(!json.Contains("@") && !json.Contains("someone"), "メールアドレスを書かない");
                Check(!json.Contains("token") && !json.Contains("prompt") && !json.Contains("plan"), "トークン数・依頼文・プランを書かない");

                store.Claude.SampleAtUtc = now.AddHours(-2);
                Check(Friends.BuildMine(store, cfg, now).ClaudeFiveHour == null, "古いClaudeの情報は載せない");
                store.Codex.Main.Windows[0].ResetsAtUtc = now.AddMinutes(-1);
                Check(Friends.BuildMine(store, cfg, now).Codex.Count == 1, "リセット済みのCodex枠は載せない");
                store.Codex.ObservedAtUtc = now.AddHours(-3);
                Check(Friends.BuildMine(store, cfg, now).Codex.Count == 0, "古いCodexの情報は載せない");
            }

            Console.WriteLine("== フレンド：共有フォルダの読み書き");
            {
                string shared = Tmp();
                var cfg = new FriendsConfig { Id = NewId(), Folder = shared, DisplayName = "me", Enabled = true };
                var me = SampleFriend(cfg.Id, "me", now, 50, 50, 50);
                Friends.Publish(cfg, me);
                Check(File.Exists(Path.Combine(shared, Friends.FolderName, cfg.Id + ".json")), "自分のファイルを書く");

                string fresh = NewId(), old = NewId();
                WriteFriendFile(shared, SampleFriend(fresh, "すずき", now.AddMinutes(-3), 80, 60, 90, 70));
                WriteFriendFile(shared, SampleFriend(old, "あべ", now.AddMinutes(-45), 10, 20));
                WriteFriendFile(shared, SampleFriend(NewId(), "とても古い", now.AddDays(-8), 10, 20));
                WriteFriendFile(shared, SampleFriend(NewId(), "未来", now.AddHours(3), 10, 20));
                string dir = Path.Combine(shared, Friends.FolderName);
                // 他人のファイル名で別idを名乗るもの
                var spoof = SampleFriend(NewId(), "なりすまし", now, 1, 1);
                File.WriteAllText(Path.Combine(dir, NewId() + ".json"), Json.Serialize(spoof.ToJson()));
                File.WriteAllText(Path.Combine(dir, NewId() + ".json"), "{壊れた");
                File.WriteAllText(Path.Combine(dir, NewId() + ".json"), new string(' ', Friends.MaxFileBytes + 10));
                File.WriteAllText(Path.Combine(dir, "notes.json"), Json.Serialize(SampleFriend(NewId(), "名前違い", now, 1, 1).ToJson()));

                var list = Friends.ReadOthers(cfg, now);
                Check(list.Count == 2, "有効なフレンドだけを読む", "count=" + list.Count + " " + string.Join(",", list.Select(x => x.Name)));
                Check(!list.Any(x => x.Id == cfg.Id), "自分は含めない");
                Check(list.Count == 2 && list[0].Id == fresh && list[1].Id == old, "新しく更新された人が上、古い人は下");
                Check(!list.Any(x => x.Name == "なりすまし" || x.Name == "未来" || x.Name == "とても古い"), "なりすまし・未来・7日超は表示しない");

                Friends.Withdraw(cfg);
                Check(!File.Exists(Path.Combine(dir, cfg.Id + ".json")), "公開をやめると自分のファイルを消す");
            }

            Console.WriteLine("== フレンド：設定と定期更新");
            {
                string root = Tmp(), shared = Tmp();
                var store = FakeStoreWithQuota(root, now);
                var svc = new FriendsService(store);
                Check(!svc.Active && Friends.ValidId(svc.Config.Id), "初期状態はオフ（idは作る）");
                svc.Tick();
                Check(!Directory.Exists(Path.Combine(shared, Friends.FolderName)), "オフの間は何も書かない");

                string id = svc.Config.Id;
                svc.Apply(new FriendsConfig { Folder = shared, DisplayName = "user01", Enabled = true });
                Check(svc.Config.Id == id && FriendsConfig.Load(store.Paths.DataDir).Folder == shared, "設定を保存（idは変えない）");
                WriteFriendFile(shared, SampleFriend(NewId(), "すずき", now, 80, 60, 90));
                svc.Tick();
                Check(svc.LastError == null && svc.Others.Count == 1 && svc.Mine != null && svc.Mine.ClaudeFiveHour == 70, "書いて読む");
                string mine = Path.Combine(shared, Friends.FolderName, id + ".json");
                Check(File.Exists(mine), "自分のファイルがある");

                svc.Apply(new FriendsConfig { Folder = shared, DisplayName = "user01", Enabled = false });
                Check(!File.Exists(mine), "オフにすると自分のファイルを消す");

                svc.Apply(new FriendsConfig { Folder = Path.Combine(shared, "存在しない"), DisplayName = "user01", Enabled = true });
                svc.Tick();
                Check(svc.LastError != null, "フォルダがなければエラーを表示（落ちない）");

                store.Policy = new Policy { DisableFriends = true };
                svc.Apply(new FriendsConfig { Folder = shared, DisplayName = "user01", Enabled = true });
                svc.Tick();
                Check(!svc.Allowed && !svc.Active && svc.Others.Count == 0 && !File.Exists(mine), "組織のポリシーで無効なら読み書きしない");
            }
        }

        /// <summary>フレンドの残り枠の見本画像（利用枠タブ）</summary>
        static void RenderFriends(string outDir)
        {
            Directory.CreateDirectory(outDir);
            var now = DateTime.UtcNow;
            string root = Tmp(), shared = Tmp();
            var store = FakeStoreWithQuota(root, now);
            store.Codex.AccountEmail = null;   // 見本画像にメールアドレスを写さない
            var activity = new Activity(store);
            var svc = new FriendsService(store);

            foreach (bool dark in new[] { false, true })
                using (var d = new Dashboard(store, activity))
                {
                    d.Friends = svc;
                    d.ApplyTheme(dark);
                    d.Tab = DashTab.Usage;
                    SaveDash(d, Path.Combine(outDir, (dark ? "dark" : "light") + "-friends-setup.png"));
                }

            svc.Apply(new FriendsConfig { Folder = shared, DisplayName = "user01", Enabled = true });
            WriteFriendFile(shared, SampleFriend(NewId(), "たなか", now.AddMinutes(-2), 82, 64, 91, 58));
            WriteFriendFile(shared, SampleFriend(NewId(), "すずき", now.AddMinutes(-6), 14, 47, 33, 72));
            WriteFriendFile(shared, SampleFriend(NewId(), "さとう", now.AddMinutes(-4), 6, 38));
            WriteFriendFile(shared, SampleFriend(NewId(), "やまだ", now.AddMinutes(-95), 50, 50, 60, 60));
            svc.Tick();
            foreach (bool dark in new[] { false, true })
                using (var d = new Dashboard(store, activity))
                {
                    d.Friends = svc;
                    d.ApplyTheme(dark);
                    d.Tab = DashTab.Usage;
                    SaveDash(d, Path.Combine(outDir, (dark ? "dark" : "light") + "-friends.png"));
                }
            // 設定画面：開いたらすぐ画像に保存して、キャンセルで閉じる
            var timer = new System.Windows.Forms.Timer { Interval = 600 };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                foreach (System.Windows.Forms.Form f in System.Windows.Forms.Application.OpenForms)
                {
                    if (f.Text != "フレンドの残り枠") continue;
                    using (var bmp = new System.Drawing.Bitmap(f.Width, f.Height))
                    {
                        f.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, f.Width, f.Height));
                        bmp.Save(Path.Combine(outDir, "friends-dialog.png"));
                    }
                    f.DialogResult = System.Windows.Forms.DialogResult.Cancel;
                    f.Close();
                    break;
                }
            };
            timer.Start();
            // 見本画像にこのPCのユーザー名を含むパスを写さない（設定だけ差し替え。フォルダの存在は確認しない）
            svc.Apply(new FriendsConfig { Folder = @"C:\Users\user01\OneDrive\フレンド共有", DisplayName = "user01", Enabled = true });
            FriendsUi.Configure(null, svc);
            Console.WriteLine("rendered to " + outDir);
        }

        static void SaveDash(Dashboard d, string file)
        {
            int w = (int)(Dashboard.LogicalWidth * d.DeviceDpi / 96f);
            using (var probe = new System.Drawing.Bitmap(w, 700)) using (var g = System.Drawing.Graphics.FromImage(probe)) d.RenderTo(g, probe.Size);
            int h = (int)Math.Min(6000, d.ContentHeight + d.ChromeHeight + 2);
            using (var bmp = new System.Drawing.Bitmap(w, h)) using (var g = System.Drawing.Graphics.FromImage(bmp)) { d.RenderTo(g, bmp.Size); bmp.Save(file); }
        }
    }
}
