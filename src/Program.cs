using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("AI Usage Bar")]
[assembly: AssemblyProduct("AI Usage Bar")]
[assembly: AssemblyDescription("Codex・Claudeの利用枠とトークン、作業ログをタスクトレイで確認")]
[assembly: AssemblyVersion("0.1.0.0")]
[assembly: AssemblyFileVersion("0.1.0.0")]
[assembly: AssemblyInformationalVersion("0.1.0")]
// P/Invoke の DLL は System32 からだけ読み込む（exe と同じフォルダのDLLを使わない）
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace AIUsageBar
{
    public static class Program
    {
        const string InstanceGuid = "6f3c2a9e-6b1d-4f0e-9a51-2c8f0d6e7b41";

        [STAThread]
        public static void Main(string[] args)
        {
            Hardening.RestrictDllSearch();

            if (!Hardening.IsFrameworkSupported(Hardening.FrameworkRelease()))
            {
                MessageBox.Show("このPCの .NET Framework が古いため、AI Usage Bar を起動できません。\n\nWindows Update で .NET Framework 4.8（4.7.2 以上）をインストールしてから、もう一度起動してください。", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // 「推奨の場所へ移動」から起動されたときは、元のプロセスの終了を待つ
            for (int i = 0; i + 1 < args.Length; i++)
            {
                int pid;
                if (args[i] == "--wait-for-pid" && int.TryParse(args[i + 1], out pid))
                {
                    try { using (var old = Process.GetProcessById(pid)) old.WaitForExit(15000); } catch { }
                }
            }

            // 招待リンク（aiusagebar:join?c=...）で起動された場合は、参加画面を開くために受け渡す
            string inviteUrl = null;
            foreach (var a in args)
                if (a != null && a.StartsWith(TeamInvite.UrlScheme + ":", StringComparison.OrdinalIgnoreCase)) inviteUrl = a;
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == "--open-invite") inviteUrl = args[i + 1];
            try { RegisterUrlScheme(); } catch (Exception ex) { Log(ex); }
            if (inviteUrl != null) WritePendingInvite(inviteUrl);

            // 二重起動防止：同じユーザー・同じログオンセッションの中だけで有効な名前（ユーザーSID＋固定GUID）
            string sid = "unknown";
            try { sid = WindowsIdentity.GetCurrent().User.Value; } catch { }
            Mutex mutex = null;
            EventWaitHandle showSignal = null;
            EventWaitHandle quitSignal = null;
            bool created = true;
            try
            {
                var mutexSecurity = new MutexSecurity();
                mutexSecurity.AddAccessRule(new MutexAccessRule(WindowsIdentity.GetCurrent().User, MutexRights.FullControl, AccessControlType.Allow));
                mutex = new Mutex(true, @"Local\AIUsageBar-" + sid + "-" + InstanceGuid, out created, mutexSecurity);
                var eventSecurity = new EventWaitHandleSecurity();
                eventSecurity.AddAccessRule(new EventWaitHandleAccessRule(WindowsIdentity.GetCurrent().User, EventWaitHandleRights.FullControl, AccessControlType.Allow));
                bool ignored;
                showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AIUsageBar-Show-" + sid + "-" + InstanceGuid, out ignored, eventSecurity);
                // インストーラー・アンインストーラーからの終了依頼（同じユーザーだけが使える）
                quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AIUsageBar-Quit-" + sid + "-" + InstanceGuid, out ignored, eventSecurity);
            }
            catch (Exception ex)
            {
                // 名前を他のプログラムに使われていても、起動できないことがないようにする（二重起動防止なしで続行）
                Log(ex);
                created = true;
            }

            try
            {
                if (!created)
                {
                    if (showSignal != null) showSignal.Set();
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => Log(e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Log(e.ExceptionObject as Exception);
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                try
                {
                    Application.Run(new TrayApp(showSignal, quitSignal));
                }
                catch (Exception ex)
                {
                    Log(ex);
                    MessageBox.Show("AI Usage Bar を起動できませんでした。\n詳細は %LOCALAPPDATA%\\AIUsageBar\\error.log をご確認ください。", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            finally
            {
                if (showSignal != null) showSignal.Dispose();
                if (quitSignal != null) quitSignal.Dispose();
                if (mutex != null) { GC.KeepAlive(mutex); mutex.Dispose(); }
            }
        }

        static void Log(Exception ex)
        {
            if (ex == null) return;
            Hardening.AppendErrorLog(AppPaths.Default().DataDir, ex.ToString());
        }

        internal static string PendingInvitePath { get { return System.IO.Path.Combine(AppPaths.Default().DataDir, "pending-invite.txt"); } }

        static void WritePendingInvite(string url)
        {
            try
            {
                string dir = AppPaths.Default().DataDir;
                Hardening.SecureDirectory(dir);
                // リンクのみ（aiusagebar:...）を受け入れる。長すぎるものは無視
                if (url.Length > 8000) return;
                FileUtil.WriteAtomic(PendingInvitePath, url);
            }
            catch (Exception ex) { Log(ex); }
        }

        /// <summary>招待リンク（aiusagebar:）をこのユーザーだけに登録する。exe が安全な場所にある場合のみ。</summary>
        static void RegisterUrlScheme()
        {
            string exe = Hardening.CurrentExePath;
            if (exe == null || Hardening.IsRiskyLocation(exe)) return;   // Downloads 等からは登録しない
            using (var cls = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + TeamInvite.UrlScheme))
            {
                if (cls == null) return;
                cls.SetValue("", "URL:AI Usage Bar Invite");
                cls.SetValue("URL Protocol", "");
                using (var icon = cls.CreateSubKey("DefaultIcon")) if (icon != null) icon.SetValue("", "\"" + exe + "\",0");
                using (var cmd = cls.CreateSubKey(@"shell\open\command")) if (cmd != null) cmd.SetValue("", "\"" + exe + "\" --open-invite \"%1\"");
            }
        }
    }
}
