using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("AI Usage Bar セットアップ")]
[assembly: AssemblyProduct("AI Usage Bar")]
[assembly: AssemblyDescription("AI Usage Bar のインストール・アンインストール（ユーザーごと・管理者権限不要）")]
[assembly: AssemblyVersion("0.1.0.0")]
[assembly: AssemblyFileVersion("0.1.0.0")]
[assembly: AssemblyInformationalVersion("0.1.0")]
// P/Invoke の DLL は System32 からだけ読み込む（セットアップと同じフォルダ＝ダウンロード等のDLLを使わない）
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace AIUsageBarSetup
{
    /// <summary>
    /// AI Usage Bar のセットアップ。
    /// ・インストール先は %LOCALAPPDATA%\Programs\AI Usage Bar（このWindowsユーザーだけ。管理者権限・UACなし）
    /// ・スタートメニューのショートカット、「アプリと機能」への登録、アンインストーラー
    /// ・一括配布用の無音インストール（/quiet）
    /// </summary>
    public static class Program
    {
        public const string Name = "AI Usage Bar";
        public const string Version = "0.1.0";
        public const string ExeName = "AIUsageBar.exe";
        public const string UninstallerName = "uninstall.exe";
        public const string InstanceGuid = "6f3c2a9e-6b1d-4f0e-9a51-2c8f0d6e7b41";   // アプリ本体と同じ値
        public const int MinimumFrameworkRelease = 461808;                          // .NET Framework 4.7.2

        public const int ExitOk = 0, ExitError = 1, ExitBadArgs = 2, ExitFramework = 3, ExitCancelled = 1602;

        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetDefaultDllDirectories(uint flags);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool SetDllDirectory(string path);

        [STAThread]
        public static int Main(string[] args)
        {
            try { SetDllDirectory(""); } catch { }
            try { SetDefaultDllDirectories(0x00000800); } catch { }
            // 作業フォルダをインストール先・ダウンロード等から外す（アンインストール時にフォルダを削除できるように）
            try { Directory.SetCurrentDirectory(Environment.SystemDirectory); } catch { }

            Options opt;
            string argError;
            if (!Options.TryParse(args, out opt, out argError))
            {
                if (argError == "help") { ShowUsage(null); return ExitOk; }
                if (opt == null || !opt.Quiet) ShowUsage(argError);
                return ExitBadArgs;
            }
            var log = new SetupLog(opt.LogPath ?? (opt.Quiet ? Path.Combine(Path.GetTempPath(), "AIUsageBar-Setup.log") : null));
            log.Write("開始：" + string.Join(" ", args) + "（版 " + Version + "）");
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            try
            {
                Engine.CleanupOldUninstallerCopies();
                if (opt.RenderDir != null) { SetupForm.RenderSamples(opt); return ExitOk; }

                int? release = FrameworkRelease();
                if (release != null && release.Value < MinimumFrameworkRelease)
                {
                    log.Write(".NET Framework が古い：" + release);
                    if (!opt.Quiet) MessageBox.Show("このPCの .NET Framework が古いため、AI Usage Bar を使えません。\n\nWindows Update で .NET Framework 4.8（4.7.2 以上）をインストールしてから、もう一度実行してください。", Name + " セットアップ", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return ExitFramework;
                }

                var loc = opt.TestRoot != null ? Locations.ForTest(opt.TestRoot) : Locations.Real();
                var engine = new Engine(loc, opt, log);
                if (opt.Quiet)
                {
                    int code = opt.Uninstall ? engine.Uninstall(null) : engine.Install(null);
                    log.Write("終了コード：" + code);
                    return code;
                }
                using (var form = new SetupForm(engine, opt.Uninstall))
                {
                    Application.Run(form);
                    log.Write("終了コード：" + form.ExitCode);
                    return form.ExitCode;
                }
            }
            catch (Exception ex)
            {
                log.Write("予期しないエラー：" + ex);
                if (!opt.Quiet) MessageBox.Show("セットアップを完了できませんでした。\n\n" + ex.Message, Name + " セットアップ", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return ExitError;
            }
            finally { log.Flush(); }
        }

        static void ShowUsage(string error)
        {
            string text = (error != null ? error + "\n\n" : "") +
                "使い方：\n" +
                "  AI-Usage-Bar-Setup-" + Version + ".exe                       画面でインストール\n" +
                "  AI-Usage-Bar-Setup-" + Version + ".exe /quiet               画面を出さずにインストール\n" +
                "  オプション：/desktop（デスクトップにショートカット） /startup（サインイン時に起動） /launch（インストール後に起動） /log:ファイル\n\n" +
                "  uninstall.exe /uninstall                  画面でアンインストール\n" +
                "  uninstall.exe /uninstall /quiet           画面を出さずにアンインストール（/removedata で設定・データも削除）\n\n" +
                "終了コード：0 成功、1 失敗、2 引数の誤り、3 .NET Framework が古い、1602 キャンセル";
            MessageBox.Show(text, Name + " セットアップ", MessageBoxButtons.OK, error != null ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }

        public static int? FrameworkRelease()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                {
                    var v = k == null ? null : k.GetValue("Release");
                    return v is int ? (int?)(int)v : null;
                }
            }
            catch { return null; }
        }

        public static string SelfPath
        {
            get
            {
                try { using (var p = Process.GetCurrentProcess()) return p.MainModule.FileName; }
                catch { return Application.ExecutablePath; }
            }
        }
    }

    public class Options
    {
        public bool Quiet, Uninstall, Desktop, Startup, Launch, NoLaunch, RemoveData;
        public string TestRoot, RenderDir, LogPath;

        public static bool TryParse(string[] args, out Options opt, out string error)
        {
            opt = new Options();
            error = null;
            foreach (var raw in args)
            {
                string a = raw.Trim();
                if (a.StartsWith("-")) a = "/" + a.TrimStart('-');
                string lower = a.ToLowerInvariant();
                string value = null;
                int colon = a.IndexOf(':');
                if (colon > 0) { value = a.Substring(colon + 1).Trim('"'); lower = lower.Substring(0, colon); }
                switch (lower)
                {
                    case "/quiet": case "/q": case "/s": case "/silent": case "/verysilent": opt.Quiet = true; break;
                    case "/uninstall": case "/u": opt.Uninstall = true; break;
                    case "/desktop": opt.Desktop = true; break;
                    case "/startup": opt.Startup = true; break;
                    case "/launch": opt.Launch = true; break;
                    case "/nolaunch": opt.NoLaunch = true; break;
                    case "/removedata": opt.RemoveData = true; break;
                    case "/log": opt.LogPath = value; break;
                    case "/testroot": opt.TestRoot = value; break;
                    case "/render": opt.RenderDir = value; break;
                    case "/?": case "/h": case "/help": error = "help"; return false;
                    default: error = "不明な指定です：" + raw; return false;
                }
            }
            if (opt.TestRoot != null)
            {
                // 検査用の差し替え先は一時フォルダの中だけに限る
                string full;
                try { full = Path.GetFullPath(opt.TestRoot); } catch { full = null; }
                string temp = Path.GetFullPath(Path.GetTempPath());
                if (full == null || !full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || full.TrimEnd('\\').Length <= temp.TrimEnd('\\').Length)
                {
                    error = "/testroot は一時フォルダの中を指定してください";
                    return false;
                }
                opt.TestRoot = full;
            }
            return true;
        }
    }

    /// <summary>書き込む場所。検査では一時フォルダとテスト用のレジストリキーに差し替える</summary>
    public class Locations
    {
        public string InstallDir, StartMenuDir, DesktopDir, DataDir;
        public string UninstallKey, RunKey, PolicyKey;
        public string ExtraArgs = "";
        public bool IsTest;

        public string ExePath { get { return Path.Combine(InstallDir, Program.ExeName); } }
        public string UninstallerPath { get { return Path.Combine(InstallDir, Program.UninstallerName); } }
        public string StartMenuLink { get { return Path.Combine(StartMenuDir, Program.Name + ".lnk"); } }
        public string DesktopLink { get { return Path.Combine(DesktopDir, Program.Name + ".lnk"); } }

        public static Locations Real()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return new Locations
            {
                InstallDir = Path.Combine(local, "Programs", Program.Name),
                StartMenuDir = Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                DesktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                DataDir = Path.Combine(local, "AIUsageBar"),
                UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AIUsageBar",
                RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run",
                PolicyKey = @"SOFTWARE\Policies\AIUsageBar",
            };
        }

        public static Locations ForTest(string root)
        {
            string id;
            using (var sha = SHA256.Create()) id = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(root.ToLowerInvariant())), 0, 6).Replace("-", "");
            string reg = @"Software\AIUsageBarSetupTest\" + id;
            return new Locations
            {
                InstallDir = Path.Combine(root, "Programs", Program.Name),
                StartMenuDir = Path.Combine(root, "StartMenu"),
                DesktopDir = Path.Combine(root, "Desktop"),
                DataDir = Path.Combine(root, "AIUsageBar"),
                UninstallKey = reg + @"\Uninstall\AIUsageBar",
                RunKey = reg + @"\Run",
                PolicyKey = reg + @"\Policies",
                ExtraArgs = " /testroot:\"" + root + "\"",
                IsTest = true,
            };
        }
    }

    public class SetupLog
    {
        readonly string path;
        readonly StringBuilder sb = new StringBuilder();
        public SetupLog(string path) { this.path = path; }
        public void Write(string s) { lock (sb) sb.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + s); }
        public void Flush()
        {
            if (path == null) return;
            try { lock (sb) File.AppendAllText(path, sb.ToString(), new UTF8Encoding(true)); } catch { }
        }
    }

    /// <summary>インストール・アンインストールの処理本体（画面なしでも動く）</summary>
    public class Engine
    {
        public readonly Locations Loc;
        readonly Options opt;
        readonly SetupLog log;

        public Engine(Locations loc, Options opt, SetupLog log)
        {
            Loc = loc;
            this.opt = opt;
            this.log = log;
        }

        public Options Opt { get { return opt; } }

        // ------------------------------------------------------------------ 状態

        public string InstalledVersion
        {
            get
            {
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(Loc.UninstallKey))
                        if (k != null) return k.GetValue("DisplayVersion") as string;
                }
                catch { }
                return File.Exists(Loc.ExePath) ? "不明" : null;
            }
        }

        public bool StartupPolicyBlocked
        {
            get { return PolicyFlag(Registry.LocalMachine, "DisableStartupRegistration") || PolicyFlag(Registry.CurrentUser, "DisableStartupRegistration"); }
        }

        bool PolicyFlag(RegistryKey hive, string name)
        {
            try
            {
                // 検査時はテスト用キー（HKCU）だけを見る
                if (Loc.IsTest && hive != Registry.CurrentUser) return false;
                using (var k = hive.OpenSubKey(Loc.PolicyKey))
                {
                    var v = k == null ? null : k.GetValue(name);
                    return v is int && (int)v != 0;
                }
            }
            catch { return false; }
        }

        public bool StartupRegistered
        {
            get
            {
                string v = RunValue();
                return v != null && string.Equals(v.Trim().Trim('"'), Loc.ExePath, StringComparison.OrdinalIgnoreCase);
            }
        }

        string RunValue()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(Loc.RunKey)) return k == null ? null : k.GetValue("AIUsageBar") as string; }
            catch { return null; }
        }

        public bool DesktopLinkExists { get { return File.Exists(Loc.DesktopLink); } }

        /// <summary>チームに参加中か（アンインストール時の注意表示用）</summary>
        public bool TeamJoined
        {
            get
            {
                try
                {
                    string p = Path.Combine(Loc.DataDir, "team-local.json");
                    if (!File.Exists(p)) return false;
                    string s = File.ReadAllText(p);
                    return s.Contains("\"teamId\"") && !s.Contains("\"teamId\": null") && !s.Contains("\"teamId\":null");
                }
                catch { return false; }
            }
        }

        // ------------------------------------------------------------------ インストール

        public class InstallChoices
        {
            public bool Desktop, Startup, Launch;
            public bool ExplicitStartupChoice;   // 画面で選んだ（外した場合は登録を消す）
            public bool ExplicitDesktopChoice;
        }

        public InstallChoices DefaultChoices()
        {
            return new InstallChoices
            {
                Desktop = opt.Desktop || (!opt.Quiet && DesktopLinkExists),
                Startup = opt.Startup || (!opt.Quiet && StartupRegistered),
                Launch = opt.Quiet ? opt.Launch : !opt.NoLaunch,
            };
        }

        /// <summary>インストールする。ui：画面から呼ぶ場合の進行表示・確認（無音なら null）</summary>
        public int Install(IProgressUi ui, InstallChoices choices = null)
        {
            choices = choices ?? DefaultChoices();
            Step(ui, "起動中の AI Usage Bar を確認しています…");
            if (!StopRunningApp(ui)) { log.Write("起動中のアプリを終了できなかった"); return ui == null ? Program.ExitError : Program.ExitCancelled; }

            Step(ui, "ファイルをコピーしています…");
            Directory.CreateDirectory(Loc.InstallDir);
            byte[] app = ReadResource(Program.ExeName);
            string expected = Encoding.ASCII.GetString(ReadResource(Program.ExeName + ".sha256")).Trim();
            string actual = Sha256(app);
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("セットアップ内のファイルが壊れています（SHA-256 が一致しません）。配布元から取得し直してください。");
            WriteFileSafely(Loc.ExePath, app);
            log.Write("本体：" + Loc.ExePath + "（SHA-256 " + actual + "）");
            WriteFileSafely(Path.Combine(Loc.InstallDir, "はじめに.txt"), ReadResource("readme.txt"));
            WriteFileSafely(Path.Combine(Loc.InstallDir, "仕様書・セキュリティ資料.md"), ReadResource("spec.md"));
            WriteFileSafely(Path.Combine(Loc.InstallDir, "他のPCでの確認手順.txt"), ReadResource("checklist.txt"));
            string self = Program.SelfPath;
            if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(Loc.UninstallerPath), StringComparison.OrdinalIgnoreCase))
                WriteFileSafely(Loc.UninstallerPath, File.ReadAllBytes(self));

            Step(ui, "ショートカットを作成しています…");
            Directory.CreateDirectory(Loc.StartMenuDir);
            ShellLink.Create(Loc.StartMenuLink, Loc.ExePath, Loc.InstallDir, "Codex・Claude の残り利用枠とトークンを確認");
            if (choices.Desktop)
            {
                Directory.CreateDirectory(Loc.DesktopDir);
                ShellLink.Create(Loc.DesktopLink, Loc.ExePath, Loc.InstallDir, "Codex・Claude の残り利用枠とトークンを確認");
            }
            else if (choices.ExplicitDesktopChoice) DeleteOurLink(Loc.DesktopLink);

            Step(ui, "Windows に登録しています…");
            using (var k = Registry.CurrentUser.CreateSubKey(Loc.UninstallKey))
            {
                string u = "\"" + Loc.UninstallerPath + "\" /uninstall" + Loc.ExtraArgs;
                k.SetValue("DisplayName", Program.Name);
                k.SetValue("DisplayVersion", Program.Version);
                k.SetValue("DisplayIcon", Loc.ExePath + ",0");
                k.SetValue("InstallLocation", Loc.InstallDir);
                k.SetValue("UninstallString", u);
                k.SetValue("QuietUninstallString", u + " /quiet");
                k.SetValue("Comments", "Codex・Claude の残り利用枠とトークン使用量を確認するツール（このユーザーのみ）");
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                k.SetValue("EstimatedSize", (int)Math.Max(1, (app.Length + new FileInfo(self).Length) / 1024), RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
            if (choices.Startup && !StartupPolicyBlocked)
            {
                using (var k = Registry.CurrentUser.CreateSubKey(Loc.RunKey)) k.SetValue("AIUsageBar", "\"" + Loc.ExePath + "\"");
                log.Write("サインイン時の起動を登録");
            }
            else if (choices.ExplicitStartupChoice && StartupRegistered)
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Loc.RunKey, true)) if (k != null) k.DeleteValue("AIUsageBar", false);
                log.Write("サインイン時の起動を解除");
            }

            if (choices.Launch)
            {
                Step(ui, "起動しています…");
                var psi = new ProcessStartInfo(Loc.ExePath) { UseShellExecute = false, WorkingDirectory = Loc.InstallDir };
                using (Process.Start(psi)) { }
            }
            log.Write("インストール完了：" + Loc.InstallDir);
            return Program.ExitOk;
        }

        // ------------------------------------------------------------------ アンインストール

        public int Uninstall(IProgressUi ui, bool? removeData = null)
        {
            bool purge = removeData ?? opt.RemoveData;
            Step(ui, "起動中の AI Usage Bar を終了しています…");
            if (!StopRunningApp(ui)) { log.Write("起動中のアプリを終了できなかった"); return ui == null ? Program.ExitError : Program.ExitCancelled; }

            Step(ui, "登録を解除しています…");
            try
            {
                string run = RunValue();
                if (run != null && IsUnder(run.Trim().Trim('"'), Loc.InstallDir))
                    using (var k = Registry.CurrentUser.OpenSubKey(Loc.RunKey, true)) if (k != null) k.DeleteValue("AIUsageBar", false);
            }
            catch (Exception ex) { log.Write("自動起動の解除に失敗：" + ex.Message); }
            DeleteOurLink(Loc.StartMenuLink);
            DeleteOurLink(Loc.DesktopLink);
            try { Registry.CurrentUser.DeleteSubKeyTree(Loc.UninstallKey, false); } catch (Exception ex) { log.Write("登録情報の削除に失敗：" + ex.Message); }
            // 招待リンク（aiusagebar:）の登録を解除（このインストール先を指している場合のみ。検査時は触らない）
            if (!Loc.IsTest)
                try
                {
                    string cmd = null;
                    using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\aiusagebar\shell\open\command"))
                        if (k != null) cmd = k.GetValue("") as string;
                    if (cmd == null || cmd.IndexOf(Loc.InstallDir, StringComparison.OrdinalIgnoreCase) >= 0)
                        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\aiusagebar", false);
                }
                catch (Exception ex) { log.Write("招待リンクの解除に失敗：" + ex.Message); }

            Step(ui, "ファイルを削除しています…");
            // セットアップが置いたファイルだけを消す（利用者が置いた他のファイルは残す）
            foreach (var name in new[] { Program.ExeName, "はじめに.txt", "仕様書・セキュリティ資料.md", "他のPCでの確認手順.txt", Program.ExeName + ".new" })
                DeleteWithRetry(Path.Combine(Loc.InstallDir, name));
            string self = Program.SelfPath;
            string uninstaller = Loc.UninstallerPath;
            if (File.Exists(uninstaller))
            {
                if (string.Equals(Path.GetFullPath(self), Path.GetFullPath(uninstaller), StringComparison.OrdinalIgnoreCase))
                {
                    // 実行中の自分自身は削除できないため、一時フォルダへ移す（次回のセットアップ実行時・Windows の一時ファイル整理で消える）
                    try { File.Move(uninstaller, Path.Combine(Path.GetTempPath(), "AIUsageBar-uninstall-" + Guid.NewGuid().ToString("N") + ".tmp")); }
                    catch (Exception ex) { log.Write("アンインストーラーを移動できなかった：" + ex.Message); }
                }
                else DeleteWithRetry(uninstaller);
            }
            for (int i = 0; i < 10 && Directory.Exists(Loc.InstallDir); i++)
            {
                try
                {
                    if (Directory.EnumerateFileSystemEntries(Loc.InstallDir).Any()) break;   // 利用者のファイルが残っていれば消さない
                    Directory.Delete(Loc.InstallDir);
                }
                catch (IOException) { Thread.Sleep(300); }
                catch (UnauthorizedAccessException) { Thread.Sleep(300); }
            }

            if (purge && Directory.Exists(Loc.DataDir))
            {
                Step(ui, "設定とデータを削除しています…");
                try { Directory.Delete(Loc.DataDir, true); log.Write("データを削除：" + Loc.DataDir); }
                catch (Exception ex) { log.Write("データを削除できなかった：" + ex.Message); }
            }
            log.Write("アンインストール完了");
            return Program.ExitOk;
        }

        // ------------------------------------------------------------------ 起動中のアプリ

        /// <summary>
        /// 起動中の AI Usage Bar に終了を依頼し、終わらなければ（画面では確認してから）強制終了する。
        /// 対象は同じユーザー・同じセッションで動いている AI Usage Bar だけ。
        /// </summary>
        bool StopRunningApp(IProgressUi ui)
        {
            var procs = FindAppProcesses();
            if (procs.Count == 0) return true;
            log.Write("起動中のアプリ：" + procs.Count + "件。終了を依頼");
            try
            {
                string sid = WindowsIdentity.GetCurrent().User.Value;
                EventWaitHandle h;
                if (EventWaitHandle.TryOpenExisting(@"Local\AIUsageBar-Quit-" + sid + "-" + Program.InstanceGuid, System.Security.AccessControl.EventWaitHandleRights.Modify, out h))
                    using (h) h.Set();
            }
            catch (Exception ex) { log.Write("終了の依頼に失敗：" + ex.Message); }
            WaitExit(procs, 10000);
            procs = FindAppProcesses();
            if (procs.Count == 0) return true;
            if (ui != null && !ui.Confirm("AI Usage Bar が終了しませんでした（以前の版の可能性があります）。強制終了して続けますか？")) return false;
            log.Write("強制終了：" + procs.Count + "件");
            foreach (var p in procs) { try { p.Kill(); } catch { } }
            WaitExit(procs, 5000);
            return FindAppProcesses().Count == 0;
        }

        static void WaitExit(List<Process> procs, int ms)
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            foreach (var p in procs)
            {
                int left = (int)Math.Max(0, (until - DateTime.UtcNow).TotalMilliseconds);
                try { p.WaitForExit(left); } catch { }
            }
        }

        static List<Process> FindAppProcesses()
        {
            var list = new List<Process>();
            int session;
            using (var me = Process.GetCurrentProcess()) session = me.SessionId;
            foreach (var p in Process.GetProcessesByName("AIUsageBar"))
            {
                try
                {
                    if (p.SessionId != session) continue;
                    var info = FileVersionInfo.GetVersionInfo(p.MainModule.FileName);
                    if (info.ProductName == Program.Name) { list.Add(p); continue; }
                }
                catch { }
                p.Dispose();
            }
            return list;
        }

        // ------------------------------------------------------------------ 補助

        static void Step(IProgressUi ui, string text) { if (ui != null) ui.Status(text); }

        static byte[] ReadResource(string name)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (s == null) throw new InvalidOperationException("セットアップ内のファイルが見つかりません：" + name);
                using (var ms = new MemoryStream()) { s.CopyTo(ms); return ms.ToArray(); }
            }
        }

        public static string Sha256(byte[] data)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "");
        }

        /// <summary>一時ファイルに書いてから置き換える（書き込み途中のファイルを残さない）</summary>
        static void WriteFileSafely(string path, byte[] data)
        {
            string tmp = path + ".new";
            File.WriteAllBytes(tmp, data);
            for (int i = 0; ; i++)
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(tmp, path);
                    return;
                }
                catch (IOException) { if (i >= 20) throw; Thread.Sleep(300); }
                catch (UnauthorizedAccessException) { if (i >= 20) throw; Thread.Sleep(300); }
            }
        }

        static void DeleteWithRetry(string path)
        {
            for (int i = 0; i < 20 && File.Exists(path); i++)
            {
                try { File.Delete(path); return; }
                catch (IOException) { Thread.Sleep(300); }
                catch (UnauthorizedAccessException) { Thread.Sleep(300); }
            }
        }

        /// <summary>このアプリを指すショートカットだけを削除する（同じ名前の別のショートカットは残す）</summary>
        void DeleteOurLink(string link)
        {
            if (!File.Exists(link)) return;
            string target = ShellLink.Target(link);
            if (target == null || IsUnder(target, Loc.InstallDir)) DeleteWithRetry(link);
            else log.Write("別の場所を指すショートカットのため残す：" + link);
        }

        static bool IsUnder(string path, string dir)
        {
            try
            {
                string p = Path.GetFullPath(path), d = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
                return p.StartsWith(d, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public static void CleanupOldUninstallerCopies()
        {
            try
            {
                foreach (var f in Directory.GetFiles(Path.GetTempPath(), "AIUsageBar-uninstall-*.tmp"))
                    try { if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(f)).TotalMinutes > 1) File.Delete(f); } catch { }
            }
            catch { }
        }
    }

    public interface IProgressUi
    {
        void Status(string text);
        bool Confirm(string question);
    }

    // ------------------------------------------------------------------ ショートカット（IShellLink）

    public static class ShellLink
    {
        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        class CShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        public static void Create(string linkPath, string target, string workDir, string description)
        {
            var link = (IShellLinkW)new CShellLink();
            try
            {
                link.SetPath(target);
                link.SetWorkingDirectory(workDir);
                link.SetDescription(description);
                link.SetIconLocation(target, 0);
                string tmp = linkPath + ".new.lnk";
                ((IPersistFile)link).Save(tmp, true);
                if (File.Exists(linkPath)) File.Delete(linkPath);
                File.Move(tmp, linkPath);
            }
            finally { Marshal.FinalReleaseComObject(link); }
        }

        public static string Target(string linkPath)
        {
            try
            {
                var link = (IShellLinkW)new CShellLink();
                try
                {
                    ((IPersistFile)link).Load(linkPath, 0);
                    var sb = new StringBuilder(1024);
                    link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0x4 /* SLGP_RAWPATH */);
                    return sb.Length == 0 ? null : Environment.ExpandEnvironmentVariables(sb.ToString());
                }
                finally { Marshal.FinalReleaseComObject(link); }
            }
            catch { return null; }
        }
    }

    // ------------------------------------------------------------------ 画面

    public class SetupForm : Form, IProgressUi
    {
        readonly Engine engine;
        readonly bool uninstall;
        public int ExitCode = Program.ExitCancelled;

        readonly Panel body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24, 16, 24, 8) };
        readonly FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 56, Padding = new Padding(16, 12, 16, 12) };
        readonly Button primary = new Button { AutoSize = true, MinimumSize = new Size(110, 30) };
        readonly Button cancel = new Button { Text = "キャンセル", AutoSize = true, MinimumSize = new Size(96, 30) };
        readonly Label status = new Label { AutoSize = true, ForeColor = SystemColors.GrayText };
        CheckBox chkDesktop, chkStartup, chkLaunch, chkRemoveData;
        bool finished, busy;

        static Font UiFont(float size, FontStyle style = FontStyle.Regular)
        {
            foreach (var name in new[] { "Yu Gothic UI", "Meiryo UI", "Segoe UI" })
            {
                try { var f = new Font(name, size, style); if (f.Name == name) return f; f.Dispose(); } catch { }
            }
            return new Font(SystemFonts.MessageBoxFont.FontFamily, size, style);
        }

        public SetupForm(Engine engine, bool uninstall)
        {
            this.engine = engine;
            this.uninstall = uninstall;
            Text = Program.Name + (uninstall ? " のアンインストール" : " セットアップ");
            Font = UiFont(9.5f);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 400);
            BackColor = SystemColors.Window;
            try { Icon = Icon.ExtractAssociatedIcon(Program.SelfPath); } catch { }

            var header = new Panel { Dock = DockStyle.Top, Height = 76, BackColor = Color.FromArgb(245, 246, 248) };
            var pic = new PictureBox { Location = new Point(22, 16), Size = new Size(44, 44), SizeMode = PictureBoxSizeMode.Zoom };
            try { using (var ic = Icon.ExtractAssociatedIcon(Program.SelfPath)) pic.Image = new Icon(ic, 48, 48).ToBitmap(); } catch { }
            var title = new Label { Text = Program.Name + " " + Program.Version, Font = UiFont(13f, FontStyle.Bold), AutoSize = true, Location = new Point(78, 14) };
            var sub = new Label { Text = uninstall ? "アンインストール" : "Codex・Claude の残り利用枠とトークンを、タスクバーでひと目で", AutoSize = true, ForeColor = SystemColors.GrayText, Location = new Point(80, 44) };
            header.Controls.Add(pic);
            header.Controls.Add(title);
            header.Controls.Add(sub);
            var line = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Color.FromArgb(225, 227, 232) };

            buttons.BackColor = Color.FromArgb(245, 246, 248);
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(primary);
            cancel.Click += (s, e) => Close();
            primary.Click += (s, e) => { if (finished) { Close(); return; } Run(); };
            AcceptButton = primary;
            CancelButton = cancel;

            Controls.Add(body);
            Controls.Add(line);
            Controls.Add(header);
            Controls.Add(buttons);
            if (uninstall) BuildUninstall(); else BuildInstall();
            FormClosing += (s, e) => { if (busy) e.Cancel = true; };
        }

        FlowLayoutPanel NewFlow()
        {
            body.Controls.Clear();
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            body.Controls.Add(flow);
            return flow;
        }

        Label Para(FlowLayoutPanel flow, string text, bool gray = false, int bottom = 10)
        {
            var l = new Label { Text = text, AutoSize = true, MaximumSize = new Size(500, 0), Margin = new Padding(0, 0, 0, bottom) };
            if (gray) l.ForeColor = SystemColors.GrayText;
            flow.Controls.Add(l);
            return l;
        }

        CheckBox Check(FlowLayoutPanel flow, string text, bool value, bool enabled = true)
        {
            var c = new CheckBox { Text = text, Checked = value, Enabled = enabled, AutoSize = true, MaximumSize = new Size(500, 0), Margin = new Padding(0, 2, 0, 4) };
            flow.Controls.Add(c);
            return c;
        }

        void BuildInstall()
        {
            var flow = NewFlow();
            var d = engine.DefaultChoices();
            string installed = engine.InstalledVersion;
            Para(flow, "Claude Code・Codex・Claude Desktop の残り利用枠とトークン使用量を確認するアプリをインストールします。");
            Para(flow, "インストール先：" + engine.Loc.InstallDir, true, 2);
            Para(flow, "このWindowsユーザーだけにインストールします（管理者権限は不要です）。アプリはPC内のファイルを読むだけで、インターネットに接続しません。", true, 12);
            if (installed != null) Para(flow, "インストール済みの版（" + installed + "）を上書きします。設定と集計データはそのまま残ります。", false, 12);
            chkDesktop = Check(flow, "デスクトップにショートカットを作る", d.Desktop);
            bool blocked = engine.StartupPolicyBlocked;
            chkStartup = Check(flow, blocked ? "サインイン時に自動で起動する（組織のポリシーで無効）" : "Windows へのサインイン時に自動で起動する", d.Startup && !blocked, !blocked);
            chkLaunch = Check(flow, "インストール後に起動する", d.Launch);
            status.Margin = new Padding(0, 12, 0, 0);
            status.Text = "";
            flow.Controls.Add(status);
            primary.Text = installed != null ? "上書きインストール" : "インストール";
        }

        void BuildUninstall()
        {
            var flow = NewFlow();
            Para(flow, Program.Name + " をこのPC（このWindowsユーザー）から削除します。起動中の場合は終了します。");
            Para(flow, "インストール先：" + engine.Loc.InstallDir, true, 12);
            if (engine.TeamJoined)
                Para(flow, "※チームに参加中です。共有フォルダにあるこのPCの報告は、アンインストールしても削除されません。先にアプリの「チーム」タブで「チームから抜ける」を選んでください。", false, 12).ForeColor = Color.FromArgb(176, 96, 0);
            chkRemoveData = Check(flow, "設定・集計データ・チームの参加情報も削除する", engine.Opt.RemoveData);
            Para(flow, "保存場所：" + engine.Loc.DataDir + "。チェックしない場合は残るので、再インストールすると続きから使えます。Claude Code・Codex・Claude Desktop 自体のデータは削除しません。", true, 0);
            status.Margin = new Padding(0, 12, 0, 0);
            status.Text = "";
            flow.Controls.Add(status);
            primary.Text = "アンインストール";
        }

        void Run()
        {
            busy = true;
            primary.Enabled = cancel.Enabled = false;
            foreach (var c in new[] { chkDesktop, chkStartup, chkLaunch, chkRemoveData }) if (c != null) c.Enabled = false;
            Cursor = Cursors.WaitCursor;
            var choices = uninstall ? null : new Engine.InstallChoices
            {
                Desktop = chkDesktop.Checked, Startup = chkStartup.Checked, Launch = chkLaunch.Checked,
                ExplicitDesktopChoice = true, ExplicitStartupChoice = chkStartup.Enabled,
            };
            bool purge = chkRemoveData != null && chkRemoveData.Checked;
            var worker = new Thread(() =>
            {
                int code;
                string error = null;
                try { code = uninstall ? engine.Uninstall(this, purge) : engine.Install(this, choices); }
                catch (Exception ex) { code = Program.ExitError; error = ex.Message; }
                try { BeginInvoke((Action)(() => Done(code, error))); } catch { }
            });
            worker.SetApartmentState(ApartmentState.STA);   // ショートカット作成（COM）のため
            worker.IsBackground = true;
            worker.Start();
        }

        void Done(int code, string error)
        {
            busy = false;
            Cursor = Cursors.Default;
            ExitCode = code;
            if (code != Program.ExitOk)
            {
                status.Text = error != null ? "完了できませんでした：" + error : "中止しました。";
                status.ForeColor = Color.FromArgb(180, 40, 40);
                cancel.Enabled = true;
                cancel.Text = "閉じる";
                return;
            }
            finished = true;
            var flow = NewFlow();
            if (uninstall)
            {
                Para(flow, "アンインストールが完了しました。");
                Para(flow, "タスクバーにピン留めしていた場合は、残ったボタンを右クリック →「タスクバーからピン留めを外す」で外してください。", true);
            }
            else
            {
                Para(flow, "インストールが完了しました。");
                Para(flow, "スタートメニューの「" + Program.Name + "」から起動できます。起動すると、画面は開かずにタスクバーにボタンが表示されます（上段 Codex・下段 Claude の残り％）。ボタンをクリックすると画面が開きます。", false);
                Para(flow, "いつでも使えるようにするには、タスクバーのボタンを右クリック →「タスクバーにピン留めする」。使い方は、インストール先の「はじめに.txt」をご覧ください。", true);
                Para(flow, "アンインストールは、Windows の「設定」→「アプリ」→「インストールされているアプリ」から行えます。", true);
            }
            primary.Text = "完了";
            primary.Enabled = true;
            cancel.Visible = false;
        }

        public void Status(string text)
        {
            try { BeginInvoke((Action)(() => status.Text = text)); } catch { }
        }

        public bool Confirm(string question)
        {
            bool result = false;
            try { Invoke((Action)(() => result = MessageBox.Show(this, question, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)); } catch { }
            return result;
        }

        /// <summary>検査用：画面の見本を画像にする（インストールはしない）</summary>
        public static void RenderSamples(Options opt)
        {
            Directory.CreateDirectory(opt.RenderDir);
            string root = Path.Combine(Path.GetTempPath(), "aiub-setup-render-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var loc = Locations.ForTest(root);
            // 見本にこのPCのユーザー名を写さない
            loc.InstallDir = @"C:\Users\user01\AppData\Local\Programs\AI Usage Bar";
            loc.DataDir = @"C:\Users\user01\AppData\Local\AIUsageBar";
            var engine = new Engine(loc, new Options(), new SetupLog(null));
            Action<Form, string> shot = (f, name) =>
            {
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(-5000, -5000);
                f.ShowInTaskbar = false;
                f.Show();
                Application.DoEvents();
                using (var bmp = new Bitmap(f.Width, f.Height)) { f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height)); bmp.Save(Path.Combine(opt.RenderDir, name)); }
            };
            using (var f = new SetupForm(engine, false)) { shot(f, "setup-install.png"); f.Done(Program.ExitOk, null); Application.DoEvents(); shot(f, "setup-done.png"); f.Hide(); }
            using (var f = new SetupForm(engine, true)) { shot(f, "setup-uninstall.png"); f.Hide(); }
        }
    }
}
