using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace AIUsageBar.Tests
{
    /// <summary>
    /// 「他の人のPC」で起こりうる違いを再現する互換性テスト。
    /// 何も入っていないPC、一部だけ入っているPC、Codex CLI の入れ方・版の違い、画面の拡大率、
    /// フォント・地域設定の違い、.NET の版を確認する。
    /// </summary>
    public static partial class TestMain
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CreateHardLink(string newFile, string existingFile, IntPtr reserved);

        static void Compat(string realDataDir, string outDir)
        {
            Directory.CreateDirectory(outDir);
            string savedPath = Environment.GetEnvironmentVariable("PATH");
            string minimalPath = Environment.SystemDirectory + ";" + Hardening.WindowsDir;

            Console.WriteLine("== 他のPC：何も入っていない");
            RunScenario("empty", outDir, p => { }, s =>
            {
                Check(s.CodexError == CodexClient.MsgNotFound, "Codex CLI なし → 「見つかりません」を表示", s.CodexError);
                Check(s.Claude == null && s.ClaudeError == ClaudeUsage.MsgNoFile, "Claude Desktop なし → 案内を表示", s.ClaudeError);
                Check(s.Logs.SessionCount == 0, "ログなし → 0件");
            }, minimalPath);

            Console.WriteLine("== 他のPC：Claude Desktop（通常インストール版）だけ");
            RunScenario("claude-desktop-only", outDir, p =>
            {
                string f = p.ClaudeHistoryCandidates[0]; // %APPDATA%\Claude\plan-usage-history.json
                Directory.CreateDirectory(Path.GetDirectoryName(f));
                long now = Fmt.ToUnixMs(DateTime.UtcNow.AddMinutes(-3));
                File.WriteAllText(f, "{\"version\":2,\"samples\":[{\"t\":" + now + ",\"org\":\"x\",\"u\":{\"fh\":35,\"sd\":62}}]}");
            }, s =>
            {
                Check(s.Claude != null && s.Claude.FiveHourRemaining == 65 && s.Claude.WeeklyRemaining == 38, "通常インストール版の保存場所を読む", s.ClaudeError);
                Check(s.ClaudeMinRemaining(DateTime.UtcNow) == 38, "トレイ表示は少ない方（38%）");
            }, minimalPath);

            Console.WriteLine("== 他のPC：Claude Code だけ");
            RunScenario("claude-code-only", outDir, p =>
            {
                var now = DateTime.UtcNow.AddMinutes(-20);
                string sid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
                WriteLines(Path.Combine(p.ClaudeProjects, "C--Users-someone-project", sid + ".jsonl"),
                    "{\"type\":\"ai-title\",\"aiTitle\":\"別PCでの作業\",\"sessionId\":\"" + sid + "\"}",
                    ClaudeUser(sid, now, "READMEを直して"),
                    ClaudeAssistant(sid, "msg_x", "req_x", now.AddSeconds(3), 3, 400, 12000, 900));
            }, s =>
            {
                var day = DateTime.UtcNow.AddMinutes(-20).ToLocalTime().Date;
                Check(s.Logs.SessionsForDay(day).Count == 1 && s.Logs.DayTotal(Tool.ClaudeCode, day).Total == 13303, "Claude Code の記録だけでも集計・一覧できる");
            }, minimalPath);

            // 本物の Codex CLI（このPCに入っているもの）
            string realCli = CodexClient.FindCli(AppPaths.Default());
            Console.WriteLine("== 他のPC：Codex を npm でインストール（codex.cmd と同梱 exe）");
            if (realCli == null || !realCli.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("  SKIP  このPCに Codex CLI がないため省略");
            }
            else
            {
                RunScenario("codex-npm", outDir, p =>
                {
                    string npm = Path.Combine(p.RoamingAppData, "npm");
                    string vendor = Path.Combine(npm, "node_modules", "@openai", "codex", "vendor", "x86_64-pc-windows-msvc", "codex");
                    Directory.CreateDirectory(vendor);
                    File.WriteAllText(Path.Combine(npm, "codex.cmd"), "@echo off\r\nnode \"%~dp0node_modules\\@openai\\codex\\bin\\codex.js\" %*\r\n");
                    string target = Path.Combine(vendor, "codex.exe");
                    if (!CreateHardLink(target, realCli, IntPtr.Zero)) File.Copy(realCli, target);
                }, s =>
                {
                    Check(s.CodexCliPath != null && s.CodexCliPath.EndsWith(@"vendor\x86_64-pc-windows-msvc\codex\codex.exe"), "npm の codex.cmd から同梱の codex.exe を見つける（cmd.exe を使わない）", s.CodexCliPath);
                    Check(s.Codex != null && s.Codex.Main != null && s.Codex.Main.Windows.Count > 0, "npm 版の場所からでも利用枠を取得できる", s.CodexError);
                }, minimalPath);
            }

            Console.WriteLine("== 他のPC：Codex CLI の版の違い・異常");
            string fakeDir = BuildFakeCodex();
            if (fakeDir == null) { Check(false, "検査用の偽 Codex CLI を作成"); }
            else
            {
                string fake = Path.Combine(fakeDir, "codex.exe");
                Func<string, int, Tuple<CodexSnapshot, string, TimeSpan>> run = (mode, timeoutSec) =>
                {
                    File.WriteAllText(Path.Combine(fakeDir, "mode.txt"), mode);
                    var sw = Stopwatch.StartNew();
                    try { return Tuple.Create(CodexClient.Fetch(fake, TimeSpan.FromSeconds(timeoutSec), Path.Combine(Tmp(), "work")), (string)null, sw.Elapsed); }
                    catch (CodexException ex) { return Tuple.Create((CodexSnapshot)null, ex.Message, sw.Elapsed); }
                };

                var r = run("old", 10);
                Check(r.Item1 != null && Math.Abs(r.Item1.Main.Windows[0].RemainingPercent - 58) < 0.01, "古い CLI（追加パラメーター非対応）→ パラメーターなしで再試行して取得", r.Item2);
                Check(r.Item1 != null && r.Item1.Daily == null && r.Item1.HistoryError != null, "古い CLI（日別トークン非対応）→ 利用枠だけ表示し、履歴は「未取得」");
                r = run("nomethod", 10);
                Check(r.Item1 == null && r.Item2 == CodexClient.MsgOldCli, "とても古い CLI（利用枠の取得に非対応）→ 更新を案内", r.Item2);
                r = run("noauth", 10);
                Check(r.Item1 == null && r.Item2 == CodexClient.MsgNeedLogin, "未ログイン → 「ChatGPTアカウントでのログインが必要」", r.Item2);
                r = run("garbage", 10);
                Check(r.Item1 == null && (r.Item2 == CodexClient.MsgClosed || r.Item2 == CodexClient.MsgFailed), "CLI が不正な出力をして終了 → 落ちずにエラー表示", r.Item2);
                r = run("hang", 3);
                Thread.Sleep(1500);
                int left = Process.GetProcessesByName("codex").Count(pr => { try { return pr.MainModule.FileName.StartsWith(fakeDir, StringComparison.OrdinalIgnoreCase); } catch { return false; } });
                Check(r.Item1 == null && r.Item2 == CodexClient.MsgTimeout && r.Item3.TotalSeconds < 10 && left == 0, "CLI が応答しない → タイムアウトし、子プロセスを終了", r.Item2 + " " + r.Item3.TotalSeconds.ToString("0.0") + "s left=" + left);
            }

            Console.WriteLine("== 他のPC：画面の拡大率（125% / 150% / 200%）");
            {
                var paths = AppPaths.Default();
                paths.DataDir = realDataDir;
                var store = new UsageStore(paths, new Settings(), null);
                store.LoadCodexCache();
                store.RefreshClaude();
                store.Logs.Load();
                store.Logs.Scan(null);
                var activity = new Activity(store);
                foreach (float sc in new[] { 1.25f, 1.5f, 2.0f })
                {
                    Dashboard.ScaleOverride = sc;
                    try
                    {
                        using (var d = new Dashboard(store, activity))
                        {
                            d.ApplyTheme(false);
                            foreach (var tab in new[] { DashTab.Usage, DashTab.Log, DashTab.Settings })
                            {
                                d.Tab = tab;
                                int w = (int)(Dashboard.LogicalWidth * d.UiScale);
                                using (var probe = new System.Drawing.Bitmap(w, (int)(700 * sc))) { probe.SetResolution(96 * sc, 96 * sc); using (var g = System.Drawing.Graphics.FromImage(probe)) d.RenderTo(g, probe.Size); }
                                int h = (int)Math.Min(9000, d.ContentHeight + d.ChromeHeight + 2);
                                using (var bmp = new System.Drawing.Bitmap(w, h))
                                {
                                    bmp.SetResolution(96 * sc, 96 * sc);
                                    using (var g = System.Drawing.Graphics.FromImage(bmp)) d.RenderTo(g, bmp.Size);
                                    bmp.Save(Path.Combine(outDir, "dpi" + (int)(sc * 100) + "-" + tab.ToString().ToLower() + ".png"));
                                }
                            }
                        }
                        Check(true, "拡大率 " + (int)(sc * 100) + "% で全タブを描画");
                    }
                    catch (Exception ex) { Check(false, "拡大率 " + (int)(sc * 100) + "% で全タブを描画", ex.Message); }
                    finally { Dashboard.ScaleOverride = null; }
                }
                foreach (int px in new[] { 20, 24, 32, 40 })
                {
                    try { using (var b = TrayIcons.Render(TrayKind.Codex, 36, px)) using (TrayIcons.ToIcon(b)) { } using (TrayIcons.MultiSizeIcon(sz => TrayIcons.RenderPair(36, 75, sz), new[] { 16, 24, 32, 48, 64 })) { } Check(true, "トレイ・タスクバーのアイコン " + px + "px"); }
                    catch (Exception ex) { Check(false, "トレイ・タスクバーのアイコン " + px + "px", ex.Message); }
                }

                Console.WriteLine("== 他のPC：英語版 Windows・日本語フォントの違い");
                var savedCulture = Thread.CurrentThread.CurrentCulture;
                var savedUi = Thread.CurrentThread.CurrentUICulture;
                try
                {
                    Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US");
                    Thread.CurrentThread.CurrentUICulture = new CultureInfo("en-US");
                    string md = activity.ExportMarkdown(new List<DateTime> { DateTime.Now.Date }, null, true);
                    string csv = activity.ExportCsv(new List<DateTime> { DateTime.Now.Date }, null, true);
                    string sec = SecurityInfo.Build(store);
                    Check(md.Contains("## " + DateTime.Now.ToString("yyyy-MM-dd")) && csv.Length > 0 && sec.Contains("SHA-256"), "英語の地域設定でも書き出しが動く（日付は日本語表記のまま）");
                    Check(Fmt.DateTimeShort(new DateTime(2026, 9, 17, 9, 5, 0)) == "9/17(木) 09:05", "英語の地域設定でも曜日は日本語", Fmt.DateTimeShort(new DateTime(2026, 9, 17, 9, 5, 0)));

                    Theme.ExcludeFontsForTests = new[] { "Yu Gothic UI" };
                    Theme.ResetFontForTests();
                    string font = Theme.UiFontFamily;
                    using (var d = new Dashboard(store, activity))
                    {
                        d.ApplyTheme(true);
                        d.Tab = DashTab.Usage;
                        using (var bmp = new System.Drawing.Bitmap(Dashboard.LogicalWidth, 700)) using (var g = System.Drawing.Graphics.FromImage(bmp))
                        {
                            d.RenderTo(g, bmp.Size);
                            bmp.Save(Path.Combine(outDir, "font-fallback-usage.png"));
                        }
                    }
                    Check(font != "Yu Gothic UI" && !string.IsNullOrEmpty(font), "Yu Gothic UI がないPC → 代わりのフォント（" + font + "）で表示");
                }
                catch (Exception ex) { Check(false, "英語版 Windows・フォントの違い", ex.ToString()); }
                finally
                {
                    Thread.CurrentThread.CurrentCulture = savedCulture;
                    Thread.CurrentThread.CurrentUICulture = savedUi;
                    Theme.ExcludeFontsForTests = null;
                    Theme.ResetFontForTests();
                }
            }

            Console.WriteLine("== 他のPC：.NET Framework の版");
            Check(Hardening.IsFrameworkSupported(533320) && Hardening.IsFrameworkSupported(528040) && Hardening.IsFrameworkSupported(461808), ".NET 4.8.1 / 4.8 / 4.7.2 は起動する");
            Check(!Hardening.IsFrameworkSupported(461308) && !Hardening.IsFrameworkSupported(394802), ".NET 4.7.1 以前は起動せず、更新を案内する");
            Check(Hardening.IsFrameworkSupported(null), "版が読み取れない場合は起動を妨げない");
            Console.WriteLine("  このPC：Release=" + Hardening.FrameworkRelease());

            Console.WriteLine("== 他のPC：標準入力の BOM 対策（システムのコードページが UTF-8 のPC）");
            CodexClient.PreventStdinBom();
            Check(Console.InputEncoding.GetPreamble().Length == 0, "子プロセスへの入力に BOM を付けない設定になっている", Console.InputEncoding.WebName);

            Environment.SetEnvironmentVariable("PATH", savedPath);
        }

        static void RunScenario(string name, string outDir, Action<AppPaths> setup, Action<UsageStore> verify, string path)
        {
            string saved = Environment.GetEnvironmentVariable("PATH");
            try
            {
                Environment.SetEnvironmentVariable("PATH", path);
                var paths = FakePaths(Tmp());
                setup(paths);
                var store = new UsageStore(paths, new Settings(), null);
                store.RefreshCodex();
                store.RefreshClaude();
                store.Logs.Scan(null);
                store.Logs.Save();
                var activity = new Activity(store);
                activity.ExportMarkdown(new List<DateTime> { DateTime.Now.Date }, null, true);
                activity.ExportCsv(new List<DateTime> { DateTime.Now.Date }, null, true);
                SecurityInfo.Build(store);
                store.Detect();
                using (var d = new Dashboard(store, activity))
                {
                    d.ApplyTheme(false);
                    foreach (var tab in new[] { DashTab.Usage, DashTab.History, DashTab.Log, DashTab.Settings })
                    {
                        d.Tab = tab;
                        using (var bmp = new System.Drawing.Bitmap(Dashboard.LogicalWidth, 700)) using (var g = System.Drawing.Graphics.FromImage(bmp))
                        {
                            d.RenderTo(g, bmp.Size);
                            if (tab == DashTab.Usage) bmp.Save(Path.Combine(outDir, name + "-usage.png"));
                        }
                    }
                }
                Check(true, "画面・書き出し・仕様情報がエラーなく動く");
                verify(store);
            }
            catch (Exception ex) { Check(false, name + "：エラーなく動く", ex.ToString()); }
            finally { Environment.SetEnvironmentVariable("PATH", saved); }
        }

        /// <summary>検査用の偽 Codex CLI（JSON-RPC で古い版・未ログイン・異常を再現）をその場でコンパイルする</summary>
        static string BuildFakeCodex()
        {
            string dir = Path.Combine(Tmp(), "fake-codex");
            Directory.CreateDirectory(dir);
            const string src = @"
using System; using System.IO; using System.Text; using System.Threading;
public static class FakeCodex {
  public static void Main() {
    string dir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
    string mode = File.ReadAllText(Path.Combine(dir, ""mode.txt"")).Trim();
    var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
    var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)); output.AutoFlush = true; output.NewLine = ""\n"";
    if (mode == ""garbage"") { output.WriteLine(""this is not json""); return; }
    if (mode == ""hang"") { Thread.Sleep(600000); return; }
    long now = (long)(DateTime.UtcNow - new DateTime(1970,1,1)).TotalSeconds;
    string line;
    while ((line = input.ReadLine()) != null) {
      string id = Id(line); if (id == null) continue;
      if (line.Contains(""\""initialize\"""")) { output.WriteLine(""{\""id\"":"" + id + "",\""result\"":{}}""); continue; }
      if (line.Contains(""account/rateLimits/read"")) {
        if (mode == ""nomethod"") output.WriteLine(""{\""id\"":"" + id + "",\""error\"":{\""code\"":-32601,\""message\"":\""Method not found\""}}"");
        else if (mode == ""noauth"") output.WriteLine(""{\""id\"":"" + id + "",\""error\"":{\""code\"":-32000,\""message\"":\""authentication required: sign in with ChatGPT\""}}"");
        else if (mode == ""old"" && line.Contains(""\""params\"""")) output.WriteLine(""{\""id\"":"" + id + "",\""error\"":{\""code\"":-32602,\""message\"":\""Invalid params: unknown field excludeResetCreditDetails\""}}"");
        else output.WriteLine(""{\""id\"":"" + id + "",\""result\"":{\""rateLimits\"":{\""limitId\"":\""codex\"",\""primary\"":{\""usedPercent\"":42,\""windowDurationMins\"":300,\""resetsAt\"":"" + (now + 3600) + ""},\""secondary\"":{\""usedPercent\"":10,\""windowDurationMins\"":10080,\""resetsAt\"":"" + (now + 86400) + ""},\""planType\"":\""plus\""}}}"");
        continue;
      }
      output.WriteLine(""{\""id\"":"" + id + "",\""error\"":{\""code\"":-32601,\""message\"":\""Method not found\""}}"");
    }
  }
  static string Id(string s) { int i = s.IndexOf(""\""id\"":""); if (i < 0) return null; i += 5; int j = i; while (j < s.Length && char.IsDigit(s[j])) j++; return j > i ? s.Substring(i, j - i) : null; }
}";
            try
            {
                using (var provider = new Microsoft.CSharp.CSharpCodeProvider())
                {
                    var opts = new System.CodeDom.Compiler.CompilerParameters { GenerateExecutable = true, OutputAssembly = Path.Combine(dir, "codex.exe") };
                    var result = provider.CompileAssemblyFromSource(opts, src);
                    if (result.Errors.HasErrors)
                    {
                        foreach (System.CodeDom.Compiler.CompilerError e in result.Errors) Console.WriteLine("  compile: " + e);
                        return null;
                    }
                }
                return dir;
            }
            catch (Exception ex) { Console.WriteLine("  " + ex.Message); return null; }
        }
    }
}
