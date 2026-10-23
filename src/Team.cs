using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AIUsageBar
{
    /// <summary>マスターと共有する範囲</summary>
    public enum ShareScope { Usage = 0, Summary = 1, Prompts = 2 }

    public static class ScopeText
    {
        public static string Label(ShareScope s)
        {
            switch (s)
            {
                case ShareScope.Prompts: return "依頼文まで";
                case ShareScope.Summary: return "作業の概要まで";
                default: return "利用量のみ";
            }
        }

        const string Always = "PC名・Windowsユーザー名・メールアドレス、利用量（トークン数・残り枠・日別・モデル別）";

        public static string Describe(ShareScope s)
        {
            switch (s)
            {
                case ShareScope.Prompts: return Always + "、セッションのタイトル・作業フォルダ・時間帯、Claude Code・Codex に入力した依頼文の本文（直近14日）";
                case ShareScope.Summary: return Always + "、セッションのタイトル・作業フォルダ・時間帯（直近14日。依頼文の本文は含まない）";
                default: return Always + "（作業内容・依頼文は含まない）";
            }
        }

        public static string Key(ShareScope s) { return s == ShareScope.Prompts ? "prompts" : s == ShareScope.Summary ? "summary" : "usage"; }

        public static ShareScope Parse(string s)
        {
            return s == "prompts" ? ShareScope.Prompts : s == "summary" ? ShareScope.Summary : ShareScope.Usage;
        }

        public static ShareScope Min(ShareScope a, ShareScope b) { return (ShareScope)Math.Min((int)a, (int)b); }
    }

    public class PlanEntry
    {
        public string Name;
        public long MonthlyFeeJpy;
    }

    public class MemberEntry
    {
        public string Id;
        public string Name;
        public List<string> Emails = new List<string>();
        public bool IsMaster;
        public ShareScope Scope = ShareScope.Usage;
        // 以下はマスターだけが読める（team.json では暗号化）
        public List<PlanEntry> Plans = new List<PlanEntry>();
        public string Note;

        public long MonthlyFeeJpy { get { return Plans.Sum(p => p.MonthlyFeeJpy); } }

        public bool Matches(IEnumerable<string> emails)
        {
            var mine = new HashSet<string>(Emails.Select(TeamConfig.NormalizeEmail).Where(e => e != null));
            return emails.Select(TeamConfig.NormalizeEmail).Any(e => e != null && mine.Contains(e));
        }
    }

    /// <summary>API換算の単価（1Mトークンあたり・米ドル）。null は未設定</summary>
    public class PriceEntry
    {
        public string Pattern;          // モデル名の先頭一致（例：claude-opus-5）
        public double? Input;
        public double? Output;
        public double? CacheRead;       // Claude：キャッシュ読み取り / Codex：キャッシュ済み入力
        public double? CacheWrite5m;
        public double? CacheWrite1h;
        public string Source;
    }

    public class RotationStep
    {
        public int Generation;
        public Dictionary<string, object> PublicKey;
        public string Proof;            // 1つ前の世代の鍵による署名
    }

    /// <summary>共有フォルダの team.json（マスターがチーム鍵で署名）</summary>
    public class TeamConfig
    {
        public const string Format = "aiub-team-2";

        public string TeamId;
        public string TeamName;
        public long Version;
        public int KeyGeneration = 1;
        public Dictionary<string, object> PublicKey;
        public List<RotationStep> Rotations = new List<RotationStep>();   // 第2世代以降の鍵の更新履歴
        public ShareScope DefaultScope = ShareScope.Usage;
        public double UsdJpy = 150;
        /// <summary>登録したメールアドレスで Claude・Codex にログインしているPCの報告を、マスターの承認なしで集計する</summary>
        public bool AutoApproveByLogin = true;
        public List<MemberEntry> Members = new List<MemberEntry>();
        public List<PriceEntry> Prices = new List<PriceEntry>();
        public DateTime UpdatedAtUtc;
        public string UpdatedBy;
        public Dictionary<string, object> PrivateSealed;   // メンバーのプラン・メモ（チーム公開鍵で暗号化）
        public bool PrivateOpened;

        public string Fingerprint { get { return TeamCrypto.Fingerprint(PublicKey); } }

        public static string NormalizeEmail(string e)
        {
            if (string.IsNullOrWhiteSpace(e)) return null;
            e = e.Trim().ToLowerInvariant();
            return e.Contains("@") && e.Length <= 254 ? e : null;
        }

        public MemberEntry MemberFor(IEnumerable<string> emails)
        {
            var list = emails.Where(e => e != null).ToList();
            return Members.FirstOrDefault(m => m.Matches(list));
        }

        public ShareScope ScopeFor(IEnumerable<string> emails)
        {
            var m = MemberFor(emails);
            return m != null ? m.Scope : DefaultScope;
        }

        /// <summary>
        /// 報告を1人のメンバーに結び付ける。検出したメール（Claude・Codex のログイン）と申告メールが
        /// 別々のメンバーに一致する場合は ambiguous（どちらにも集計しない）。
        /// </summary>
        public MemberEntry ResolveMember(IEnumerable<string> detected, string declared, out bool ambiguous)
        {
            ambiguous = false;
            var det = detected.Select(NormalizeEmail).Where(e => e != null).Distinct().ToList();
            var byDetected = Members.Where(m => m.Matches(det)).ToList();
            var decl = NormalizeEmail(declared);
            var byDeclared = decl == null ? new List<MemberEntry>() : Members.Where(m => m.Matches(new[] { decl })).ToList();
            var all = byDetected.Concat(byDeclared).Distinct().ToList();
            if (all.Count > 1) { ambiguous = true; return null; }
            return all.FirstOrDefault();
        }

        /// <summary>
        /// 「メールアドレス」「名前, メールアドレス」「名前 &lt;メールアドレス&gt;」を1行ずつ読み取る（Excel・Slack からの貼り付け用）。
        /// 名前がない場合はメールアドレスの @ より前を名前にする。
        /// </summary>
        public static List<MemberEntry> ParseMemberList(string text, List<string> errors)
        {
            var list = new List<MemberEntry>();
            var seen = new HashSet<string>();
            int lineNo = 0;
            foreach (var raw in (text ?? "").Replace("\r\n", "\n").Split('\n', '\r'))
            {
                lineNo++;
                string line = Hardening.SafeLine(raw, 400);
                if (string.IsNullOrWhiteSpace(line)) continue;
                var m = System.Text.RegularExpressions.Regex.Match(line, @"[^\s<>,;:""'()\[\]\t]+@[^\s<>,;:""'()\[\]\t]+");
                string email = m.Success ? NormalizeEmail(m.Value.Trim('.')) : null;
                if (email == null || !email.Substring(email.IndexOf('@') + 1).Contains("."))
                {
                    if (errors != null) errors.Add(lineNo + "行目：メールアドレスが見つかりません（" + line + "）");
                    continue;
                }
                if (!seen.Add(email)) continue;
                string name = (line.Substring(0, m.Index) + " " + line.Substring(m.Index + m.Length)).Trim(' ', '\t', ',', ';', '<', '>', '"', '\'', '(', ')', '　', ':');
                if (string.IsNullOrWhiteSpace(name)) name = email.Substring(0, email.IndexOf('@'));
                list.Add(new MemberEntry { Id = Guid.NewGuid().ToString("N"), Name = Hardening.SafeLine(name, 80), Emails = { email } });
            }
            return list;
        }

        public PriceEntry FindPrice(string model)
        {
            if (string.IsNullOrEmpty(model)) return null;
            string lower = model.ToLowerInvariant();
            return Prices.Where(p => !string.IsNullOrEmpty(p.Pattern) && lower.StartsWith(p.Pattern.ToLowerInvariant()))
                .OrderByDescending(p => p.Pattern.Length).FirstOrDefault();
        }

        /// <summary>
        /// Anthropic API の公式料金（2026-06 時点の資料）。キャッシュ読み取りは入力の0.1倍、
        /// キャッシュ書き込みは5分が1.25倍・1時間が2倍（Claude Fable 5.1 の読み取りのみ $0.25）。
        /// OpenAI のモデルは確かな資料がないため未設定（マスターが入力する）。
        /// </summary>
        public static List<PriceEntry> DefaultPrices()
        {
            const string src = "Anthropic API 公式料金（2026-06時点の資料）";
            Func<string, double, double, double?, PriceEntry> claude = (pattern, input, output, read) => new PriceEntry
            {
                Pattern = pattern, Input = input, Output = output, CacheRead = read ?? Math.Round(input * 0.1, 6),
                CacheWrite5m = Math.Round(input * 1.25, 6), CacheWrite1h = Math.Round(input * 2, 6), Source = src,
            };
            return new List<PriceEntry>
            {
                claude("claude-fable-5-1", 10, 50, 0.25),
                claude("claude-fable-5", 10, 50, 1.0),
                claude("claude-mythos-5-1", 10, 50, null),
                claude("claude-opus-5", 5, 25, null),
                claude("claude-opus-4-8", 5, 25, null),
                claude("claude-opus-4-7", 5, 25, null),
                claude("claude-opus-4-6", 5, 25, null),
                claude("claude-sonnet-5", 2, 10, null),
                claude("claude-sonnet-4-6", 3, 15, null),
                claude("claude-haiku-4-5", 1, 5, null),
                new PriceEntry { Pattern = "gpt-", Source = "未設定（OpenAI の料金表で確認して入力してください）" },
            };
        }

        public TeamConfig Clone()
        {
            var c = FromJson(ToJson());
            if (PrivateOpened)
            {
                c.PrivateOpened = true;
                foreach (var m in c.Members)
                {
                    var src = Members.FirstOrDefault(x => x.Id == m.Id);
                    if (src == null) continue;
                    m.Note = src.Note;
                    m.Plans = src.Plans.Select(p => new PlanEntry { Name = p.Name, MonthlyFeeJpy = p.MonthlyFeeJpy }).ToList();
                }
            }
            return c;
        }

        public Dictionary<string, object> ToJson()
        {
            var d = new Dictionary<string, object>();
            d["format"] = Format;
            d["teamId"] = TeamId;
            d["teamName"] = TeamName;
            d["version"] = Version;
            d["keyGeneration"] = (long)KeyGeneration;
            d["publicKey"] = PublicKey;
            d["rotations"] = Rotations.Select(r => (object)new Dictionary<string, object> { { "generation", (long)r.Generation }, { "publicKey", r.PublicKey }, { "proof", r.Proof } }).ToList();
            d["defaultScope"] = ScopeText.Key(DefaultScope);
            d["usdJpy"] = UsdJpy;
            d["autoApproveByLogin"] = AutoApproveByLogin;
            d["updatedAt"] = UpdatedAtUtc.ToString("o", CultureInfo.InvariantCulture);
            d["updatedBy"] = UpdatedBy;
            d["members"] = Members.Select(m => (object)new Dictionary<string, object> {
                { "id", m.Id }, { "name", m.Name }, { "emails", m.Emails.Cast<object>().ToList() }, { "role", m.IsMaster ? "master" : "general" },
                { "scope", ScopeText.Key(m.Scope) } }).ToList();
            d["prices"] = Prices.Select(p => (object)new Dictionary<string, object> {
                { "pattern", p.Pattern }, { "input", p.Input }, { "output", p.Output }, { "cacheRead", p.CacheRead },
                { "cacheWrite5m", p.CacheWrite5m }, { "cacheWrite1h", p.CacheWrite1h }, { "source", p.Source } }).ToList();
            d["privateData"] = PrivateSealed;
            return d;
        }

        public static TeamConfig FromJson(object o)
        {
            if (Json.Str(o, "format") != Format) throw new TeamException("チーム設定の形式が正しくありません（対応していない版です）");
            var t = new TeamConfig
            {
                TeamId = Json.Str(o, "teamId"),
                TeamName = Hardening.SafeLine(Json.Str(o, "teamName"), 80),
                Version = Json.Long(o, "version") ?? 0,
                KeyGeneration = (int)Math.Max(1, Math.Min(100000, Json.Long(o, "keyGeneration") ?? 1)),
                PublicKey = Json.Obj(o, "publicKey"),
                DefaultScope = ScopeText.Parse(Json.Str(o, "defaultScope")),
                UsdJpy = Math.Max(1, Math.Min(1000, Json.Num(o, "usdJpy") ?? 150)),
                AutoApproveByLogin = Json.Bool(o, "autoApproveByLogin") ?? true,
                UpdatedBy = Hardening.SafeLine(Json.Str(o, "updatedBy"), 80),
                PrivateSealed = Json.Obj(o, "privateData"),
            };
            DateTime up;
            if (DateTime.TryParse(Json.Str(o, "updatedAt"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out up)) t.UpdatedAtUtc = up;
            if (t.TeamId == null || t.PublicKey == null) throw new TeamException("チーム設定の形式が正しくありません");
            var rotations = Json.Arr(o, "rotations");
            if (rotations != null) foreach (var r in rotations.Take(1000))
                t.Rotations.Add(new RotationStep { Generation = (int)(Json.Long(r, "generation") ?? 0), PublicKey = Json.Obj(r, "publicKey"), Proof = Json.Str(r, "proof") });
            var members = Json.Arr(o, "members");
            if (members != null) foreach (var m in members.Take(5000))
            {
                var e = new MemberEntry
                {
                    Id = Json.Str(m, "id") ?? Guid.NewGuid().ToString("N"),
                    Name = Hardening.SafeLine(Json.Str(m, "name"), 80),
                    IsMaster = Json.Str(m, "role") == "master",
                    Scope = ScopeText.Parse(Json.Str(m, "scope")),
                };
                var emails = Json.Arr(m, "emails");
                if (emails != null) foreach (var x in emails.Take(20)) if (x is string) e.Emails.Add(Hardening.SafeLine((string)x, 254));
                t.Members.Add(e);
            }
            var prices = Json.Arr(o, "prices");
            if (prices != null) foreach (var p in prices.Take(1000))
                t.Prices.Add(new PriceEntry
                {
                    Pattern = Hardening.SafeLine(Json.Str(p, "pattern"), 80), Input = Json.Num(p, "input"), Output = Json.Num(p, "output"),
                    CacheRead = Json.Num(p, "cacheRead"), CacheWrite5m = Json.Num(p, "cacheWrite5m"), CacheWrite1h = Json.Num(p, "cacheWrite1h"),
                    Source = Hardening.SafeLine(Json.Str(p, "source"), 200),
                });
            return t;
        }

        /// <summary>メンバーのプラン・メモをチーム公開鍵で暗号化して PrivateSealed に入れる</summary>
        public void SealPrivate(RSA teamPublic)
        {
            var data = new Dictionary<string, object>();
            foreach (var m in Members)
                data[m.Id] = new Dictionary<string, object> {
                    { "note", m.Note },
                    { "plans", m.Plans.Select(p => (object)new Dictionary<string, object> { { "name", p.Name }, { "monthlyFeeJpy", p.MonthlyFeeJpy } }).ToList() } };
            PrivateSealed = TeamCrypto.EncryptForTeam(teamPublic, Encoding.UTF8.GetBytes(Json.Serialize(data)), Encoding.UTF8.GetBytes("aiub-team-private:" + TeamId));
        }

        public void OpenPrivate(RSA teamPrivate)
        {
            if (PrivateSealed == null) { PrivateOpened = true; return; }
            var plain = TeamCrypto.DecryptForTeam(teamPrivate, PrivateSealed, Encoding.UTF8.GetBytes("aiub-team-private:" + TeamId));
            var data = Json.Parse(Encoding.UTF8.GetString(plain)) as Dictionary<string, object>;
            foreach (var m in Members)
            {
                object v;
                if (data == null || !data.TryGetValue(m.Id, out v)) continue;
                m.Note = Json.Str(v, "note");
                m.Plans.Clear();
                var plans = Json.Arr(v, "plans");
                if (plans != null) foreach (var p in plans) m.Plans.Add(new PlanEntry { Name = Hardening.SafeLine(Json.Str(p, "name"), 80), MonthlyFeeJpy = Math.Max(0, Json.Long(p, "monthlyFeeJpy") ?? 0) });
            }
            PrivateOpened = true;
        }
    }

    /// <summary>このPCのチーム参加状態（%LOCALAPPDATA%\AIUsageBar\team-local.json。鍵は DPAPI で保護）</summary>
    public class TeamLocal
    {
        public string InstallId;
        public string SharedFolder;          // AIUsageBar-Team フォルダ
        public string TeamId;
        public int PinnedGeneration;
        public string PinnedPublicKey;        // 参加時に確認した公開鍵（JSON）
        public long MaxVersionSeen;
        public string MemberKeyProtected;     // このPCの署名鍵（DPAPI）
        public string MasterKeyProtected;     // チーム秘密鍵（DPAPI）。マスターのみ
        public ShareScope ConsentedScope;
        public DateTime? ConsentedAtUtc;
        public string DeclaredName;
        public string DeclaredEmail;
        public DateTime? LastUploadUtc;
        public string LastPayloadHash;
        public string LastError;
        public bool Corrupt;                  // ファイルが壊れていた（勝手に初期化しない）

        static readonly object SaveGate = new object();

        public bool Joined { get { return !Corrupt && TeamId != null && SharedFolder != null; } }
        public bool HasMasterKey { get { return MasterKeyProtected != null; } }

        public static string PathIn(string dataDir) { return Path.Combine(dataDir, "team-local.json"); }

        public static TeamLocal Load(string dataDir)
        {
            var l = new TeamLocal();
            string p = PathIn(dataDir);
            lock (SaveGate)
            {
                if (!File.Exists(p)) { l.InstallId = Guid.NewGuid().ToString("N"); return l; }
                try
                {
                    var o = Json.Parse(FileUtil.ReadShared(p));
                    l.InstallId = Json.Str(o, "installId");
                    l.SharedFolder = Json.Str(o, "sharedFolder");
                    l.TeamId = Json.Str(o, "teamId");
                    l.PinnedGeneration = (int)(Json.Long(o, "pinnedGeneration") ?? 0);
                    l.PinnedPublicKey = Json.Str(o, "pinnedPublicKey");
                    l.MaxVersionSeen = Json.Long(o, "maxVersionSeen") ?? 0;
                    l.MemberKeyProtected = Json.Str(o, "memberKey");
                    l.MasterKeyProtected = Json.Str(o, "masterKey");
                    l.ConsentedScope = ScopeText.Parse(Json.Str(o, "consentedScope"));
                    long? ca = Json.Long(o, "consentedAt");
                    if (ca != null && Fmt.ValidUnixMs(ca.Value)) l.ConsentedAtUtc = Fmt.FromUnixMs(ca.Value);
                    l.DeclaredName = Json.Str(o, "declaredName");
                    l.DeclaredEmail = Json.Str(o, "declaredEmail");
                    long? lu = Json.Long(o, "lastUpload");
                    if (lu != null && Fmt.ValidUnixMs(lu.Value)) l.LastUploadUtc = Fmt.FromUnixMs(lu.Value);
                    l.LastPayloadHash = Json.Str(o, "lastPayloadHash");
                    if (l.InstallId == null || l.InstallId.Length != 32 || !l.InstallId.All(Uri.IsHexDigit)) throw new FormatException();
                }
                catch (Exception)
                {
                    // 壊れたファイルで黙って初期化すると、PCの識別子や鍵が変わってしまうので止める
                    return new TeamLocal { Corrupt = true, LastError = "チームの参加情報（team-local.json）が壊れています。チームに参加し直してください（壊れたファイルは保存時に退避します）" };
                }
            }
            return l;
        }

        public void Save(string dataDir)
        {
            var d = new Dictionary<string, object>
            {
                { "installId", InstallId }, { "sharedFolder", SharedFolder }, { "teamId", TeamId }, { "pinnedGeneration", (long)PinnedGeneration },
                { "pinnedPublicKey", PinnedPublicKey }, { "maxVersionSeen", MaxVersionSeen }, { "memberKey", MemberKeyProtected },
                { "masterKey", MasterKeyProtected }, { "consentedScope", ScopeText.Key(ConsentedScope) },
                { "consentedAt", ConsentedAtUtc == null ? (object)null : Fmt.ToUnixMs(ConsentedAtUtc.Value) },
                { "declaredName", DeclaredName }, { "declaredEmail", DeclaredEmail },
                { "lastUpload", LastUploadUtc == null ? (object)null : Fmt.ToUnixMs(LastUploadUtc.Value) },
                { "lastPayloadHash", LastPayloadHash },
            };
            lock (SaveGate)
            {
                Hardening.SecureDirectory(dataDir);
                string p = PathIn(dataDir);
                if (Corrupt && File.Exists(p)) File.Move(p, p + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
                Corrupt = false;
                FileUtil.WriteAtomic(p, Json.Serialize(d, true));
            }
        }
    }

    /// <summary>マスターが読み込んだ1台分の報告</summary>
    public class TeamReport
    {
        public string InstallId;
        public string FilePath;
        public DateTime CreatedAtUtc;
        public string MemberFingerprint;
        public bool SignatureOk;
        public bool KeyChanged;
        public bool EmailsChanged;
        public bool Approved;
        public bool AutoApproved;       // ログイン中のメールアドレスが登録と一致したため自動で承認
        public string Error;

        public string WindowsUser, Machine, DeclaredName, DeclaredEmail, AppVersion;
        public List<string> DetectedEmails = new List<string>();
        public ShareScope Scope;
        public ShareScope ConsentedScope;
        public object Quota;
        public List<DailyUsage> Daily = new List<DailyUsage>();
        public List<DailyTokens> CodexServiceDaily = new List<DailyTokens>();
        public List<object> Sessions = new List<object>();
        public List<object> Prompts = new List<object>();

        public IEnumerable<string> AllEmails
        {
            get { return DetectedEmails.Concat(new[] { DeclaredEmail }).Select(TeamConfig.NormalizeEmail).Where(e => e != null).Distinct(); }
        }

        /// <summary>承認時に記録するメールアドレスの組（並べ替えて連結）</summary>
        public string EmailKey { get { return string.Join(",", AllEmails.OrderBy(e => e, StringComparer.Ordinal)); } }
    }

    /// <summary>
    /// 招待コード：共有フォルダの場所・チーム名・チーム確認コードをまとめた文字列（秘密情報は含まない）。
    /// Slack・メールなど共有フォルダとは別の経路で送るので、共有フォルダをすり替えられても確認コードで気づける。
    /// </summary>
    public static class TeamInvite
    {
        public const string Prefix = "AIUB-INVITE-1:";
        public const string UrlScheme = "aiusagebar";

        static string EncodeRaw(string folder, string teamName, string fingerprint)
        {
            var d = new Dictionary<string, object> { { "folder", folder }, { "team", teamName }, { "fingerprint", fingerprint } };
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(Json.Serialize(d))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        public static string Encode(string folder, string teamName, string fingerprint)
        {
            return Prefix + EncodeRaw(folder, teamName, fingerprint);
        }

        /// <summary>クリックで参加画面が開くリンク（アプリがインストール済みのPCで有効）</summary>
        public static string LinkUrl(TeamConfig team, string folder)
        {
            return UrlScheme + ":join?c=" + EncodeRaw(folder, team.TeamName, team.Fingerprint);
        }

        /// <summary>aiusagebar:join?c=... のリンクから招待コードを取り出す</summary>
        public static bool TryDecodeLink(string url, out string folder, out string teamName, out string fingerprint)
        {
            folder = teamName = fingerprint = null;
            if (string.IsNullOrEmpty(url)) return false;
            var m = System.Text.RegularExpressions.Regex.Match(url, @"[?&]c=([A-Za-z0-9_-]{16,4000})");
            if (!m.Success) return false;
            return TryDecode(Prefix + m.Groups[1].Value, out folder, out teamName, out fingerprint);
        }

        /// <summary>貼り付けた文章の中から招待コードを探して読む</summary>
        public static bool TryDecode(string text, out string folder, out string teamName, out string fingerprint)
        {
            folder = teamName = fingerprint = null;
            if (string.IsNullOrEmpty(text) || text.Length > 20000) return false;
            var m = System.Text.RegularExpressions.Regex.Match(text, System.Text.RegularExpressions.Regex.Escape(Prefix) + "([A-Za-z0-9_-]{16,4000})");
            if (!m.Success) return false;
            try
            {
                string b64 = m.Groups[1].Value.Replace('-', '+').Replace('_', '/');
                b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
                var o = Json.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
                folder = Hardening.SafeLine(Json.Str(o, "folder"), 1000);
                teamName = Hardening.SafeLine(Json.Str(o, "team"), 80);
                string fp = TeamCrypto.NormalizeFingerprint(Json.Str(o, "fingerprint"));
                if (fp.Length != 32 || string.IsNullOrWhiteSpace(folder)) { folder = teamName = null; return false; }
                fingerprint = string.Join("-", Enumerable.Range(0, 8).Select(i => fp.Substring(i * 4, 4)));
                return true;
            }
            catch (FormatException) { return false; }
            catch (ArgumentException) { return false; }
        }

        public static string MailSubject(TeamConfig team) { return "AI Usage Bar チーム「" + team.TeamName + "」への招待"; }

        /// <summary>簡素なメール本文。アプリ導入済みのPCでは、リンクを開くと参加画面（共有するかの選択）が開く。</summary>
        public static string MailBody(TeamConfig team, string folder)
        {
            return "AI Usage Bar のチーム「" + team.TeamName + "」への招待です。\r\n\r\n" +
                "▼ 参加する（AI Usage Bar をインストール後、下のリンクを開くと参加画面が開きます。共有するかどうかを選べます）\r\n" +
                LinkUrl(team, folder) + "\r\n\r\n" +
                "リンクが開かない場合は、アプリの「チーム」タブ →「チームに参加する」に、次の招待コードを貼り付けてください：\r\n" +
                Encode(folder, team.TeamName, team.Fingerprint) + "\r\n";
        }

        /// <summary>既定のメールアプリで作成画面を開く mailto: URL</summary>
        public static string MailtoUrl(IEnumerable<string> to, TeamConfig team, string folder)
        {
            string rcpt = string.Join(",", (to ?? new string[0]).Select(TeamConfig.NormalizeEmail).Where(e => e != null).Select(Uri.EscapeDataString));
            return "mailto:" + rcpt + "?subject=" + Uri.EscapeDataString(MailSubject(team)) + "&body=" + Uri.EscapeDataString(MailBody(team, folder));
        }

        /// <summary>ブラウザの Gmail で作成画面を開く URL</summary>
        public static string GmailUrl(IEnumerable<string> to, TeamConfig team, string folder)
        {
            string rcpt = string.Join(",", (to ?? new string[0]).Select(TeamConfig.NormalizeEmail).Where(e => e != null));
            return "https://mail.google.com/mail/?view=cm&fs=1&to=" + Uri.EscapeDataString(rcpt) + "&su=" + Uri.EscapeDataString(MailSubject(team)) + "&body=" + Uri.EscapeDataString(MailBody(team, folder));
        }

        /// <summary>メンバーに送る案内文（簡素版）</summary>
        public static string Message(TeamConfig team, string folder)
        {
            return MailBody(team, folder);
        }
    }

    public static class CostCalc
    {
        /// <summary>1日分・1モデル分の API 換算（米ドル）。単価が未設定なら null</summary>
        public static double? Usd(DailyUsage d, TeamConfig team)
        {
            var p = team.FindPrice(d.Model);
            if (p == null) return null;
            var s = d.Sum;
            if (d.Tool == Tool.ClaudeCode)
            {
                if (p.Input == null || p.Output == null) return null;
                long cw1h = Math.Min(s.CacheWrite1h, s.CacheWrite);
                return (s.Input * p.Input.Value + s.Output * p.Output.Value + s.CacheRead * (p.CacheRead ?? p.Input.Value)
                    + (s.CacheWrite - cw1h) * (p.CacheWrite5m ?? p.Input.Value) + cw1h * (p.CacheWrite1h ?? p.CacheWrite5m ?? p.Input.Value)) / 1e6;
            }
            if (p.Input == null || p.Output == null) return null;
            long uncached = Math.Max(0, s.Input - s.CacheRead);
            return (uncached * p.Input.Value + s.CacheRead * (p.CacheRead ?? p.Input.Value) + s.Output * p.Output.Value) / 1e6;
        }

        public class Total
        {
            public double Jpy;
            public long UnpricedTokens;
            public long Tokens;
        }

        public static Total Sum(IEnumerable<DailyUsage> days, TeamConfig team)
        {
            var t = new Total();
            foreach (var d in days)
            {
                t.Tokens += d.Sum.Total;
                var usd = Usd(d, team);
                if (usd == null) t.UnpricedTokens += d.Sum.Total;
                else t.Jpy += usd.Value * team.UsdJpy;
            }
            return t;
        }
    }

    /// <summary>
    /// チームの作成・参加・報告・読み込み。共有フォルダの構成：
    ///   AIUsageBar-Team\team.json       … チーム設定（マスターの署名付き。メンバー・権限・閲覧範囲・単価。プランとメモは暗号化）
    ///   AIUsageBar-Team\team-key.enc    … チーム秘密鍵（パスフレーズで暗号化）
    ///   AIUsageBar-Team\reports\*.aiubr … 各PCの報告（チーム公開鍵で暗号化＋PCの鍵で署名）
    /// </summary>
    public class TeamService
    {
        public const string FolderName = "AIUsageBar-Team";
        public const int MinPassphraseLength = 12;
        public const int SessionDays = 14;
        public const int MaxPromptChars = 8000;
        public const int MaxReportBytes = 8 * 1024 * 1024;
        public const int MaxReports = 500;
        const int MaxTeamFileBytes = 2 * 1024 * 1024;
        const string KeyFileFormat = "aiub-team-key-2";
        const string ReportFormat = "aiub-report-2";

        readonly UsageStore store;
        readonly Activity activity;
        public UsageStore Store { get { return store; } }
        readonly object gate = new object();   // 報告・退出・同意の変更を同時に行わない
        public TeamLocal Local;
        public TeamConfig Team;           // 最後に検証できたチーム設定
        public string TeamError;

        public TeamService(UsageStore store, Activity activity)
        {
            this.store = store;
            this.activity = activity;
            Local = TeamLocal.Load(store.Paths.DataDir);
            if (Local.Corrupt) TeamError = Local.LastError;
        }

        string DataDir { get { return store.Paths.DataDir; } }
        public static string TeamFile(string folder) { return Path.Combine(folder, "team.json"); }
        public static string KeyFile(string folder) { return Path.Combine(folder, "team-key.enc"); }
        public static string ReportsDir(string folder) { return Path.Combine(folder, "reports"); }
        string MyReportPath { get { return Local.SharedFolder == null || Local.InstallId == null ? null : Path.Combine(ReportsDir(Local.SharedFolder), Local.InstallId + ".aiubr"); } }
        static string KeyPurpose(string teamId, int generation) { return "aiub-team-key:" + teamId + ":" + generation; }

        public bool Allowed { get { return !store.Policy.DisableTeamSharing; } }

        /// <summary>マスターとして使えるか（チーム秘密鍵を持ち、チーム設定でもマスター）。本当の境界はパスフレーズ（鍵）</summary>
        public bool IsMaster
        {
            get
            {
                if (!Local.Joined || !Local.HasMasterKey || Team == null) return false;
                var me = Team.MemberFor(MyEmails());
                return me != null && me.IsMaster;
            }
        }

        /// <summary>このPCで検出したメール（Claude・Codex にログインしているアカウント）</summary>
        public List<string> MyDetectedEmails()
        {
            var list = new List<string>();
            var ca = activity.ClaudeAccount;
            if (ca != null && ca.Email != null) list.Add(ca.Email);
            if (store.Codex != null && store.Codex.AccountEmail != null) list.Add(store.Codex.AccountEmail);
            return list.Select(TeamConfig.NormalizeEmail).Where(e => e != null).Distinct().ToList();
        }

        /// <summary>このPCの確認コード（マスターが承認時に照合する）</summary>
        public string MyPcFingerprint()
        {
            if (!Local.Joined || Local.MemberKeyProtected == null) return null;
            if (pcFingerprintFor == Local.MemberKeyProtected) return pcFingerprint;
            try { using (var k = TeamCrypto.ImportPrivateJson(TeamCrypto.DpapiUnprotect(Local.MemberKeyProtected, "member-key"))) pcFingerprint = TeamCrypto.Fingerprint(TeamCrypto.ExportPublic(k)); }
            catch { pcFingerprint = null; }
            pcFingerprintFor = Local.MemberKeyProtected;
            return pcFingerprint;
        }
        string pcFingerprint, pcFingerprintFor;

        public List<string> MyEmails()
        {
            var list = MyDetectedEmails();
            var d = TeamConfig.NormalizeEmail(Local.DeclaredEmail);
            if (d != null && !list.Contains(d)) list.Add(d);
            return list;
        }

        public static string ResolveFolder(string chosen)
        {
            if (string.IsNullOrWhiteSpace(chosen)) throw new TeamException("共有フォルダを選んでください");
            chosen = chosen.Trim();
            if (File.Exists(TeamFile(chosen))) return chosen;
            return Path.Combine(chosen, FolderName);
        }

        public static void CheckPassphrase(string passphrase)
        {
            if (passphrase == null || passphrase.Length < MinPassphraseLength) throw new TeamException("パスフレーズは" + MinPassphraseLength + "文字以上にしてください");
            if (passphrase.Distinct().Count() < 6) throw new TeamException("パスフレーズには6種類以上の文字を使ってください（同じ文字の繰り返しは推測されやすいため）");
        }

        static void CheckReadable(string path, int maxBytes)
        {
            var fi = new FileInfo(path);
            if (fi.Length > maxBytes) throw new TeamException("ファイルが大きすぎます（" + Path.GetFileName(path) + "）");
        }

        // ------------------------------------------------------------------ 作成（マスター）

        public void CreateTeam(string chosenFolder, string teamName, string masterName, string masterEmail, string passphrase)
        {
            if (!Allowed) throw new TeamException("組織のポリシーでチーム機能は無効になっています");
            if (string.IsNullOrWhiteSpace(teamName)) throw new TeamException("チーム名を入力してください");
            if (TeamConfig.NormalizeEmail(masterEmail) == null) throw new TeamException("メールアドレスを正しく入力してください");
            CheckPassphrase(passphrase);
            string folder = ResolveFolder(chosenFolder);
            if (File.Exists(TeamFile(folder))) throw new TeamException("このフォルダには既にチームがあります。「チームに参加」を使ってください");
            Directory.CreateDirectory(ReportsDir(folder));

            using (var teamKey = TeamCrypto.NewKey(TeamCrypto.TeamKeyBits))
            using (var memberKey = TeamCrypto.NewKey(TeamCrypto.MemberKeyBits))
            {
                var team = new TeamConfig
                {
                    TeamId = Guid.NewGuid().ToString("N"),
                    TeamName = teamName.Trim(),
                    Version = 1,
                    KeyGeneration = 1,
                    PublicKey = TeamCrypto.ExportPublic(teamKey),
                    DefaultScope = ShareScope.Usage,
                    Prices = TeamConfig.DefaultPrices(),
                    UpdatedAtUtc = DateTime.UtcNow,
                    UpdatedBy = masterName,
                };
                team.Members.Add(new MemberEntry { Id = Guid.NewGuid().ToString("N"), Name = masterName, Emails = { masterEmail.Trim() }, IsMaster = true, Scope = ShareScope.Prompts });
                team.SealPrivate(teamKey);
                WriteSigned(TeamFile(folder), team.ToJson(), teamKey);
                string privateJson = TeamCrypto.ExportPrivateJson(teamKey);
                WriteKeyFile(folder, privateJson, passphrase, team.TeamId, 1);

                Local.SharedFolder = folder;
                Local.TeamId = team.TeamId;
                Local.PinnedGeneration = 1;
                Local.PinnedPublicKey = Json.Serialize(team.PublicKey);
                Local.MaxVersionSeen = team.Version;
                Local.MemberKeyProtected = TeamCrypto.DpapiProtect(TeamCrypto.ExportPrivateJson(memberKey), "member-key");
                Local.MasterKeyProtected = TeamCrypto.DpapiProtect(privateJson, "team-key");
                Local.ConsentedScope = ShareScope.Prompts;
                Local.ConsentedAtUtc = DateTime.UtcNow;
                Local.DeclaredName = masterName;
                Local.DeclaredEmail = masterEmail.Trim();
                if (Local.InstallId == null) Local.InstallId = Guid.NewGuid().ToString("N");
                Local.Save(DataDir);
                team.PrivateOpened = true;
                Team = team;
                TeamError = null;
            }
        }

        static void WriteSigned(string path, Dictionary<string, object> doc, RSA key)
        {
            doc["signature"] = TeamCrypto.Sign(key, doc);
            FileUtil.WriteAtomic(path, Json.Serialize(doc, true));
        }

        static void WriteKeyFile(string folder, string privateJson, string passphrase, string teamId, int generation)
        {
            var keyFile = TeamCrypto.ProtectWithPassphrase(Encoding.UTF8.GetBytes(privateJson), passphrase, KeyPurpose(teamId, generation));
            keyFile["format"] = KeyFileFormat;
            FileUtil.WriteAtomic(KeyFile(folder), Json.Serialize(keyFile, true));
        }

        // ------------------------------------------------------------------ 参加（一般ユーザー）

        /// <summary>参加前の確認用。署名は自己検証のみ（本物かどうかは、マスターから伝えられた確認コードと照合する）</summary>
        public static TeamConfig Peek(string chosenFolder, out string folder)
        {
            folder = ResolveFolder(chosenFolder);
            if (!File.Exists(TeamFile(folder))) throw new TeamException("このフォルダにチームが見つかりません（team.json がありません）");
            CheckReadable(TeamFile(folder), MaxTeamFileBytes);
            var doc = Json.Parse(FileUtil.ReadShared(TeamFile(folder))) as Dictionary<string, object>;
            var team = TeamConfig.FromJson(doc);
            using (var pub = TeamCrypto.ImportPublic(team.PublicKey, TeamCrypto.TeamKeyBits))
                if (!TeamCrypto.Verify(pub, doc)) throw new TeamException("チーム設定の署名が正しくありません（改ざんの可能性があります）");
            return team;
        }

        /// <summary>
        /// 参加する。typedFingerprint：マスターから伝えられたチーム確認コード（本人が入力）。
        /// confirmed：画面で確認したチーム設定。読み直した内容と違えば参加しない（確認と参加の間のすり替え対策）。
        /// </summary>
        public void Join(string chosenFolder, string typedFingerprint, string name, string email, ShareScope consent, TeamConfig confirmed = null)
        {
            if (!Allowed) throw new TeamException("組織のポリシーでチーム機能は無効になっています");
            if (TeamConfig.NormalizeEmail(email) == null) throw new TeamException("メールアドレスを正しく入力してください");
            string folder;
            var team = Peek(chosenFolder, out folder);
            if (confirmed != null && (confirmed.TeamId != team.TeamId || Json.Serialize(confirmed.PublicKey) != Json.Serialize(team.PublicKey)))
                throw new TeamException("確認した後にチーム設定が変わりました。もう一度「読み込む」から確認してください");
            if (TeamCrypto.NormalizeFingerprint(typedFingerprint) != TeamCrypto.NormalizeFingerprint(team.Fingerprint))
                throw new TeamException("入力したチーム確認コードが、共有フォルダのチームと一致しません。マスターに正しい確認コードを確認してください（一致しない場合は参加しないでください）");
            lock (gate)
            {
                using (var memberKey = TeamCrypto.NewKey(TeamCrypto.MemberKeyBits))
                {
                    if (Local.InstallId == null) Local.InstallId = Guid.NewGuid().ToString("N");
                    Local.SharedFolder = folder;
                    Local.TeamId = team.TeamId;
                    Local.PinnedGeneration = team.KeyGeneration;
                    Local.PinnedPublicKey = Json.Serialize(team.PublicKey);
                    Local.MaxVersionSeen = team.Version;
                    Local.MemberKeyProtected = TeamCrypto.DpapiProtect(TeamCrypto.ExportPrivateJson(memberKey), "member-key");
                    Local.MasterKeyProtected = null;
                    Local.ConsentedScope = consent;
                    Local.ConsentedAtUtc = DateTime.UtcNow;
                    Local.DeclaredName = string.IsNullOrWhiteSpace(name) ? Environment.UserName : name.Trim();
                    Local.DeclaredEmail = email.Trim();
                    Local.LastPayloadHash = null;
                    Local.LastUploadUtc = null;
                    Local.Save(DataDir);
                }
                Team = team;
                TeamError = null;
            }
        }

        /// <summary>同意する範囲を変える。狭めた場合は、共有フォルダの報告をすぐに削除する</summary>
        public void UpdateConsent(ShareScope consent)
        {
            lock (gate)
            {
                bool narrowed = consent < Local.ConsentedScope;
                Local.ConsentedScope = consent;
                Local.ConsentedAtUtc = DateTime.UtcNow;
                Local.LastPayloadHash = null;
                Local.Save(DataDir);
                if (narrowed) DeleteMyReport();
            }
        }

        void DeleteMyReport()
        {
            try { string p = MyReportPath; if (p != null && File.Exists(p)) File.Delete(p); } catch { }
        }

        public void Leave()
        {
            lock (gate)
            {
                DeleteMyReport();
                string installId = Local.InstallId ?? Guid.NewGuid().ToString("N");
                Local = new TeamLocal { InstallId = installId, Corrupt = Local.Corrupt };   // 壊れたファイルは保存時に退避
                Local.Save(DataDir);
                Team = null;
                TeamError = null;
            }
        }

        /// <summary>チームのパスフレーズでマスター権限を有効にする（マスターに指定された人が、自分のPCで行う）</summary>
        public void UnlockMaster(string passphrase)
        {
            var team = LoadTeam();
            if (team.MemberFor(MyEmails()) == null || !team.MemberFor(MyEmails()).IsMaster)
                throw new TeamException("チーム設定で、このPCのアカウントがマスターに指定されていません。マスターに登録を確認してください");
            string path = KeyFile(Local.SharedFolder);
            if (!File.Exists(path)) throw new TeamException("チームの鍵ファイルが見つかりません");
            CheckReadable(path, 64 * 1024);
            var keyDoc = Json.Parse(FileUtil.ReadShared(path));
            if (Json.Str(keyDoc, "format") != KeyFileFormat) throw new TeamException("鍵ファイルの形式が正しくありません");
            string privateJson = Encoding.UTF8.GetString(TeamCrypto.UnprotectWithPassphrase(keyDoc, passphrase, KeyPurpose(team.TeamId, team.KeyGeneration)));
            using (var key = TeamCrypto.ImportPrivateJson(privateJson))
            {
                if (Json.Serialize(TeamCrypto.ExportPublic(key)) != Json.Serialize(team.PublicKey)) throw new TeamException("鍵ファイルが現在のチーム鍵と一致しません");
                team.OpenPrivate(key);
            }
            Local.MasterKeyProtected = TeamCrypto.DpapiProtect(privateJson, "team-key");
            Local.Save(DataDir);
            Team = team;
        }

        // ------------------------------------------------------------------ チーム設定の読み込み・検証

        /// <summary>
        /// team.json を読み、参加時に確認した鍵（または前の鍵の署名でつながった新しい鍵）で署名を検証する。
        /// 古い版への巻き戻し、鍵のすり替え、改ざんは拒否する。マスターでなくなったPCからはチーム秘密鍵を消す。
        /// </summary>
        public TeamConfig LoadTeam()
        {
            if (Local.Corrupt) throw new TeamException(Local.LastError);
            if (!Local.Joined) throw new TeamException("チームに参加していません");
            string path = TeamFile(Local.SharedFolder);
            Dictionary<string, object> doc;
            try
            {
                if (!File.Exists(path)) throw new TeamException("共有フォルダのチーム設定が見つかりません（" + Local.SharedFolder + "）");
                CheckReadable(path, MaxTeamFileBytes);
                doc = Json.Parse(FileUtil.ReadShared(path)) as Dictionary<string, object>;
            }
            catch (IOException) { throw new TeamException("共有フォルダに接続できません"); }
            catch (UnauthorizedAccessException) { throw new TeamException("共有フォルダを読む権限がありません"); }
            catch (FormatException) { throw new TeamException("チーム設定の形式が正しくありません"); }
            var team = TeamConfig.FromJson(doc);
            if (team.TeamId != Local.TeamId) throw new TeamException("共有フォルダのチームが、参加したチームと違います");
            if (team.Version < Local.MaxVersionSeen) throw new TeamException("チーム設定が古い版に戻されています（改ざんの可能性があります）");
            if (team.KeyGeneration < Local.PinnedGeneration) throw new TeamException("チームの鍵が古い世代に戻されています（改ざんの可能性があります）");

            var pinned = Json.Parse(Local.PinnedPublicKey) as Dictionary<string, object>;
            bool sameKey = Json.Serialize(pinned) == Json.Serialize(team.PublicKey);
            if (!sameKey)
            {
                // 鍵の更新：固定した世代から現在の世代まで、各世代の鍵が1つ前の鍵で署名されていること
                if (team.KeyGeneration == Local.PinnedGeneration) throw new TeamException("チームの鍵が変わっています（改ざんの可能性があります）。マスターに確認してください");
                var current = pinned;
                for (int g = Local.PinnedGeneration + 1; g <= team.KeyGeneration; g++)
                {
                    var step = team.Rotations.FirstOrDefault(r => r.Generation == g);
                    if (step == null || step.PublicKey == null || step.Proof == null) throw new TeamException("チームの鍵の更新を確認できません（改ざんの可能性があります）");
                    using (var prev = TeamCrypto.ImportPublic(current, TeamCrypto.TeamKeyBits))
                        if (!TeamCrypto.VerifyBytes(prev, RotationStatement(team.TeamId, g, step.PublicKey), step.Proof))
                            throw new TeamException("チームの鍵の更新を確認できません（改ざんの可能性があります）");
                    current = step.PublicKey;
                }
                if (Json.Serialize(current) != Json.Serialize(team.PublicKey)) throw new TeamException("チームの鍵の更新を確認できません（改ざんの可能性があります）");
            }
            using (var pub = TeamCrypto.ImportPublic(team.PublicKey, TeamCrypto.TeamKeyBits))
                if (!TeamCrypto.Verify(pub, doc)) throw new TeamException("チーム設定の署名が正しくありません（改ざんの可能性があります）");

            bool changed = false;
            if (!sameKey)
            {
                Local.PinnedGeneration = team.KeyGeneration;
                Local.PinnedPublicKey = Json.Serialize(team.PublicKey);
                if (Local.HasMasterKey)
                {
                    // 旧鍵は破棄（新しいパスフレーズで解除し直す）。ただし、このPCが保存済みの新しい鍵なら残す
                    bool keep = false;
                    try { using (var k = MasterKey()) keep = Json.Serialize(TeamCrypto.ExportPublic(k)) == Json.Serialize(team.PublicKey); } catch { }
                    if (!keep) Local.MasterKeyProtected = null;
                }
                changed = true;
            }
            var me = team.MemberFor(MyEmails());
            if (Local.HasMasterKey && (me == null || !me.IsMaster))
            {
                Local.MasterKeyProtected = null;   // マスターから外されたPCでは鍵を消す
                changed = true;
            }
            if (team.Version > Local.MaxVersionSeen) { Local.MaxVersionSeen = team.Version; changed = true; }
            if (changed) Local.Save(DataDir);

            if (Local.HasMasterKey)
            {
                try { using (var key = MasterKey()) team.OpenPrivate(key); }
                catch (TeamException) { }
                catch (CryptographicException) { }
            }
            Team = team;
            TeamError = null;
            return team;
        }

        public bool TryRefreshTeam()
        {
            try { LoadTeam(); return true; }
            catch (TeamException ex) { TeamError = ex.Message; return false; }
            catch (Exception ex) { TeamError = "チーム設定を読めません（" + ex.GetType().Name + "）"; return false; }
        }

        static byte[] RotationStatement(string teamId, int generation, Dictionary<string, object> newPublic)
        {
            return TeamCrypto.Canonical(new Dictionary<string, object> { { "purpose", "aiub-key-rotation" }, { "teamId", teamId }, { "generation", (long)generation }, { "publicKey", newPublic } });
        }

        RSA MasterKey()
        {
            if (!Local.HasMasterKey) throw new TeamException("このPCではマスター権限が有効になっていません");
            return TeamCrypto.ImportPrivateJson(TeamCrypto.DpapiUnprotect(Local.MasterKeyProtected, "team-key"));
        }

        /// <summary>マスターがチーム設定を保存（版を上げて署名）。他のマスターの更新と衝突したら止める</summary>
        public void SaveTeam(TeamConfig edited, string editorName)
        {
            if (!IsMaster) throw new TeamException("マスターだけがチーム設定を変更できます");
            var current = LoadTeam();
            if (current.Version != edited.Version) throw new TeamException("他のマスターがチーム設定を更新しました。画面を読み込み直してから変更してください");
            Validate(edited);
            edited.Version = current.Version + 1;
            edited.UpdatedAtUtc = DateTime.UtcNow;
            edited.UpdatedBy = editorName;
            edited.PublicKey = current.PublicKey;
            edited.KeyGeneration = current.KeyGeneration;
            edited.Rotations = current.Rotations;
            // プラン・メモを読めていない状態で保存すると消えてしまうので、その場合は元の暗号データを引き継ぐ
            if (!edited.PrivateOpened) edited.PrivateSealed = current.PrivateSealed;
            using (var key = MasterKey())
            {
                if (edited.PrivateOpened) edited.SealPrivate(key);
                WriteSigned(TeamFile(Local.SharedFolder), edited.ToJson(), key);
            }
            LoadTeam();
        }

        /// <summary>メンバーをまとめて追加する（既に登録済みのメールアドレスは飛ばす）。追加した人数を返す</summary>
        public int AddMembers(IEnumerable<MemberEntry> newMembers, ShareScope scope, string editorName, List<string> skipped)
        {
            if (!IsMaster) throw new TeamException("マスターだけがメンバーを追加できます");
            var edited = LoadTeam().Clone();
            var existing = new HashSet<string>(edited.Members.SelectMany(m => m.Emails).Select(TeamConfig.NormalizeEmail).Where(e => e != null));
            int added = 0;
            foreach (var m in newMembers)
            {
                string email = m.Emails.Select(TeamConfig.NormalizeEmail).FirstOrDefault(e => e != null);
                if (email == null || existing.Contains(email)) { if (skipped != null && email != null) skipped.Add(email); continue; }
                existing.Add(email);
                edited.Members.Add(new MemberEntry { Id = Guid.NewGuid().ToString("N"), Name = m.Name, Emails = { email }, IsMaster = false, Scope = scope });
                added++;
            }
            if (added > 0) SaveTeam(edited, editorName);
            return added;
        }

        static void Validate(TeamConfig edited)
        {
            if (!edited.Members.Any(m => m.IsMaster)) throw new TeamException("マスターを少なくとも1人残してください");
            foreach (var m in edited.Members)
            {
                if (string.IsNullOrWhiteSpace(m.Name)) throw new TeamException("名前が空のメンバーがあります");
                if (m.Emails.Count == 0 || m.Emails.Any(e => TeamConfig.NormalizeEmail(e) == null)) throw new TeamException("「" + m.Name + "」のメールアドレスが正しくありません");
            }
            var dup = edited.Members.SelectMany(m => m.Emails.Select(e => new { m.Id, Email = TeamConfig.NormalizeEmail(e) })).GroupBy(x => x.Email).FirstOrDefault(g => g.Select(x => x.Id).Distinct().Count() > 1);
            if (dup != null) throw new TeamException("メールアドレス " + dup.Key + " が複数のメンバーに登録されています");
        }

        /// <summary>
        /// チーム鍵を作り直す（マスターを外した人・パスフレーズを知った人が、以後の報告を読めないようにする）。
        /// 新しい鍵は前の鍵で署名するので、メンバーのPCは自動で新しい鍵に切り替わる。
        /// edited を渡すと、同じ保存でチーム設定の変更（マスターの解除など）も行う。
        /// </summary>
        public void RotateKey(string newPassphrase, string editorName, TeamConfig edited = null)
        {
            if (!IsMaster) throw new TeamException("マスターだけが鍵を更新できます");
            CheckPassphrase(newPassphrase);
            var current = LoadTeam();
            if (edited != null)
            {
                if (current.Version != edited.Version) throw new TeamException("他のマスターがチーム設定を更新しました。画面を読み込み直してから変更してください");
                if (!edited.PrivateOpened) throw new TeamException("プラン・メモを読み込めていないため、鍵を更新できません");
                Validate(edited);
            }
            if (edited == null && !current.PrivateOpened) throw new TeamException("プラン・メモを読み込めていないため、鍵を更新できません");
            using (var newKey = TeamCrypto.NewKey(TeamCrypto.TeamKeyBits))
            using (var oldKey = MasterKey())
            {
                var next = (edited ?? current).Clone();
                next.KeyGeneration = current.KeyGeneration + 1;
                next.PublicKey = TeamCrypto.ExportPublic(newKey);
                next.Rotations = current.Rotations.ToList();
                next.Rotations.Add(new RotationStep { Generation = next.KeyGeneration, PublicKey = next.PublicKey, Proof = TeamCrypto.SignBytes(oldKey, RotationStatement(next.TeamId, next.KeyGeneration, next.PublicKey)) });
                next.Version = current.Version + 1;
                next.UpdatedAtUtc = DateTime.UtcNow;
                next.UpdatedBy = editorName;
                next.SealPrivate(newKey);
                string privateJson = TeamCrypto.ExportPrivateJson(newKey);
                // 先にこのPCへ新しい鍵を保存し、チーム設定→鍵ファイルの順に書く（途中で失敗しても、このPCから書き直せる）
                string previousLocalKey = Local.MasterKeyProtected;
                Local.MasterKeyProtected = TeamCrypto.DpapiProtect(privateJson, "team-key");
                Local.Save(DataDir);
                try
                {
                    WriteSigned(TeamFile(Local.SharedFolder), next.ToJson(), newKey);
                    WriteKeyFile(Local.SharedFolder, privateJson, newPassphrase, next.TeamId, next.KeyGeneration);
                }
                catch
                {
                    Local.MasterKeyProtected = previousLocalKey;
                    Local.Save(DataDir);
                    throw;
                }
                LoadTeam();
            }
        }

        // ------------------------------------------------------------------ 共有範囲

        /// <summary>実際に共有する範囲＝マスターの指定・本人の同意・組織ポリシーのうち最も狭いもの</summary>
        public ShareScope EffectiveScope(TeamConfig team)
        {
            var scope = ScopeText.Min(team.ScopeFor(MyEmails()), Local.ConsentedScope);
            var p = store.Policy;
            if (p.TeamMaxScope != null) scope = ScopeText.Min(scope, p.TeamMaxScope.Value);
            if (p.DisableActivityLog) scope = ShareScope.Usage;
            else if (p.HidePromptText) scope = ScopeText.Min(scope, ShareScope.Summary);
            return scope;
        }

        /// <summary>マスターが同意より広い範囲を求めている</summary>
        public bool NeedsConsent(TeamConfig team)
        {
            // 同意は「すべて（依頼文まで）共有する」の一択。以前の版で狭い範囲に同意していた場合は確認を求める
            return Local.ConsentedScope < ShareScope.Prompts;
        }

        // ------------------------------------------------------------------ 報告（各PC）

        /// <summary>見本画像の作成用（実際のPC名・ユーザー名を写さない）。通常は null</summary>
        public static string DemoMachine, DemoUser;

        public Dictionary<string, object> BuildPayload(ShareScope scope)
        {
            var d = new Dictionary<string, object>();
            d["appVersion"] = AppInfo.Version;
            d["windowsUser"] = DemoUser ?? Environment.UserName;
            d["machine"] = DemoMachine ?? Environment.MachineName;
            d["declaredName"] = Local.DeclaredName;
            d["declaredEmail"] = Local.DeclaredEmail;
            d["detectedEmails"] = MyDetectedEmails().Cast<object>().ToList();
            d["scope"] = ScopeText.Key(scope);
            d["consentedScope"] = ScopeText.Key(Local.ConsentedScope);

            var quota = new Dictionary<string, object>();
            var now = DateTime.UtcNow;
            if (store.Codex != null && store.Codex.Main != null && !store.CodexStale(now))
            {
                double? rem = store.CodexMinRemaining(now);
                var codex = new Dictionary<string, object> {
                    { "planType", store.Codex.PlanType }, { "remaining", rem == null ? (object)null : Math.Floor(rem.Value) },
                    { "observedAt", Fmt.ToUnixMs(store.Codex.ObservedAtUtc) } };
                // 5時間・週間の枠を個別に（Mac版と同じ表示にするため）
                foreach (var w in store.Codex.Main.Windows.Where(x => !UsageStore.WindowExpired(x, now)))
                {
                    string key = w.WindowMinutes == 300 ? "fiveHour" : w.WindowMinutes == 10080 ? "weekly" : null;
                    if (key == null) continue;
                    codex[key + "Remaining"] = Math.Floor(w.RemainingPercent);
                    if (w.ResetsAtUtc != null) codex[key + "ResetsAt"] = Fmt.ToUnixMs(w.ResetsAtUtc.Value);
                }
                quota["codex"] = codex;
            }
            if (store.Claude != null && !store.Claude.IsStale(now, store.Settings.ClaudeIntervalMinutes))
                quota["claude"] = new Dictionary<string, object> {
                    { "fiveHourRemaining", store.Claude.FiveHourRemaining == null ? (object)null : Math.Floor(store.Claude.FiveHourRemaining.Value) },
                    { "weeklyRemaining", store.Claude.WeeklyRemaining == null ? (object)null : Math.Floor(store.Claude.WeeklyRemaining.Value) },
                    { "observedAt", Fmt.ToUnixMs(store.Claude.SampleAtUtc) } };
            d["quota"] = quota;

            d["daily"] = store.Logs.History().Select(h => (object)new List<object> {
                h.Date, h.Tool == Tool.Codex ? "codex" : "claude", h.Model, h.Sum.Input, h.Sum.Output, h.Sum.CacheRead, h.Sum.CacheWrite,
                h.Sum.CacheWrite1h, h.Sum.Reasoning, h.Sum.Total, (long)h.Sum.Responses }).ToList();
            if (store.Codex != null && store.Codex.Daily != null)
                d["codexServiceDaily"] = store.Codex.Daily.Select(x => (object)new Dictionary<string, object> { { "date", x.Date }, { "tokens", x.Tokens } }).ToList();

            if (scope >= ShareScope.Summary)
            {
                var sessions = new List<object>();
                var prompts = new List<object>();
                var seen = new HashSet<string>();
                var index = store.Logs.ReadCodexIndex();
                var today = DateTime.Now.Date;
                var dayViews = Enumerable.Range(0, SessionDays).Select(i => store.Logs.SessionsForDay(today.AddDays(-i))).ToList();
                var tokensByKey = new Dictionary<string, TokenSum>();
                foreach (var views in dayViews)
                    foreach (var v in views)
                    {
                        TokenSum t;
                        if (!tokensByKey.TryGetValue(v.Info.Key, out t)) { t = new TokenSum(); tokensByKey[v.Info.Key] = t; }
                        t.Add(v.Tokens);
                    }
                long from = Fmt.ToUnixMs(today.AddDays(-(SessionDays - 1)).ToUniversalTime());
                for (int i = 0; i < SessionDays; i++)
                {
                    foreach (var v in dayViews[i])
                    {
                        var s = v.Info;
                        if (!seen.Add(s.Key)) continue;
                        // タイトルは、AIが付けたもの・手動で付けたもの・Codexのスレッド名だけ（依頼文から作らない）
                        string title = store.Logs.ReadTitle(s, index);
                        sessions.Add(new Dictionary<string, object> {
                            { "key", s.Key }, { "tool", s.Tool == Tool.Codex ? "codex" : "claude" }, { "title", title }, { "cwd", s.Cwd },
                            { "start", s.Start }, { "end", s.End }, { "model", s.Model }, { "via", Activity.ViaLabel(s, null) },
                            { "inputKind", Activity.InputKindLabel(s, null) }, { "subagents", (long)v.Subagents.Count },
                            { "prompts", (long)s.Prompts.Count(p => p.T >= from) }, { "totalTokens", tokensByKey[s.Key].Total } });
                        if (scope >= ShareScope.Prompts)
                        {
                            foreach (var p in s.Prompts.Where(p => p.T >= from).OrderBy(p => p.T))
                            {
                                string text = store.Logs.ReadPrompt(s.Tool, p);
                                if (text != null && text.Length > MaxPromptChars) text = text.Substring(0, MaxPromptChars) + "…（以下省略）";
                                prompts.Add(new Dictionary<string, object> {
                                    { "t", p.T }, { "sessionKey", s.Key }, { "tool", s.Tool == Tool.Codex ? "codex" : "claude" },
                                    { "via", Activity.ViaLabel(s, p.Source) }, { "inputKind", Activity.InputKindLabel(s, p.Source) }, { "text", text } });
                            }
                        }
                    }
                }
                d["sessions"] = sessions;
                if (scope >= ShareScope.Prompts) d["prompts"] = prompts;
            }
            return d;
        }

        /// <summary>サイズから共有範囲や量を推測されにくいよう、暗号化前の長さを段階ごとにそろえる</summary>
        public static byte[] Pad(byte[] plain)
        {
            int[] buckets = { 64 * 1024, 256 * 1024, 1024 * 1024, 4 * 1024 * 1024, MaxReportBytes / 2 };
            int target = buckets.FirstOrDefault(b => b >= plain.Length + 64);
            if (target == 0) throw new TeamException("報告が大きすぎます（依頼文の量を減らすか、共有範囲を狭めてください）");
            // JSON の末尾に空白を足しても内容は変わらない
            var padded = new byte[target];
            Buffer.BlockCopy(plain, 0, padded, 0, plain.Length);
            for (int i = plain.Length; i < target; i++) padded[i] = (byte)' ';
            return padded;
        }

        /// <summary>報告を暗号化・署名して共有フォルダに書く。書かなかった場合は理由を返す</summary>
        public string Upload(bool force)
        {
            lock (gate)
            {
                if (!Local.Joined) return "チーム未参加";
                if (!Allowed)
                {
                    DeleteMyReport();   // ポリシーで無効になったら、置いてある報告も消す
                    return "組織のポリシーでチーム機能は無効";
                }
                if (!force && Local.LastUploadUtc != null && (DateTime.UtcNow - Local.LastUploadUtc.Value).TotalMinutes < 5) return "前回から5分以内";
                try
                {
                    var team = LoadTeam();
                    var scope = EffectiveScope(team);
                    string payloadJson = Json.Serialize(BuildPayload(scope));
                    string hash;
                    using (var sha = SHA256.Create()) hash = TeamCrypto.B64(sha.ComputeHash(Encoding.UTF8.GetBytes(payloadJson + "|" + team.KeyGeneration)));
                    // 内容が変わっていなければ書かない（ただし1時間に1回は生存確認として書く）
                    if (!force && hash == Local.LastPayloadHash && Local.LastUploadUtc != null && (DateTime.UtcNow - Local.LastUploadUtc.Value).TotalMinutes < 60)
                        return "変更なし";
                    byte[] plain = Pad(Encoding.UTF8.GetBytes(payloadJson));
                    var header = new Dictionary<string, object> {
                        { "format", ReportFormat }, { "teamId", team.TeamId }, { "keyGeneration", (long)team.KeyGeneration },
                        { "installId", Local.InstallId }, { "createdAt", Fmt.ToUnixMs(DateTime.UtcNow) } };
                    using (var memberKey = TeamCrypto.ImportPrivateJson(TeamCrypto.DpapiUnprotect(Local.MemberKeyProtected, "member-key")))
                    using (var teamPub = TeamCrypto.ImportPublic(team.PublicKey, TeamCrypto.TeamKeyBits))
                    {
                        header["memberPublicKey"] = TeamCrypto.ExportPublic(memberKey);
                        var sealedData = TeamCrypto.EncryptForTeam(teamPub, plain, TeamCrypto.Canonical(header));
                        var doc = new Dictionary<string, object>(header);
                        doc["payload"] = sealedData;
                        doc["signature"] = TeamCrypto.Sign(memberKey, doc);
                        if (!Local.Joined) return "チーム未参加";
                        Directory.CreateDirectory(ReportsDir(Local.SharedFolder));
                        FileUtil.WriteAtomic(MyReportPath, Json.Serialize(doc));
                    }
                    Local.LastUploadUtc = DateTime.UtcNow;
                    Local.LastPayloadHash = hash;
                    Local.LastError = null;
                    Local.Save(DataDir);
                    return null;
                }
                catch (TeamException ex) { Local.LastError = ex.Message; TeamError = ex.Message; return ex.Message; }
                catch (IOException) { Local.LastError = "共有フォルダに書き込めません"; return Local.LastError; }
                catch (UnauthorizedAccessException) { Local.LastError = "共有フォルダに書き込む権限がありません"; return Local.LastError; }
                catch (CryptographicException) { Local.LastError = "このPCの鍵を読み取れません（チームに参加し直してください）"; return Local.LastError; }
            }
        }

        // ------------------------------------------------------------------ 読み込み（マスター）

        string PinsPath { get { return Path.Combine(DataDir, "team-pins.json"); } }

        Dictionary<string, Dictionary<string, object>> LoadPins()
        {
            var map = new Dictionary<string, Dictionary<string, object>>();
            try
            {
                if (File.Exists(PinsPath))
                {
                    var o = Json.Parse(FileUtil.ReadShared(PinsPath)) as Dictionary<string, object>;
                    if (o != null && Json.Str(o, "teamId") == Local.TeamId)
                    {
                        var pins = Json.Obj(o, "pins");
                        if (pins != null) foreach (var kv in pins) { var v = kv.Value as Dictionary<string, object>; if (v != null) map[kv.Key] = v; }
                    }
                }
            }
            catch { }
            return map;
        }

        void SavePins(Dictionary<string, Dictionary<string, object>> pins)
        {
            // 180日以上報告のないPCの記録は消す
            long old = Fmt.ToUnixMs(DateTime.UtcNow.AddDays(-180));
            foreach (var k in pins.Where(kv => (Json.Long(kv.Value, "lastSeen") ?? 0) < old).Select(kv => kv.Key).ToList()) pins.Remove(k);
            var d = new Dictionary<string, object> { { "teamId", Local.TeamId }, { "pins", pins.ToDictionary(kv => kv.Key, kv => (object)kv.Value) } };
            Hardening.SecureDirectory(DataDir);
            FileUtil.WriteAtomic(PinsPath, Json.Serialize(d, true));
        }

        /// <summary>
        /// すべての報告を検証して開く。PCごとに鍵・メールアドレス・最新の作成日時を記録し、
        /// 初めてのPC、鍵やメールアドレスが変わったPCは承認待ち、古い報告への差し戻しはエラーにする。
        /// </summary>
        public List<TeamReport> ReadReports()
        {
            if (!IsMaster) throw new TeamException("マスターだけが報告を読めます");
            var team = Team;
            var list = new List<TeamReport>();
            var pins = LoadPins();
            bool pinsChanged = false;
            string dir = ReportsDir(Local.SharedFolder);
            if (!Directory.Exists(dir)) return list;
            var files = new DirectoryInfo(dir).GetFiles("*.aiubr").OrderByDescending(f => f.LastWriteTimeUtc).Take(MaxReports).ToList();
            long nowMs = Fmt.ToUnixMs(DateTime.UtcNow);
            using (var teamKey = MasterKey())
            {
                foreach (var file in files)
                {
                    var r = new TeamReport { FilePath = file.FullName, InstallId = Path.GetFileNameWithoutExtension(file.Name) };
                    list.Add(r);
                    try
                    {
                        if (r.InstallId.Length != 32 || !r.InstallId.All(Uri.IsHexDigit)) throw new TeamException("報告ファイルの名前が正しくありません");
                        if (file.Length > MaxReportBytes + 1024 * 1024) throw new TeamException("報告ファイルが大きすぎます");
                        var doc = Json.Parse(FileUtil.ReadShared(file.FullName)) as Dictionary<string, object>;
                        if (Json.Str(doc, "format") != ReportFormat || Json.Str(doc, "teamId") != team.TeamId) throw new TeamException("このチームの報告ではありません");
                        if (Json.Str(doc, "installId") != r.InstallId) throw new TeamException("報告ファイルの名前と中身が一致しません");
                        long? created = Json.Long(doc, "createdAt");
                        if (created == null || !Fmt.ValidUnixMs(created.Value)) throw new TeamException("データ形式が正しくありません");
                        if (created.Value > nowMs + 10 * 60 * 1000) throw new TeamException("作成日時が未来になっています（改ざんまたはPCの時計のずれ）");
                        r.CreatedAtUtc = Fmt.FromUnixMs(created.Value);
                        var memberPub = Json.Obj(doc, "memberPublicKey");
                        r.MemberFingerprint = TeamCrypto.Fingerprint(memberPub);
                        using (var mk = TeamCrypto.ImportPublic(memberPub, TeamCrypto.MemberKeyBits)) r.SignatureOk = TeamCrypto.Verify(mk, doc);
                        if (!r.SignatureOk) throw new TeamException("署名が正しくありません（改ざんの可能性があります）");
                        if ((Json.Long(doc, "keyGeneration") ?? 0) != team.KeyGeneration) throw new TeamException("鍵の更新前の報告です（そのPCの次回の報告を待っています）");

                        var header = new Dictionary<string, object>(doc);
                        header.Remove("payload");
                        header.Remove("signature");
                        byte[] plain = TeamCrypto.DecryptForTeam(teamKey, Json.Obj(doc, "payload"), TeamCrypto.Canonical(header));
                        ParsePayload(r, Json.Parse(Encoding.UTF8.GetString(plain).TrimEnd(' ')));

                        Dictionary<string, object> pin;
                        if (!pins.TryGetValue(r.InstallId, out pin))
                        {
                            pin = new Dictionary<string, object> { { "fingerprint", r.MemberFingerprint }, { "emails", r.EmailKey }, { "approved", false }, { "firstSeen", nowMs }, { "lastCreatedAt", created.Value }, { "lastSeen", nowMs } };
                            pins[r.InstallId] = pin;
                        }
                        r.KeyChanged = Json.Str(pin, "fingerprint") != r.MemberFingerprint;
                        long lastCreated = Json.Long(pin, "lastCreatedAt") ?? 0;
                        if (!r.KeyChanged && created.Value < lastCreated) throw new TeamException("以前より古い報告に差し戻されています（改ざんの可能性があります）");
                        bool pinApproved = Json.Bool(pin, "approved") == true;
                        bool manual = Json.Bool(pin, "manual") == true;
                        bool auto = Json.Bool(pin, "auto") == true;
                        // 承認前のPCは、ログインの変化（Codex のメールは取得後に加わる等）に追従する
                        if (!pinApproved && !r.KeyChanged && Json.Str(pin, "emails") != r.EmailKey) pin["emails"] = r.EmailKey;
                        r.EmailsChanged = Json.Str(pin, "emails") != r.EmailKey;
                        var loginMember = LoginMember(team, r);
                        if (!r.KeyChanged && team.AutoApproveByLogin && loginMember != null && !manual)
                        {
                            // 自動承認：初めてのPC、または自動承認済みのPCが同じメンバーのまま別のメールでもログインした場合
                            if (!pinApproved || (auto && r.EmailsChanged && Json.Str(pin, "member") == loginMember.Id))
                            {
                                pin["approved"] = true;
                                pin["auto"] = true;
                                pin["member"] = loginMember.Id;
                                pin["emails"] = r.EmailKey;
                                pinApproved = auto = true;
                                r.EmailsChanged = false;
                            }
                        }
                        r.Approved = pinApproved && !r.KeyChanged && !r.EmailsChanged;
                        r.AutoApproved = r.Approved && auto && !manual;
                        if (!r.KeyChanged && created.Value > lastCreated) pin["lastCreatedAt"] = created.Value;
                        pin["lastSeen"] = nowMs;
                        pinsChanged = true;
                    }
                    catch (TeamException ex) { r.Error = ex.Message; }
                    catch (IOException) { r.Error = "読み込めませんでした（書き込み中の可能性があります）"; }
                    catch (FormatException) { r.Error = "データ形式が正しくありません"; }
                    catch (CryptographicException) { r.Error = "暗号データを開けません"; }
                    catch (OutOfMemoryException) { r.Error = "報告ファイルが大きすぎます"; }
                }
            }
            if (pinsChanged) SavePins(pins);
            return list;
        }

        /// <summary>
        /// 自動承認に使うメンバー：Claude・Codex に実際にログインしているメール（本人が入力しただけのメールは使わない）が
        /// 登録済みのメンバー1人だけに一致し、申告メールも別のメンバーを指していないこと
        /// </summary>
        static MemberEntry LoginMember(TeamConfig team, TeamReport r)
        {
            if (r.DetectedEmails.Count == 0) return null;
            bool ambiguous;
            var m = team.ResolveMember(r.DetectedEmails, r.DeclaredEmail, out ambiguous);
            if (ambiguous || m == null) return null;
            var byLogin = team.MemberFor(r.DetectedEmails);
            return byLogin != null && byLogin.Id == m.Id ? m : null;
        }

        static void ParsePayload(TeamReport r, object p)
        {
            r.WindowsUser = Hardening.SafeLine(Json.Str(p, "windowsUser"), 64);
            r.Machine = Hardening.SafeLine(Json.Str(p, "machine"), 64);
            r.DeclaredName = Hardening.SafeLine(Json.Str(p, "declaredName"), 80);
            r.DeclaredEmail = Hardening.SafeLine(Json.Str(p, "declaredEmail"), 254);
            r.AppVersion = Hardening.SafeLine(Json.Str(p, "appVersion"), 20);
            var emails = Json.Arr(p, "detectedEmails");
            if (emails != null) foreach (var e in emails.Take(10)) if (e is string) r.DetectedEmails.Add(Hardening.SafeLine((string)e, 254));
            r.Scope = ScopeText.Parse(Json.Str(p, "scope"));
            r.ConsentedScope = ScopeText.Parse(Json.Str(p, "consentedScope"));
            r.Quota = Json.Obj(p, "quota");
            var daily = Json.Arr(p, "daily");
            if (daily != null) foreach (var item in daily.Take(20000))
            {
                var a = item as List<object>;
                if (a == null || a.Count < 11 || !(a[0] is string)) continue;
                var d = new DailyUsage { Date = (string)a[0], Tool = (a[1] as string) == "codex" ? Tool.Codex : Tool.ClaudeCode, Model = Hardening.SafeLine(a[2] as string, 80) };
                d.Sum.Input = L(a[3]); d.Sum.Output = L(a[4]); d.Sum.CacheRead = L(a[5]); d.Sum.CacheWrite = L(a[6]);
                d.Sum.CacheWrite1h = L(a[7]); d.Sum.Reasoning = L(a[8]); d.Sum.Total = L(a[9]); d.Sum.Responses = (int)Math.Min(int.MaxValue, L(a[10]));
                r.Daily.Add(d);
            }
            var svc = Json.Arr(p, "codexServiceDaily");
            if (svc != null) foreach (var x in svc.Take(1000)) r.CodexServiceDaily.Add(new DailyTokens { Date = Json.Str(x, "date"), Tokens = Json.Long(x, "tokens") ?? 0 });
            // マスターが受け取る内容は、報告側が宣言した範囲に従って読む（範囲外のデータは無視する）
            if (r.Scope >= ShareScope.Summary)
            {
                var s = Json.Arr(p, "sessions");
                if (s != null) foreach (var x in s.Take(5000))
                {
                    var dct = x as Dictionary<string, object>;
                    if (dct == null) continue;
                    foreach (var k in new[] { "title", "cwd", "via", "inputKind", "model" })
                        if (dct.ContainsKey(k)) dct[k] = Hardening.SafeLine(dct[k] as string, 300);
                    r.Sessions.Add(dct);
                }
            }
            if (r.Scope >= ShareScope.Prompts)
            {
                var pr = Json.Arr(p, "prompts");
                if (pr != null) foreach (var x in pr.Take(20000))
                {
                    var dct = x as Dictionary<string, object>;
                    if (dct == null) continue;
                    if (dct.ContainsKey("text")) dct["text"] = Hardening.SafeText(dct["text"] as string, MaxPromptChars + 20);
                    foreach (var k in new[] { "via", "inputKind" }) if (dct.ContainsKey(k)) dct[k] = Hardening.SafeLine(dct[k] as string, 80);
                    r.Prompts.Add(dct);
                }
            }
        }

        static long L(object o)
        {
            if (o is long) return Math.Max(0, (long)o);
            if (o is double) { double d = (double)o; return d > 0 && d < long.MaxValue ? (long)d : 0; }
            return 0;
        }

        /// <summary>PCを承認する（その時点の鍵・メールアドレスを記録。以後どちらかが変われば承認待ちに戻る）</summary>
        public void SetApproval(TeamReport r, bool approved)
        {
            if (!IsMaster) throw new TeamException("マスターだけが承認できます");
            if (r.Error != null) throw new TeamException("読めない報告は承認できません");
            var pins = LoadPins();
            long now = Fmt.ToUnixMs(DateTime.UtcNow);
            // マスターが自分で決めたPCは、以後の自動承認の対象にしない
            pins[r.InstallId] = new Dictionary<string, object> {
                { "fingerprint", r.MemberFingerprint }, { "emails", r.EmailKey }, { "approved", approved }, { "manual", true }, { "firstSeen", now },
                { "lastCreatedAt", Fmt.ToUnixMs(r.CreatedAtUtc) }, { "lastSeen", now } };
            SavePins(pins);
        }

        /// <summary>報告ファイルを削除する（退職者・不審な報告など）</summary>
        public void DeleteReport(TeamReport r)
        {
            if (!IsMaster) throw new TeamException("マスターだけが削除できます");
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(r.FilePath)).TrimEnd('\\'), Path.GetFullPath(ReportsDir(Local.SharedFolder)).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new TeamException("報告フォルダ以外のファイルは削除できません");
            File.Delete(r.FilePath);
            var pins = LoadPins();
            if (pins.Remove(r.InstallId)) SavePins(pins);
        }
    }
}
