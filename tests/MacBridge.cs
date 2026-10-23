using System;
using System.Collections.Generic;
using System.Linq;

namespace AIUsageBar.Tests
{
    /// <summary>
    /// Mac 用ツール（Python）との相互検証用の入口。
    ///   create : 指定フォルダにチームを作り、メンバーを1人追加して招待コードを出す（マスターの鍵は dataDir に保存）
    ///   read   : そのマスターとして報告を読み、各報告の要約を JSON で出す
    /// これにより「Python が作った報告を C# マスターが読めるか」「C# の team.json を Python が検証できるか」を確認する。
    /// </summary>
    public static partial class TestMain
    {
        static int MacBridge(string[] a)
        {
            string cmd = a[1];
            if (cmd == "create")
            {
                string shared = a[2], masterEmail = a[3], memberEmail = a[4], dataDir = a[5];
                var svc = BridgeService(dataDir);
                svc.CreateTeam(shared, "ブリッジ検証", "マスター", masterEmail, "bridge passphrase 7x");
                svc.AddMembers(TeamConfig.ParseMemberList(memberEmail, null), ShareScope.Prompts, "マスター", null);
                Console.WriteLine("FOLDER=" + svc.Local.SharedFolder);
                Console.WriteLine("FINGERPRINT=" + svc.Team.Fingerprint);
                Console.WriteLine("INVITE=" + TeamInvite.Encode(svc.Local.SharedFolder, svc.Team.TeamName, svc.Team.Fingerprint));
                return 0;
            }
            if (cmd == "read")
            {
                string dataDir = a[2];
                var svc = BridgeService(dataDir);
                svc.LoadTeam();
                foreach (var r in svc.ReadReports())
                {
                    var row = new Dictionary<string, object>
                    {
                        { "installId", r.InstallId }, { "error", r.Error }, { "sigOk", r.SignatureOk },
                        { "approved", r.Approved }, { "autoApproved", r.AutoApproved }, { "scope", ScopeText.Key(r.Scope) },
                        { "machine", r.Machine }, { "windowsUser", r.WindowsUser }, { "declaredEmail", r.DeclaredEmail },
                        { "detected", string.Join(",", r.DetectedEmails) }, { "appVersion", r.AppVersion },
                        { "claudeTotal", r.Daily.Where(d => d.Tool == Tool.ClaudeCode).Sum(d => d.Sum.Total) },
                        { "codexTotal", r.Daily.Where(d => d.Tool == Tool.Codex).Sum(d => d.Sum.Total) },
                        { "codexService", r.CodexServiceDaily.Sum(d => d.Tokens) },
                        { "sessions", r.Sessions.Count }, { "prompts", r.Prompts.Count },
                        { "quotaCodex", QuotaField(r, "codex", "fiveHourRemaining") }, { "quotaCodexWeek", QuotaField(r, "codex", "weeklyRemaining") },
                        { "quotaClaude", QuotaField(r, "claude", "fiveHourRemaining") },
                        { "promptTexts", r.Prompts.Select(p => Json.Str(p, "text")).ToList() },
                        { "fingerprint", r.MemberFingerprint },
                    };
                    Console.WriteLine("REPORT=" + Json.Serialize(row));
                }
                return 0;
            }
            if (cmd == "friend-write")
            {
                // C# がフレンドの残り枠ファイルを書く（Python が読めるか検証）
                string dir = a[2], id = a[3], name = a[4];
                var cfg = new FriendsConfig { Id = id, Folder = dir, DisplayName = name, Enabled = true };
                var mine = new FriendStatus
                {
                    Id = id, Name = name, UpdatedAtUtc = DateTime.UtcNow,
                    ClaudeFiveHour = 88, ClaudeWeekly = 42,
                    Codex = { new FriendWindow { Label = "5時間", Remaining = 70, ResetsAtUtc = DateTime.UtcNow.AddHours(2) } },
                };
                Friends.Publish(cfg, mine);
                Console.WriteLine("WROTE=" + Friends.FileFor(cfg));
                return 0;
            }
            if (cmd == "friend-read")
            {
                // C# が共有フォルダのフレンドを読む（Python が書いたものを読めるか検証）
                string dir = a[2], myId = a[3];
                var cfg = new FriendsConfig { Id = myId, Folder = dir, Enabled = true };
                foreach (var s in Friends.ReadOthers(cfg, DateTime.UtcNow))
                    Console.WriteLine("FRIEND=" + Json.Serialize(new Dictionary<string, object>
                    {
                        { "id", s.Id }, { "name", s.Name }, { "claude5h", s.ClaudeFiveHour }, { "claudeWeek", s.ClaudeWeekly },
                        { "codex", s.Codex.Select(w => (object)new Dictionary<string, object> { { "label", w.Label }, { "remaining", w.Remaining } }).ToList() },
                        { "min", s.MinRemaining },
                    }));
                return 0;
            }
            Console.WriteLine("usage: --mac-bridge create|read|friend-write|friend-read ...");
            return 2;
        }

        static object QuotaField(TeamReport r, string tool, string field)
        {
            if (r.Quota == null) return null;
            var q = Json.Obj(r.Quota, tool);
            return q == null ? null : Json.Num(q, field);
        }

        static TeamService BridgeService(string dataDir)
        {
            var paths = AppPaths.Default();
            paths.DataDir = dataDir;
            var store = new UsageStore(paths, new Settings(), null);
            var act = new Activity(store);
            return new TeamService(store, act);
        }
    }
}
