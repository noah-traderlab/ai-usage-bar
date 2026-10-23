using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace AIUsageBar
{
    public static class AppInfo
    {
        public const string Name = "AI Usage Bar";
        public const string Version = "0.1.0";
        public const string ClientName = "ai_usage_bar";
    }

    /// <summary>保存先・読み取り元のパス。テストでは差し替え可能。</summary>
    public class AppPaths
    {
        public string Home;
        public string LocalAppData;
        public string RoamingAppData;
        public string DataDir;      // 集計キャッシュ（%LOCALAPPDATA%\AIUsageBar）
        public string ClaudeProjects; // ~/.claude/projects
        public string CodexHome;    // ~/.codex
        public List<string> ClaudeHistoryCandidates = new List<string>();

        public static AppPaths Default()
        {
            var p = new AppPaths();
            p.Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            p.LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            p.RoamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            p.DataDir = Path.Combine(p.LocalAppData, "AIUsageBar");
            p.ClaudeProjects = Path.Combine(p.Home, ".claude", "projects");
            string codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            p.CodexHome = string.IsNullOrEmpty(codexHome) ? Path.Combine(p.Home, ".codex") : codexHome;

            // Claude Desktop：通常インストールとMicrosoft Store（MSIX）版で保存先が違う
            p.ClaudeHistoryCandidates.Add(Path.Combine(p.RoamingAppData, "Claude", "plan-usage-history.json"));
            try
            {
                string packages = Path.Combine(p.LocalAppData, "Packages");
                if (Directory.Exists(packages))
                {
                    foreach (var dir in Directory.GetDirectories(packages, "Claude_*"))
                        p.ClaudeHistoryCandidates.Add(Path.Combine(dir, "LocalCache", "Roaming", "Claude", "plan-usage-history.json"));
                }
            }
            catch { }
            return p;
        }
    }

    public static class Fmt
    {
        static readonly CultureInfo Ja = new CultureInfo("ja-JP");

        public static string Tokens(long n)
        {
            double a = Math.Abs((double)n);
            if (a >= 1e9) return (n / 1e9).ToString(a >= 1e10 ? "0.0" : "0.00", CultureInfo.InvariantCulture) + "B";
            // Mac版と同じ桁：B は小数2桁、M・K は小数1桁
            if (a >= 1e6) return (n / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + "M";
            if (a >= 1e3) return (n / 1e3).ToString("0.0", CultureInfo.InvariantCulture) + "K";
            return n.ToString(CultureInfo.InvariantCulture);
        }

        public static string Full(long n)
        {
            return n.ToString("#,0", CultureInfo.InvariantCulture);
        }

        public static string DateTimeShort(DateTime local)
        {
            return local.ToString("M/d(ddd) HH:mm", Ja);
        }

        public static string DayLabel(DateTime day, DateTime today)
        {
            if (day.Date == today.Date) return "今日";
            if (day.Date == today.Date.AddDays(-1)) return "昨日";
            return day.ToString("M/d(ddd)", Ja);
        }

        public static string DayShort(DateTime day)
        {
            return day.ToString("M/d", Ja);
        }

        public static string Weekday(DateTime day)
        {
            return day.ToString("ddd", Ja);
        }

        public static string Ago(DateTime utc, DateTime nowUtc)
        {
            var span = nowUtc - utc;
            if (span.TotalSeconds < 60) return "たった今";
            if (span.TotalMinutes < 60) return ((int)span.TotalMinutes) + "分前";
            if (span.TotalHours < 24) return ((int)span.TotalHours) + "時間前";
            return ((int)span.TotalDays) + "日前";
        }

        public static string Until(DateTime utc, DateTime nowUtc)
        {
            var span = utc - nowUtc;
            if (span.TotalSeconds <= 0) return "まもなく";
            if (span.TotalMinutes < 60) return "あと" + Math.Max(1, (int)Math.Ceiling(span.TotalMinutes)) + "分";
            if (span.TotalHours < 24) return "あと" + (int)span.TotalHours + "時間" + (span.Minutes > 0 ? span.Minutes + "分" : "");
            return "あと" + (int)span.TotalDays + "日" + (span.Hours > 0 ? span.Hours + "時間" : "");
        }

        public static string WindowLabel(long? mins)
        {
            if (mins == null) return "利用枠";
            long m = mins.Value;
            if (m == 300) return "5時間";
            if (m == 10080) return "週間";
            if (m == 1440) return "1日";
            if (m % 1440 == 0) return (m / 1440) + "日間";
            if (m % 60 == 0) return (m / 60) + "時間";
            return m + "分";
        }

        public static string OneLine(string s, int max)
        {
            if (s == null) return "";
            var sb = new StringBuilder();
            bool space = false;
            foreach (char c in s)
            {
                if (char.IsWhiteSpace(c)) { space = true; continue; }
                if (space && sb.Length > 0) sb.Append(' ');
                space = false;
                sb.Append(c);
                if (sb.Length >= max) break;
            }
            string r = sb.ToString();
            if (r.Length >= max) r = r.Substring(0, max - 1) + "…";
            return r;
        }

        public static DateTime? ParseIsoUtc(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            DateTime dt;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out dt))
                return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            return null;
        }

        const long MaxUnixMs = 253402300799999; // 9999-12-31

        public static bool ValidUnixMs(long ms) { return ms >= 0 && ms <= MaxUnixMs; }

        /// <summary>範囲外の値は FormatException（不正なデータとして扱う）</summary>
        public static DateTime FromUnixSeconds(long s)
        {
            if (s < 0 || s > MaxUnixMs / 1000) throw new FormatException("時刻が範囲外です");
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(s);
        }

        public static DateTime FromUnixMs(long ms)
        {
            if (!ValidUnixMs(ms)) throw new FormatException("時刻が範囲外です");
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms);
        }

        public static long ToUnixMs(DateTime utc)
        {
            return (long)(utc.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }
    }

    public static class FileUtil
    {
        static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        /// <summary>一時ファイルに書いてから置き換える（書き込み途中で壊れないように）</summary>
        public static void WriteAtomic(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, content, Utf8NoBom);
            if (File.Exists(path)) File.Replace(tmp, path, null, true);
            else File.Move(tmp, path);
        }

        /// <summary>他のアプリが書き込み中でも読めるように共有モードで読む</summary>
        public static string ReadShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, Encoding.UTF8, true))
                return sr.ReadToEnd();
        }
    }

    public class Settings
    {
        public int CodexIntervalMinutes = 5;
        public int ClaudeIntervalMinutes = 1;   // Claudeの利用枠履歴・Claude Code/Codexの記録を確認する間隔
        public bool NotificationsEnabled = false;
        public int NotificationThreshold = 20;
        public int ResetNotify = ResetWatcher.ModeOff;   // 枠がリセットされたときの通知（0：しない／1：残りが少なかったときだけ／2：毎回）
        public bool ActivityEnabled = true;
        public bool ShowPrompts = true;
        public bool ShowInTaskbar = true;   // ウィンドウとしてタスクバーに並べる
        public bool StartMinimized = true;  // 起動時はタスクバーに収納

        public static readonly int[] IntervalChoices = { 1, 5, 10, 15, 30, 60 };
        public static readonly int[] ClaudeIntervalChoices = { 1, 5, 10, 15, 30, 60 };
        public static readonly int[] ThresholdChoices = { 10, 20, 30, 50 };

        public static Settings Load(string dataDir)
        {
            var s = new Settings();
            try
            {
                string path = Path.Combine(dataDir, "settings.json");
                if (!File.Exists(path)) return s;
                var o = Json.Parse(FileUtil.ReadShared(path));
                long? l = Json.Long(o, "codexIntervalMinutes");
                if (l != null && IntervalChoices.Contains((int)l.Value)) s.CodexIntervalMinutes = (int)l.Value;
                l = Json.Long(o, "claudeIntervalMinutes");
                if (l != null && ClaudeIntervalChoices.Contains((int)l.Value)) s.ClaudeIntervalMinutes = (int)l.Value;
                bool? b = Json.Bool(o, "notificationsEnabled");
                if (b != null) s.NotificationsEnabled = b.Value;
                l = Json.Long(o, "notificationThreshold");
                if (l != null && ThresholdChoices.Contains((int)l.Value)) s.NotificationThreshold = (int)l.Value;
                l = Json.Long(o, "resetNotify");
                if (l != null && l.Value >= ResetWatcher.ModeOff && l.Value <= ResetWatcher.ModeAlways) s.ResetNotify = (int)l.Value;
                b = Json.Bool(o, "activityEnabled");
                if (b != null) s.ActivityEnabled = b.Value;
                b = Json.Bool(o, "showPrompts");
                if (b != null) s.ShowPrompts = b.Value;
                b = Json.Bool(o, "showInTaskbar");
                if (b != null) s.ShowInTaskbar = b.Value;
                b = Json.Bool(o, "startMinimized");
                if (b != null) s.StartMinimized = b.Value;
            }
            catch { }
            return s;
        }

        public void Save(string dataDir)
        {
            var d = new Dictionary<string, object>();
            d["codexIntervalMinutes"] = CodexIntervalMinutes;
            d["claudeIntervalMinutes"] = ClaudeIntervalMinutes;
            d["notificationsEnabled"] = NotificationsEnabled;
            d["notificationThreshold"] = NotificationThreshold;
            d["resetNotify"] = ResetNotify;
            d["activityEnabled"] = ActivityEnabled;
            d["showPrompts"] = ShowPrompts;
            d["showInTaskbar"] = ShowInTaskbar;
            d["startMinimized"] = StartMinimized;
            try { FileUtil.WriteAtomic(Path.Combine(dataDir, "settings.json"), Json.Serialize(d, true)); } catch { }
        }
    }

    /// <summary>ログイン時起動（HKCU\...\Run）。既定はOFF。</summary>
    public static class StartupRegistration
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "AIUsageBar";

        /// <summary>このexeのパスで登録されているか（別の場所のexeが登録されていても「有効」とは扱わない）</summary>
        public static bool IsEnabled()
        {
            string v = RegisteredValue();
            return v != null && string.Equals(v.Trim().Trim('"'), Hardening.CurrentExePath, StringComparison.OrdinalIgnoreCase);
        }

        public static string RegisteredValue()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                    return k == null ? null : k.GetValue(ValueName) as string;
            }
            catch { return null; }
        }

        public static void Remove()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                    if (k != null && k.GetValue(ValueName) != null) k.DeleteValue(ValueName);
            }
            catch { }
        }

        public static void Set(bool enabled, string exePath)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled) k.SetValue(ValueName, "\"" + exePath + "\"");
                else if (k.GetValue(ValueName) != null) k.DeleteValue(ValueName);
            }
        }
    }
}
