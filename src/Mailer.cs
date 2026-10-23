using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Text;

namespace AIUsageBar
{
    public class MailException : Exception
    {
        public MailException(string message) : base(message) { }
    }

    /// <summary>
    /// 招待メールを SMTP で直接送信する設定と送信処理。
    /// 送信用ログイン情報（アプリパスワード）は DPAPI で本人のWindowsアカウントだけが復号できる形で保存し、
    /// 設定した SMTP サーバー以外へは送らない。アプリ自身のサーバーやインターネットの他のサービスには接続しない。
    /// </summary>
    public class MailConfig
    {
        public string Host;
        public int Port = 587;
        public bool UseSsl = true;      // STARTTLS も含めた暗号化
        public string FromEmail;
        public string FromName;
        public string Username;         // 通常は FromEmail と同じ
        string passwordProtected;       // DPAPI 保護済み（平文は保持しない）

        public bool HasPassword { get { return !string.IsNullOrEmpty(passwordProtected); } }

        public bool IsComplete
        {
            get { return !string.IsNullOrWhiteSpace(Host) && Port > 0 && !string.IsNullOrWhiteSpace(FromEmail) && HasPassword; }
        }

        public void SetPassword(string plain)
        {
            passwordProtected = string.IsNullOrEmpty(plain) ? null : TeamCrypto.DpapiProtect(plain, "smtp-password");
        }

        string GetPassword()
        {
            if (string.IsNullOrEmpty(passwordProtected)) return "";
            try { return TeamCrypto.DpapiUnprotect(passwordProtected, "smtp-password"); }
            catch (TeamException) { throw new MailException("保存した送信用パスワードを読み取れません（別のWindowsアカウントでは使えません）。設定し直してください。"); }
        }

        // ------------------------------------------------------------------ 保存・読み込み

        static string Path(AppPaths paths) { return System.IO.Path.Combine(paths.DataDir, "mail.json"); }

        public static MailConfig Load(AppPaths paths)
        {
            var c = new MailConfig();
            try
            {
                string p = Path(paths);
                if (!File.Exists(p)) return c;
                var o = Json.Parse(FileUtil.ReadShared(p));
                c.Host = Json.Str(o, "host");
                c.Port = (int)(Json.Long(o, "port") ?? 587);
                c.UseSsl = Json.Bool(o, "useSsl") ?? true;
                c.FromEmail = Json.Str(o, "fromEmail");
                c.FromName = Json.Str(o, "fromName");
                c.Username = Json.Str(o, "username");
                c.passwordProtected = Json.Str(o, "password");
            }
            catch { }
            return c;
        }

        public void Save(AppPaths paths)
        {
            Hardening.SecureDirectory(paths.DataDir);
            var d = new Dictionary<string, object>
            {
                { "host", Host }, { "port", (long)Port }, { "useSsl", UseSsl }, { "fromEmail", FromEmail },
                { "fromName", FromName }, { "username", Username }, { "password", passwordProtected },
            };
            FileUtil.WriteAtomic(Path(paths), Json.Serialize(d, true));
        }

        /// <summary>よく使う送信サーバーの候補（メールアドレスのドメインから推測）</summary>
        public static void ApplyPreset(MailConfig c, string email)
        {
            string domain = (email ?? "").Split('@').LastOrDefault();
            if (string.IsNullOrEmpty(domain)) return;
            domain = domain.ToLowerInvariant();
            if (domain == "gmail.com" || domain == "googlemail.com") { c.Host = "smtp.gmail.com"; c.Port = 587; c.UseSsl = true; }
            else if (domain == "outlook.com" || domain == "hotmail.com" || domain == "live.com") { c.Host = "smtp-mail.outlook.com"; c.Port = 587; c.UseSsl = true; }
            else if (domain.EndsWith("onmicrosoft.com") || domain == "office365.com") { c.Host = "smtp.office365.com"; c.Port = 587; c.UseSsl = true; }
            else if (domain == "yahoo.co.jp" || domain == "yahoo.com") { c.Host = "smtp.mail.yahoo.co.jp"; c.Port = 587; c.UseSsl = true; }
            else { c.Host = "smtp." + domain; c.Port = 587; c.UseSsl = true; }
            if (string.IsNullOrEmpty(c.Username)) c.Username = email;
        }

        // ------------------------------------------------------------------ 送信

        /// <summary>招待メールを送信する。宛先・件名・本文は呼び出し側で組み立てる。</summary>
        public void Send(IEnumerable<string> to, string subject, string body)
        {
            if (!IsComplete) throw new MailException("送信の設定が未完了です");
            var recipients = (to ?? new string[0]).Select(x => (x ?? "").Trim())
                .Where(x => TeamConfig.NormalizeEmail(x) != null).Distinct().ToList();
            if (recipients.Count == 0) throw new MailException("宛先のメールアドレスがありません");

            using (var msg = new MailMessage())
            {
                msg.From = new MailAddress(FromEmail, FromName ?? FromEmail, Encoding.UTF8);
                foreach (var r in recipients) msg.To.Add(r);
                msg.SubjectEncoding = Encoding.UTF8;
                msg.BodyEncoding = Encoding.UTF8;
                msg.Subject = subject ?? "";
                msg.Body = body ?? "";
                using (var client = new SmtpClient(Host, Port))
                {
                    client.EnableSsl = UseSsl;
                    client.DeliveryMethod = SmtpDeliveryMethod.Network;
                    client.UseDefaultCredentials = false;
                    client.Credentials = new NetworkCredential(Username ?? FromEmail, GetPassword());
                    client.Timeout = 30000;
                    try
                    {
                        client.Send(msg);
                    }
                    catch (SmtpException ex)
                    {
                        throw new MailException(FriendlyError(ex));
                    }
                    catch (InvalidOperationException ex)
                    {
                        throw new MailException("送信設定を確認してください：" + ex.Message);
                    }
                }
            }
        }

        static string FriendlyError(SmtpException ex)
        {
            string m = (ex.Message ?? "");
            var code = ex.StatusCode;
            if (code == SmtpStatusCode.MustIssueStartTlsFirst || code == SmtpStatusCode.ClientNotPermitted
                || m.IndexOf("5.7", StringComparison.Ordinal) >= 0 || m.ToLowerInvariant().Contains("auth"))
                return "ログインに失敗しました。メールアドレスと送信用パスワード（Gmail・Outlook は「アプリパスワード」）を確認してください。"
                    + "\n\n" + m;
            return "メールを送信できませんでした：" + m;
        }
    }
}
