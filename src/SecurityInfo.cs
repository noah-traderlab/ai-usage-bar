using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Win32;

namespace AIUsageBar
{
    /// <summary>
    /// 設定画面の「仕様・セキュリティ情報を書き出す」。
    /// セキュリティ審査で聞かれる項目（版、ハッシュ、署名、読み書きする場所、通信、子プロセス、ポリシー）を
    /// この端末の実際の値で出力する。会話本文・認証情報・メールアドレスは含めない。
    /// </summary>
    public static class SecurityInfo
    {
        public static string Build(UsageStore store, TeamService team = null)
        {
            var sb = new StringBuilder();
            var paths = store.Paths;
            string exe = Hardening.CurrentExePath;

            sb.AppendLine("# AI Usage Bar 仕様・セキュリティ情報（この端末の実測値）");
            sb.AppendLine();
            sb.AppendLine("出力日時：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            sb.AppendLine();
            sb.AppendLine("> この書き出しには会話本文・依頼文・認証情報・メールアドレスを含めていません。");
            sb.AppendLine();

            sb.AppendLine("## 1. 実行ファイル");
            sb.AppendLine();
            Row(sb, "製品名 / 版", AppInfo.Name + " " + AppInfo.Version);
            Row(sb, "ファイル", exe);
            try
            {
                var fi = new FileInfo(exe);
                Row(sb, "サイズ", fi.Length.ToString("#,0") + " バイト");
                Row(sb, "更新日時", fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"));
                Row(sb, "SHA-256", Sha256(exe));
            }
            catch (Exception ex) { Row(sb, "ファイル情報", "取得できませんでした（" + ex.GetType().Name + "）"); }
            Row(sb, "デジタル署名", SignatureText(exe));
            Row(sb, "実行権限", "asInvoker（管理者権限を要求しない）");
            Row(sb, "実行場所の評価", Hardening.IsRiskyLocation(exe) ? "注意：ダウンロード・デスクトップ・一時フォルダなど他のファイルが置かれやすい場所" : "問題なし");
            Row(sb, "ランタイム", ".NET Framework " + FrameworkVersion() + "（CLR " + Environment.Version + "）");
            Row(sb, "プロセス", (Environment.Is64BitProcess ? "64bit" : "32bit") + " / OS " + Environment.OSVersion.VersionString);
            Row(sb, "外部ライブラリ", "なし（.NET Framework 標準ライブラリのみ）");
            sb.AppendLine();

            sb.AppendLine("## 2. 読み取る場所（この端末で検出したパス）");
            sb.AppendLine();
            sb.AppendLine("| 対象 | パス | 状態 | 読む内容 |");
            sb.AppendLine("|---|---|---|---|");
            string claudeHistory = ClaudeUsage.FindHistory(paths);
            ReadRow(sb, "Claude Desktop 利用枠履歴", claudeHistory ?? string.Join(" / ", paths.ClaudeHistoryCandidates), claudeHistory != null, "時刻・5時間/週間の使用率のみ");
            ReadRow(sb, "Claude Code 会話ログ", paths.ClaudeProjects, Directory.Exists(paths.ClaudeProjects),
                store.Policy.DisableActivityLog ? "トークン使用量のみ（詳細ログはポリシーで無効）" : "トークン使用量、時刻、作業フォルダ、タイトル、依頼文（表示時のみ）");
            string codexSessions = Path.Combine(paths.CodexHome, "sessions");
            ReadRow(sb, "Codex セッションログ", codexSessions + "（archived_sessions を含む）", Directory.Exists(codexSessions),
                store.Policy.DisableActivityLog ? "トークン使用量のみ（詳細ログはポリシーで無効）" : "トークン使用量、時刻、作業フォルダ、モデル、依頼文（表示時のみ）");
            ReadRow(sb, "Codex スレッド名", Path.Combine(paths.CodexHome, "session_index.jsonl"), File.Exists(Path.Combine(paths.CodexHome, "session_index.jsonl")), "スレッドID・スレッド名");
            ReadRow(sb, "Claude Code アカウント表示", Path.Combine(paths.Home, ".claude.json"), File.Exists(Path.Combine(paths.Home, ".claude.json")), "oauthAccount のメール・表示名・組織名・アカウントIDのみ（トークン類は読まない）");
            sb.AppendLine();
            sb.AppendLine("読まないもの：`~/.codex/auth.json`、`~/.claude/.credentials.json`、ブラウザーのデータ、Claude Desktop の Cookie・IndexedDB。");
            sb.AppendLine();

            sb.AppendLine("## 3. 書き込む場所");
            sb.AppendLine();
            sb.AppendLine("| パス | 内容 | サイズ |");
            sb.AppendLine("|---|---|---|");
            foreach (var f in new[] {
                Tuple.Create("settings.json", "表示設定"),
                Tuple.Create("codex-snapshot.json", "Codex利用枠・日別トークン・プラン・アカウント種別（メールアドレスは保存しない）"),
                Tuple.Create("local-logs-v3.json", "トークン使用量、時刻、セッションID、作業フォルダのパス、ログ内の位置、依頼文のHMAC-SHA256（本文なし）"),
                Tuple.Create("hash-key.bin", "HMAC用の端末ごとの乱数鍵（DPAPIで本人のみ復号可能）"),
                Tuple.Create("error.log", "例外発生時のみ（最大1MB）"),
                Tuple.Create("codex-work\\", "Codex CLI を起動するための空の作業フォルダ") })
            {
                string p = Path.Combine(paths.DataDir, f.Item1);
                string size = File.Exists(p) ? new FileInfo(p).Length.ToString("#,0") + " バイト" : (Directory.Exists(p) ? "フォルダ" : "未作成");
                sb.AppendLine("| `" + p + "` | " + f.Item2 + " | " + size + " |");
            }
            sb.AppendLine();
            Row(sb, "保存先のアクセス権", DataDirAcl(paths.DataDir));
            Row(sb, "ログイン時起動", StartupRegistration.IsEnabled() ? "有効（HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run\\AIUsageBar）" : "無効（レジストリに登録なし）");
            Row(sb, "書き出し", "利用者が保存ダイアログで選んだ場所にだけ保存" + (store.Policy.DisableExport ? "（ポリシーで無効）" : ""));
            sb.AppendLine();

            sb.AppendLine("## 4. 通信と子プロセス");
            sb.AppendLine();
            Row(sb, "本アプリ自身の通信", "なし（HTTP・ソケットを使用しない。待ち受けポートなし）");
            Row(sb, "テレメトリ・自動更新", "なし");
            string cli = store.Policy.DisableCodexFetch ? null : CodexClient.FindCli(paths);
            Row(sb, "起動する子プロセス", store.Policy.DisableCodexFetch ? "なし（ポリシーで Codex の取得を無効）" :
                (cli == null ? "Codex CLI 未検出のため起動しない" : "`\"" + cli + "\" -c analytics.enabled=false app-server`"));
            Row(sb, "子プロセスの署名", cli == null ? "—" : SignatureText(cli));
            Row(sb, "子プロセスの通信", "Codex CLI が利用者のログイン情報で OpenAI の利用状況（利用枠・日別トークン・アカウント）を取得。生成リクエストは送らない");
            Row(sb, "起動間隔", store.Settings.CodexIntervalMinutes + "分ごと（1回あたり約1秒、45秒でタイムアウトし子プロセスを終了）");
            Row(sb, "開くURL", "利用者がリンクを押したときのみ https://claude.ai/settings/usage");
            sb.AppendLine();

            sb.AppendLine("## 5. 組織ポリシー（レジストリ）");
            sb.AppendLine();
            sb.AppendLine("読み取り先：`HKLM\\" + Policy.KeyPath + "` と `HKCU\\" + Policy.KeyPath + "`（DWORD、1で有効）");
            sb.AppendLine();
            sb.AppendLine("| 値の名前 | 現在 |");
            sb.AppendLine("|---|---|");
            foreach (var kv in store.Policy.ToDictionary()) sb.AppendLine("| " + kv.Key + " | " + (kv.Value ? "1（有効）" : "未設定") + " |");
            sb.AppendLine();
            if (store.Policy.Sources.Count > 0) sb.AppendLine("検出したポリシー：" + string.Join("、", store.Policy.Sources));
            sb.AppendLine();

            sb.AppendLine("## 5-2. チーム機能");
            sb.AppendLine();
            if (team == null || !team.Local.Joined)
                Row(sb, "参加状況", store.Policy.DisableTeamSharing ? "組織のポリシーで無効" : "参加していない（報告しない）");
            else
            {
                var cfg = team.Team;
                Row(sb, "参加状況", "参加中（" + (team.IsMaster ? "マスター" : "一般ユーザー") + "）");
                Row(sb, "共有フォルダ", team.Local.SharedFolder);
                Row(sb, "チームチーム確認コード", cfg == null ? "（読み込めません）" : cfg.Fingerprint + "（第" + cfg.KeyGeneration + "世代・RSA 3072bit）");
                Row(sb, "共有中の範囲", cfg == null ? "—" : ScopeText.Label(team.EffectiveScope(cfg)) + "（本人の同意：" + ScopeText.Label(team.Local.ConsentedScope) + "）");
                Row(sb, "報告ファイル", Path.Combine(TeamService.ReportsDir(team.Local.SharedFolder), team.Local.InstallId + ".aiubr") + "（チーム公開鍵で暗号化・このPCの鍵で署名）");
                Row(sb, "最終報告", team.Local.LastUploadUtc == null ? "なし" : team.Local.LastUploadUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
                Row(sb, "このPCに保存した鍵", "PCの署名鍵" + (team.Local.HasMasterKey ? "・チーム秘密鍵" : "") + "（DPAPI で本人のみ復号可能。team-local.json）");
            }
            Row(sb, "組織ポリシーの上限", store.Policy.TeamMaxScope == null ? "未設定" : ScopeText.Label(store.Policy.TeamMaxScope.Value));
            sb.AppendLine();

            sb.AppendLine("## 6. 現在の設定");
            sb.AppendLine();
            Row(sb, "Codexの更新間隔", store.Settings.CodexIntervalMinutes + "分");
            Row(sb, "Claude・作業ログの確認間隔", store.Settings.ClaudeIntervalMinutes + "分（PC内のファイルの読み取りのみ）");
            Row(sb, "通知", store.Settings.NotificationsEnabled ? "ON（残り" + store.Settings.NotificationThreshold + "%以下）" : "OFF");
            Row(sb, "詳細ログ", store.ActivityAllowed ? "表示する" : "表示しない");
            Row(sb, "依頼文", store.PromptsAllowed ? "表示する" : "表示しない");
            Row(sb, "タスクバーに表示", store.Settings.ShowInTaskbar ? "ON" : "OFF");
            Row(sb, "起動時はタスクバーに収納", store.Settings.StartMinimized ? "ON" : "OFF");
            sb.AppendLine();

            sb.AppendLine("## 7. 組み込みの安全対策");
            sb.AppendLine();
            foreach (var line in new[] {
                "DLL読み込みを System32 に限定（SetDefaultDllDirectories / DefaultDllImportSearchPaths）。exe と同じフォルダに置かれたDLLを読み込まない",
                "explorer.exe・taskkill.exe・cmd.exe は System32 の絶対パスで起動（同じフォルダの同名exeを実行しない）",
                "Codex CLI の探索で相対パスのPATH項目を無視",
                "Codex CLI は専用の空フォルダを作業フォルダにして起動し、git の上位フォルダ探索を止める",
                "ログ内の位置情報は Claude Code / Codex のログフォルダ配下のファイルだけを読む（キャッシュ改ざん対策）",
                "1行あたりの読み取り上限あり（巨大な行は読み飛ばす）",
                "CSVの数式インジェクション対策（= + - @ で始まるセルを無害化）、Markdownの HTML タグを無害化",
                "保存先フォルダのアクセス権を本人・SYSTEM・Administrators に限定",
                "二重起動防止は同一ログオンセッション内（Local\\）の名前付きオブジェクト",
            }) sb.AppendLine("- " + line);
            sb.AppendLine();
            return sb.ToString();
        }

        static void Row(StringBuilder sb, string key, string value)
        {
            sb.AppendLine("- **" + key + "**：" + value);
        }

        static void ReadRow(StringBuilder sb, string what, string path, bool exists, string content)
        {
            sb.AppendLine("| " + what + " | `" + path + "` | " + (exists ? "あり" : "なし") + " | " + content + " |");
        }

        public static string Sha256(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "");
        }

        static string SignatureText(string path)
        {
            try
            {
                var cert = X509Certificate.CreateFromSignedFile(path);
                return "署名あり：" + cert.Subject + "（Authenticode の有効性はOSの検証結果を確認してください）";
            }
            catch { return "署名なし"; }
        }

        static string FrameworkVersion()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                {
                    var release = k == null ? null : k.GetValue("Release");
                    if (!(release is int)) return "4.x";
                    int r = (int)release;
                    if (r >= 533320) return "4.8.1";
                    if (r >= 528040) return "4.8";
                    if (r >= 461808) return "4.7.2";
                    return "4.x（" + r + "）";
                }
            }
            catch { return "4.x"; }
        }

        static string DataDirAcl(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return "未作成";
                var acl = Directory.GetAccessControl(dir);
                var rules = acl.GetAccessRules(true, true, typeof(System.Security.Principal.NTAccount))
                    .Cast<System.Security.AccessControl.FileSystemAccessRule>()
                    .Select(r => r.IdentityReference.Value + "：" + r.FileSystemRights + (r.IsInherited ? "（継承）" : ""));
                return (acl.AreAccessRulesProtected ? "継承なし／" : "親から継承／") + string.Join("、", rules);
            }
            catch (Exception ex) { return "取得できませんでした（" + ex.GetType().Name + "）"; }
        }
    }
}
