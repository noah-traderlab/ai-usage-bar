using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace AIUsageBar
{
    /// <summary>
    /// セキュリティ対策をまとめたもの。
    /// ・DLL読み込み先の制限、外部プログラムの絶対パス起動
    /// ・保存先フォルダのアクセス権
    /// ・子プロセスをジョブに入れて、アプリ終了時に必ず終了させる
    /// ・依頼文の重複判定用HMAC（端末ごとの鍵をDPAPIで保護）
    /// ・書き出しの無害化（CSVの数式、Markdown/HTML）
    /// </summary>
    public static class Hardening
    {
        // ------------------------------------------------------------------ DLL・外部プログラム

        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetDefaultDllDirectories(uint flags);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool SetDllDirectory(string path);
        const uint LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x00000800;

        /// <summary>起動直後に呼ぶ。exe と同じフォルダ（Downloads など）に置かれたDLLを読み込まない</summary>
        public static void RestrictDllSearch()
        {
            try { SetDllDirectory(""); } catch { }                                   // カレントフォルダを検索対象から外す
            try { SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32); } catch { } // 以降の読み込みは System32 のみ
        }

        public static string WindowsDir { get { return Environment.GetFolderPath(Environment.SpecialFolder.Windows); } }
        public static string System32 { get { return Environment.SystemDirectory; } }
        public static string ExplorerPath { get { return Path.Combine(WindowsDir, "explorer.exe"); } }
        public static string TaskkillPath { get { return Path.Combine(System32, "taskkill.exe"); } }
        public static string CmdPath { get { return Path.Combine(System32, "cmd.exe"); } }

        /// <summary>フォルダを開く（絶対パスの explorer.exe、シェルを通さない）</summary>
        public static void OpenFolder(string dir, string select)
        {
            try
            {
                string target = select ?? dir;
                if (string.IsNullOrEmpty(target) || IsUncPath(target)) return;
                if (select != null ? !File.Exists(select) : !Directory.Exists(dir)) return;
                var psi = new ProcessStartInfo(ExplorerPath, select != null ? "/select,\"" + select + "\"" : "\"" + dir + "\"");
                psi.UseShellExecute = false;
                psi.WorkingDirectory = WindowsDir;
                using (Process.Start(psi)) { }
            }
            catch { }
        }

        public static bool IsUncPath(string path)
        {
            return !string.IsNullOrEmpty(path) && (path.StartsWith(@"\\") || path.StartsWith("//"));
        }

        /// <summary>見本画像の生成時だけ差し替える実行パス（本番では null）。実PCのユーザー名を画像に写さないため。</summary>
        public static string CurrentExePathOverride;

        /// <summary>このプロセスのexeの実際のパス</summary>
        public static string CurrentExePath
        {
            get
            {
                if (CurrentExePathOverride != null) return CurrentExePathOverride;
                try { using (var p = Process.GetCurrentProcess()) return p.MainModule.FileName; }
                catch { return System.Windows.Forms.Application.ExecutablePath; }
            }
        }

        /// <summary>ダウンロード・一時フォルダなど、他のファイルが置かれやすい場所から実行しているか</summary>
        public static bool IsRiskyLocation(string exePath)
        {
            string dir = SafeFull(Path.GetDirectoryName(exePath));
            if (dir == null) return true;
            var risky = new List<string>();
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            risky.Add(Path.Combine(home, "Downloads"));
            risky.Add(Path.GetTempPath());
            risky.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            risky.Add(Environment.GetFolderPath(Environment.SpecialFolder.InternetCache));
            return risky.Any(r => IsUnder(dir, r));
        }

        public static string RecommendedInstallDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "AI Usage Bar"); }
        }

        public static bool IsUnder(string path, string root)
        {
            string p = SafeFull(path), r = SafeFull(root);
            if (p == null || r == null) return false;
            r = r.TrimEnd('\\') + "\\";
            return (p.TrimEnd('\\') + "\\").StartsWith(r, StringComparison.OrdinalIgnoreCase);
        }

        static string SafeFull(string path)
        {
            try { return string.IsNullOrEmpty(path) ? null : Path.GetFullPath(path); } catch { return null; }
        }

        // ------------------------------------------------------------------ .NET Framework の版

        public const int MinimumFrameworkRelease = 461808; // .NET Framework 4.7.2（PBKDF2-SHA256 に必要）

        public static int? FrameworkRelease()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                {
                    var v = k == null ? null : k.GetValue("Release");
                    return v is int ? (int?)(int)v : null;
                }
            }
            catch { return null; }
        }

        /// <summary>版が分からない場合は起動を妨げない</summary>
        public static bool IsFrameworkSupported(int? release)
        {
            return release == null || release.Value >= MinimumFrameworkRelease;
        }

        // ------------------------------------------------------------------ 保存先のアクセス権

        /// <summary>保存先を作り、本人・SYSTEM・Administrators だけが使えるようにする（継承を切る）</summary>
        static readonly HashSet<string> secured = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static void SecureDirectory(string dir)
        {
            Directory.CreateDirectory(dir);
            lock (secured) { if (!secured.Add(dir)) return; }
            try
            {
                var user = WindowsIdentity.GetCurrent().User;
                var acl = new DirectorySecurity();
                acl.SetAccessRuleProtection(true, false);
                var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
                acl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                Directory.SetAccessControl(dir, acl);
            }
            catch { }
        }

        // ------------------------------------------------------------------ 子プロセスのジョブ

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attrs, string name);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct IO_COUNTERS { public ulong a, b, c, d, e, f; }

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        static IntPtr job = IntPtr.Zero;
        static readonly object jobGate = new object();

        /// <summary>子プロセスをジョブに入れる。アプリが強制終了されてもOSが子プロセスを終了する</summary>
        public static void AttachToKillOnCloseJob(Process p)
        {
            try
            {
                lock (jobGate)
                {
                    if (job == IntPtr.Zero)
                    {
                        job = CreateJobObject(IntPtr.Zero, null);
                        if (job == IntPtr.Zero) return;
                        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                        info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                        SetInformationJobObject(job, 9, ref info, Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)));
                    }
                }
                AssignProcessToJobObject(job, p.Handle);
            }
            catch { }
        }

        // ------------------------------------------------------------------ HMAC（依頼文の重複判定用）

        static byte[] hmacKey;

        /// <summary>端末ごとの乱数鍵（DPAPIで本人のみ復号可能）でHMAC-SHA256を取り、先頭12バイトを返す</summary>
        public static string PromptHmac(string dataDir, string text)
        {
            if (text == null) return null;
            var key = HmacKey(dataDir);
            using (var h = new HMACSHA256(key))
            {
                var mac = h.ComputeHash(Encoding.UTF8.GetBytes(text.Trim()));
                return BitConverter.ToString(mac, 0, 12).Replace("-", "").ToLowerInvariant();
            }
        }

        static byte[] HmacKey(string dataDir)
        {
            if (hmacKey != null) return hmacKey;
            string path = Path.Combine(dataDir ?? Path.GetTempPath(), "hash-key.bin");
            try
            {
                if (File.Exists(path))
                {
                    hmacKey = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
                    if (hmacKey.Length == 32) return hmacKey;
                }
            }
            catch { }
            hmacKey = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(hmacKey);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, ProtectedData.Protect(hmacKey, Entropy, DataProtectionScope.CurrentUser));
            }
            catch { }
            return hmacKey;
        }

        static readonly byte[] Entropy = Encoding.ASCII.GetBytes("AIUsageBar.prompt-hmac.v1");

        /// <summary>テスト用：鍵の読み込み状態を消す</summary>
        public static void ResetKeyForTests() { hmacKey = null; }

        // ------------------------------------------------------------------ 書き出しの無害化

        /// <summary>CSVのセル：必ず引用符で囲み、数式として解釈される先頭文字を無害化する</summary>
        public static string CsvCell(string s)
        {
            s = s ?? "";
            string t = s.TrimStart(' ', '\u3000');
            if (s.Length > 0 && ("\t\r\n".IndexOf(s[0]) >= 0 || (t.Length > 0 && "=+-@＝＋－＠".IndexOf(t[0]) >= 0))) s = "'" + s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        /// <summary>他のPCから届いた1行の文字列を表示用に整える（制御文字・方向制御・ゼロ幅文字を除き、長さを制限）</summary>
        public static string SafeLine(string s, int max)
        {
            if (s == null) return null;
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                if (char.IsControl(c)) { sb.Append(' '); continue; }
                if (IsInvisibleFormatChar(c)) continue;
                sb.Append(c);
                if (sb.Length >= max) { sb.Append('…'); break; }
            }
            return sb.ToString().Trim();
        }

        /// <summary>複数行を許す本文用（改行とタブは残し、方向制御・ゼロ幅文字だけ除く）</summary>
        public static string SafeText(string s, int max)
        {
            if (s == null) return null;
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                if (char.IsControl(c) && c != '\n' && c != '\r' && c != '\t') continue;
                if (IsInvisibleFormatChar(c)) continue;
                sb.Append(c);
                if (sb.Length >= max) { sb.Append('…'); break; }
            }
            return sb.ToString();
        }

        static bool IsInvisibleFormatChar(char c)
        {
            return (c >= '\u200B' && c <= '\u200F') || (c >= '\u202A' && c <= '\u202E') || (c >= '\u2066' && c <= '\u2069') || c == '\uFEFF' || c == '\u061C';
        }

        /// <summary>Markdownの1行項目：改行を空白にし、HTML・リンク・強調などの記号を無効化する</summary>
        public static string MdInline(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\r': case '\n': sb.Append(' '); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '&': sb.Append("&amp;"); break;
                    case '|': sb.Append("&#124;"); break;
                    case '\\': case '`': case '*': case '_': case '[': case ']': case '!': case '#': case '~':
                        sb.Append('\\').Append(c); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        /// <summary>Markdownの本文ブロック：中の ` の連続より長い囲みで囲む（中身は書式として解釈されない）</summary>
        public static string MdFence(string text, string indent)
        {
            text = (text ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
            int longest = 0, run = 0;
            foreach (char c in text) { if (c == '`') { run++; longest = Math.Max(longest, run); } else run = 0; }
            string fence = new string('`', Math.Max(3, longest + 1));
            var sb = new StringBuilder();
            sb.Append(indent).Append(fence).Append("text\n");
            foreach (var line in text.Split('\n')) sb.Append(indent).Append(line).Append('\n');
            sb.Append(indent).Append(fence).Append('\n');
            return sb.ToString();
        }

        // ------------------------------------------------------------------ エラーログ

        public static void AppendErrorLog(string dataDir, string text)
        {
            try
            {
                SecureDirectory(dataDir);
                string path = Path.Combine(dataDir, "error.log");
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.Delete(path); // 1MBで作り直す
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + text + Environment.NewLine);
            }
            catch { }
        }
    }
}
