using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AIUsageBar
{
    /// <summary>フレンドに見せる1つの利用枠（Codex の5時間・週間など）</summary>
    public class FriendWindow
    {
        public string Label;
        public double Remaining;
        public DateTime? ResetsAtUtc;
    }

    /// <summary>
    /// フレンド1人分の残り枠。共有フォルダの AIUsageBar-Friends\&lt;id&gt;.json に書く。
    /// 含めるのは表示名・残り枠（%）・リセット時刻・更新時刻だけ（メールアドレス・トークン数・作業内容は含めない）。
    /// </summary>
    public class FriendStatus
    {
        public const string Format = "aiub-friend-1";

        public string Id;
        public string Name;
        public DateTime UpdatedAtUtc;
        public double? ClaudeFiveHour;
        public double? ClaudeWeekly;
        public List<FriendWindow> Codex = new List<FriendWindow>();
        public bool IsMe;

        /// <summary>すべての枠のうち最も少ない残り（なければ null）</summary>
        public double? MinRemaining
        {
            get
            {
                var all = new List<double>();
                if (ClaudeFiveHour != null) all.Add(ClaudeFiveHour.Value);
                if (ClaudeWeekly != null) all.Add(ClaudeWeekly.Value);
                all.AddRange(Codex.Select(w => w.Remaining));
                return all.Count == 0 ? (double?)null : all.Min();
            }
        }

        public double? ClaudeMin
        {
            get
            {
                if (ClaudeFiveHour == null) return ClaudeWeekly;
                if (ClaudeWeekly == null) return ClaudeFiveHour;
                return Math.Min(ClaudeFiveHour.Value, ClaudeWeekly.Value);
            }
        }

        public double? CodexMin { get { return Codex.Count == 0 ? (double?)null : Codex.Min(w => w.Remaining); } }

        /// <summary>しばらく更新がない（相手のPCが閉じている・アプリが止まっている）</summary>
        public bool IsStale(DateTime nowUtc) { return nowUtc - UpdatedAtUtc > TimeSpan.FromMinutes(Friends.StaleMinutes); }

        /// <summary>まだリセットされていない Codex の枠のうち、最も早いリセット</summary>
        public DateTime? NextCodexReset(DateTime nowUtc)
        {
            var r = Codex.Where(w => w.ResetsAtUtc != null && w.ResetsAtUtc.Value > nowUtc).Select(w => w.ResetsAtUtc.Value).ToList();
            return r.Count == 0 ? (DateTime?)null : r.Min();
        }

        public Dictionary<string, object> ToJson()
        {
            return new Dictionary<string, object>
            {
                { "format", Format }, { "id", Id }, { "name", Name }, { "updatedAt", Fmt.ToUnixMs(UpdatedAtUtc) },
                { "claude", new Dictionary<string, object> { { "fiveHour", ClaudeFiveHour }, { "weekly", ClaudeWeekly } } },
                { "codex", Codex.Select(w => (object)new Dictionary<string, object> {
                    { "label", w.Label }, { "remaining", w.Remaining },
                    { "resetsAt", w.ResetsAtUtc == null ? (object)null : Fmt.ToUnixMs(w.ResetsAtUtc.Value) } }).ToList() },
            };
        }

        /// <summary>他人が書いたファイルなので、形式・範囲・長さをすべて確かめる。不正なら null</summary>
        public static FriendStatus FromJson(object o)
        {
            if (Json.Str(o, "format") != Format) return null;
            string id = Json.Str(o, "id");
            if (!Friends.ValidId(id)) return null;
            long? up = Json.Long(o, "updatedAt");
            if (up == null || !Fmt.ValidUnixMs(up.Value)) return null;
            var s = new FriendStatus
            {
                Id = id,
                Name = Hardening.SafeLine(Json.Str(o, "name"), Friends.MaxNameLength),
                UpdatedAtUtc = Fmt.FromUnixMs(up.Value),
            };
            if (string.IsNullOrWhiteSpace(s.Name)) s.Name = "名前なし";
            var cl = Json.Obj(o, "claude");
            if (cl != null) { s.ClaudeFiveHour = Pct(Json.Num(cl, "fiveHour")); s.ClaudeWeekly = Pct(Json.Num(cl, "weekly")); }
            var cx = Json.Arr(o, "codex");
            if (cx != null)
                foreach (var w in cx.Take(Friends.MaxWindows))
                {
                    var rem = Pct(Json.Num(w, "remaining"));
                    if (rem == null) continue;
                    long? r = Json.Long(w, "resetsAt");
                    s.Codex.Add(new FriendWindow
                    {
                        Label = Hardening.SafeLine(Json.Str(w, "label"), 20) ?? "",
                        Remaining = rem.Value,
                        ResetsAtUtc = r != null && Fmt.ValidUnixMs(r.Value) ? Fmt.FromUnixMs(r.Value) : (DateTime?)null,
                    });
                }
            return s;
        }

        static double? Pct(double? v)
        {
            if (v == null || double.IsNaN(v.Value) || double.IsInfinity(v.Value)) return null;
            return Math.Max(0, Math.Min(100, v.Value));
        }
    }

    /// <summary>このPCのフレンド設定（%LOCALAPPDATA%\AIUsageBar\friends.json）</summary>
    public class FriendsConfig
    {
        public string Id;
        public string Folder;          // 共有フォルダ（この中に AIUsageBar-Friends を作る）
        public string DisplayName;
        public bool Enabled;           // 自分の残り枠を公開し、フレンドの残り枠を読む

        public bool IsConfigured { get { return Enabled && !string.IsNullOrWhiteSpace(Folder); } }
        public string FriendsDir { get { return string.IsNullOrWhiteSpace(Folder) ? null : Path.Combine(Folder, Friends.FolderName); } }

        public static string PathIn(string dataDir) { return Path.Combine(dataDir, "friends.json"); }

        public static FriendsConfig Load(string dataDir)
        {
            var c = new FriendsConfig();
            try
            {
                string p = PathIn(dataDir);
                if (File.Exists(p))
                {
                    var o = Json.Parse(FileUtil.ReadShared(p));
                    c.Id = Json.Str(o, "id");
                    c.Folder = Hardening.SafeLine(Json.Str(o, "folder"), 1000);
                    c.DisplayName = Hardening.SafeLine(Json.Str(o, "displayName"), Friends.MaxNameLength);
                    c.Enabled = Json.Bool(o, "enabled") ?? false;
                }
            }
            catch { }
            if (!Friends.ValidId(c.Id)) c.Id = Guid.NewGuid().ToString("N");
            return c;
        }

        public void Save(string dataDir)
        {
            var d = new Dictionary<string, object> { { "id", Id }, { "folder", Folder }, { "displayName", DisplayName }, { "enabled", Enabled } };
            Hardening.SecureDirectory(dataDir);
            FileUtil.WriteAtomic(PathIn(dataDir), Json.Serialize(d, true));
        }
    }

    /// <summary>フレンド招待コード：共有フォルダの場所だけを入れた文字列（秘密情報は含まない）</summary>
    public static class FriendInvite
    {
        public const string Prefix = "AIUB-FRIEND-1:";

        public static string Encode(string folder)
        {
            var d = new Dictionary<string, object> { { "folder", folder } };
            return Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(Json.Serialize(d))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        /// <summary>貼り付けた文章の中から招待コードを探して、共有フォルダの場所を返す</summary>
        public static string TryDecode(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > 20000) return null;
            var m = System.Text.RegularExpressions.Regex.Match(text, System.Text.RegularExpressions.Regex.Escape(Prefix) + "([A-Za-z0-9_-]{8,4000})");
            if (!m.Success) return null;
            try
            {
                string b64 = m.Groups[1].Value.Replace('-', '+').Replace('_', '/');
                b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
                string folder = Hardening.SafeLine(Json.Str(Json.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64))), "folder"), 1000);
                return string.IsNullOrWhiteSpace(folder) ? null : folder;
            }
            catch (FormatException) { return null; }
            catch (ArgumentException) { return null; }
        }
    }

    /// <summary>
    /// フレンド機能：共有フォルダ（OneDrive・Googleドライブ・社内サーバーなど）を通じて、残り枠だけを見せ合う。
    /// アプリ自身は通信しない（チーム機能と同じ方式）。フォルダを共有した相手だけが読める。
    ///   AIUsageBar-Friends\&lt;id&gt;.json … 各PCの残り枠（表示名・残り%・リセット時刻・更新時刻）
    /// </summary>
    public static class Friends
    {
        public const string FolderName = "AIUsageBar-Friends";
        public const int StaleMinutes = 30;      // これより更新がなければ灰色
        public const int HideAfterDays = 7;      // これより古いファイルは表示しない
        public const int MaxFriends = 100;
        public const int MaxFileBytes = 16 * 1024;
        public const int MaxWindows = 6;
        public const int MaxNameLength = 40;

        public static bool ValidId(string id)
        {
            return id != null && id.Length == 32 && id.All(Uri.IsHexDigit);
        }

        /// <summary>このPCの今の残り枠。古い情報・リセット済みの枠は載せない（相手に誤った数字を見せないため）</summary>
        public static FriendStatus BuildMine(UsageStore store, FriendsConfig cfg, DateTime nowUtc)
        {
            var s = new FriendStatus
            {
                Id = cfg.Id,
                Name = string.IsNullOrWhiteSpace(cfg.DisplayName) ? "名前なし" : Hardening.SafeLine(cfg.DisplayName, MaxNameLength),
                UpdatedAtUtc = nowUtc,
                IsMe = true,
            };
            var cl = store.Claude;
            if (cl != null && !cl.IsStale(nowUtc, store.Settings.ClaudeIntervalMinutes))
            {
                s.ClaudeFiveHour = cl.FiveHourRemaining;
                s.ClaudeWeekly = cl.WeeklyRemaining;
            }
            var cx = store.Codex;
            if (cx != null && cx.Main != null && !store.CodexStale(nowUtc))
                foreach (var w in cx.Main.Windows.Where(w => !UsageStore.WindowExpired(w, nowUtc)).Take(MaxWindows))
                    s.Codex.Add(new FriendWindow { Label = w.Label, Remaining = w.RemainingPercent, ResetsAtUtc = w.ResetsAtUtc });
            return s;
        }

        public static string FileFor(FriendsConfig cfg) { return Path.Combine(cfg.FriendsDir, cfg.Id + ".json"); }

        public static void Publish(FriendsConfig cfg, FriendStatus mine)
        {
            if (!Directory.Exists(cfg.Folder)) throw new IOException("共有フォルダが見つかりません：" + cfg.Folder);
            Directory.CreateDirectory(cfg.FriendsDir);
            FileUtil.WriteAtomic(FileFor(cfg), Json.Serialize(mine.ToJson(), true));
        }

        /// <summary>公開をやめる：共有フォルダから自分のファイルを消す</summary>
        public static void Withdraw(FriendsConfig cfg)
        {
            if (cfg.FriendsDir == null || !Friends.ValidId(cfg.Id)) return;
            string p = FileFor(cfg);
            if (File.Exists(p)) File.Delete(p);
        }

        /// <summary>共有フォルダのフレンドを読む（自分は除く）。壊れた・大きすぎる・古すぎるファイルは無視する</summary>
        public static List<FriendStatus> ReadOthers(FriendsConfig cfg, DateTime nowUtc)
        {
            var list = new List<FriendStatus>();
            string dir = cfg.FriendsDir;
            if (dir == null || !Directory.Exists(dir)) return list;
            var seen = new HashSet<string>();
            foreach (var file in Directory.EnumerateFiles(dir, "*.json").Take(MaxFriends * 3))
            {
                try
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (!ValidId(name) || name == cfg.Id) continue;
                    var fi = new FileInfo(file);
                    if (fi.Length > MaxFileBytes || (fi.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    var s = FriendStatus.FromJson(Json.Parse(FileUtil.ReadShared(file)));
                    // ファイル名とidが違うもの（他人のファイルの上書き・複製）は採用しない
                    if (s == null || s.Id != name || !seen.Add(s.Id)) continue;
                    if (nowUtc - s.UpdatedAtUtc > TimeSpan.FromDays(HideAfterDays)) continue;
                    if (s.UpdatedAtUtc > nowUtc.AddMinutes(10)) continue;   // 未来の時刻は信用しない
                    list.Add(s);
                    if (list.Count >= MaxFriends) break;
                }
                catch { }
            }
            // 新しく更新された人を上に、同じなら名前順
            return list.OrderBy(f => f.IsStale(nowUtc)).ThenBy(f => f.Name, StringComparer.CurrentCulture).ToList();
        }
    }

    /// <summary>定期的に自分の残り枠を書き、フレンドの残り枠を読む（UIスレッド以外から呼ぶ）</summary>
    public class FriendsService
    {
        readonly UsageStore store;
        readonly object gate = new object();
        public FriendsConfig Config;
        List<FriendStatus> others = new List<FriendStatus>();
        FriendStatus mine;
        string lastPayload;
        DateTime lastWriteUtc = DateTime.MinValue;

        public string LastError;
        public DateTime? LastReadUtc;

        public FriendsService(UsageStore store)
        {
            this.store = store;
            Config = FriendsConfig.Load(store.Paths.DataDir);
        }

        public bool Allowed { get { return !store.Policy.DisableFriends; } }
        public bool Active { get { return Allowed && Config.IsConfigured; } }

        public List<FriendStatus> Others { get { lock (gate) return others.ToList(); } }
        public FriendStatus Mine { get { lock (gate) return mine; } }

        /// <summary>設定を保存する。公開をやめたら共有フォルダから自分のファイルを消す</summary>
        public void Apply(FriendsConfig next)
        {
            var prev = Config;
            if (prev.IsConfigured && (!next.IsConfigured || !string.Equals(prev.Folder, next.Folder, StringComparison.OrdinalIgnoreCase)))
                try { Friends.Withdraw(prev); } catch { }
            next.Id = prev.Id;
            next.Save(store.Paths.DataDir);
            lock (gate) { Config = next; lastPayload = null; others = new List<FriendStatus>(); mine = null; LastError = null; }
        }

        public void Tick()
        {
            var cfg = Config;
            if (!Allowed || !cfg.IsConfigured) { lock (gate) { others = new List<FriendStatus>(); mine = null; } return; }
            var now = DateTime.UtcNow;
            try
            {
                var me = Friends.BuildMine(store, cfg, now);
                // 中身が変わったとき、または5分おきに書く（更新時刻でフレンドに「動いている」ことを知らせる）
                string payload = Json.Serialize(new Dictionary<string, object> { { "n", me.Name }, { "c5", me.ClaudeFiveHour }, { "cw", me.ClaudeWeekly },
                    { "x", me.Codex.Select(w => (object)(w.Label + ":" + Math.Floor(w.Remaining))).ToList() } });
                if (payload != lastPayload || now - lastWriteUtc > TimeSpan.FromMinutes(5))
                {
                    Friends.Publish(cfg, me);
                    lastPayload = payload;
                    lastWriteUtc = now;
                }
                var list = Friends.ReadOthers(cfg, now);
                lock (gate) { mine = me; others = list; LastError = null; LastReadUtc = now; }
            }
            catch (Exception ex)
            {
                lock (gate) LastError = ex is IOException || ex is UnauthorizedAccessException
                    ? "共有フォルダに書き込めません（" + Hardening.SafeLine(ex.Message, 160) + "）"
                    : "フレンドの残り枠を更新できませんでした";
            }
        }
    }
}
