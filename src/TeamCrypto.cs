using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AIUsageBar
{
    public class TeamException : Exception
    {
        public TeamException(string message) : base(message) { }
    }

    /// <summary>
    /// チーム機能の暗号処理（.NET Framework 標準の暗号ライブラリのみ）。
    /// ・チーム鍵：RSA 3072bit。公開鍵で報告を暗号化し、秘密鍵はマスターだけが持つ
    /// ・署名：RSA-PSS / SHA-256（チーム設定はチーム鍵、報告は各PCのメンバー鍵）
    /// ・報告の暗号化：AES-256-CBC ＋ HMAC-SHA256（暗号化してからMAC）、鍵は RSA-OAEP-SHA256 で包む
    /// ・チーム秘密鍵の共有：パスフレーズから PBKDF2-SHA256（60万回）で鍵を作って暗号化
    /// ・このPCでの保存：DPAPI（本人のWindowsアカウントでのみ復号可能）
    /// </summary>
    public static class TeamCrypto
    {
        public const int TeamKeyBits = 3072;
        public const int MemberKeyBits = 2048;
        public const int Pbkdf2Iterations = 600000;

        static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

        public static byte[] Random(int n)
        {
            var b = new byte[n];
            lock (Rng) Rng.GetBytes(b);
            return b;
        }

        public static string B64(byte[] b) { return Convert.ToBase64String(b); }
        public static byte[] UnB64(string s)
        {
            if (s == null) throw new TeamException("データ形式が正しくありません");
            try { return Convert.FromBase64String(s); } catch (FormatException) { throw new TeamException("データ形式が正しくありません"); }
        }

        // ------------------------------------------------------------------ RSA 鍵

        public static RSA NewKey(int bits)
        {
            return new RSACng(bits);
        }

        public static Dictionary<string, object> ExportPublic(RSA rsa)
        {
            var p = rsa.ExportParameters(false);
            return new Dictionary<string, object> { { "n", B64(p.Modulus) }, { "e", B64(p.Exponent) } };
        }

        public static string ExportPrivateJson(RSA rsa)
        {
            var p = rsa.ExportParameters(true);
            var d = new Dictionary<string, object> {
                { "n", B64(p.Modulus) }, { "e", B64(p.Exponent) }, { "d", B64(p.D) }, { "p", B64(p.P) }, { "q", B64(p.Q) },
                { "dp", B64(p.DP) }, { "dq", B64(p.DQ) }, { "qi", B64(p.InverseQ) } };
            return Json.Serialize(d);
        }

        public static RSA ImportPrivateJson(string json)
        {
            var o = Json.Parse(json);
            var p = new RSAParameters
            {
                Modulus = UnB64(Json.Str(o, "n")), Exponent = UnB64(Json.Str(o, "e")), D = UnB64(Json.Str(o, "d")),
                P = UnB64(Json.Str(o, "p")), Q = UnB64(Json.Str(o, "q")), DP = UnB64(Json.Str(o, "dp")),
                DQ = UnB64(Json.Str(o, "dq")), InverseQ = UnB64(Json.Str(o, "qi")),
            };
            var rsa = new RSACng();
            rsa.ImportParameters(p);
            return rsa;
        }

        /// <summary>公開鍵を読み込む。鍵長は minBits〜4096bit、公開指数は 65537 だけを受け入れる</summary>
        public static RSA ImportPublic(object pub, int minBits = 2048)
        {
            var p = new RSAParameters { Modulus = TrimLeadingZeros(UnB64(Json.Str(pub, "n"))), Exponent = TrimLeadingZeros(UnB64(Json.Str(pub, "e"))) };
            int bits = p.Modulus.Length * 8;
            if (bits < minBits || bits > 4096) throw new TeamException("鍵の長さが正しくありません");
            if (p.Exponent.Length != 3 || p.Exponent[0] != 1 || p.Exponent[1] != 0 || p.Exponent[2] != 1) throw new TeamException("鍵の形式が正しくありません");
            var rsa = new RSACng();
            rsa.ImportParameters(p);
            return rsa;
        }

        static byte[] TrimLeadingZeros(byte[] b)
        {
            int i = 0;
            while (i < b.Length - 1 && b[i] == 0) i++;
            return i == 0 ? b : b.Skip(i).ToArray();
        }

        /// <summary>
        /// 公開チーム確認コード：長さ付きで n と e を並べた SHA-256 の先頭128bit（32桁を4桁ずつ区切る）。
        /// 参加時にマスターから伝えられた値を入力して照合する。
        /// </summary>
        public static string Fingerprint(object pub)
        {
            using (var sha = SHA256.Create())
            {
                var n = TrimLeadingZeros(UnB64(Json.Str(pub, "n")));
                var e = TrimLeadingZeros(UnB64(Json.Str(pub, "e")));
                var h = sha.ComputeHash(Frame(Encoding.ASCII.GetBytes("aiub-fingerprint-v2"), n, e));
                string hex = BitConverter.ToString(h, 0, 16).Replace("-", "");
                return string.Join("-", Enumerable.Range(0, 8).Select(i => hex.Substring(i * 4, 4)));
            }
        }

        public static string NormalizeFingerprint(string s)
        {
            if (s == null) return "";
            return new string(s.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
        }

        /// <summary>長さ（8バイト）を前に付けて連結する。区切りのあいまいさをなくす</summary>
        public static byte[] Frame(params byte[][] parts)
        {
            using (var ms = new MemoryStream())
            {
                foreach (var p in parts)
                {
                    ms.Write(BitConverter.GetBytes((long)p.Length), 0, 8);
                    ms.Write(p, 0, p.Length);
                }
                return ms.ToArray();
            }
        }

        // ------------------------------------------------------------------ 署名

        /// <summary>署名対象を一意に並べたJSON（キーを辞書順に並べる）</summary>
        public static byte[] Canonical(object o)
        {
            var sb = new StringBuilder();
            WriteCanonical(sb, o);
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        static void WriteCanonical(StringBuilder sb, object o)
        {
            var dict = o as IDictionary;
            if (dict != null)
            {
                var keys = dict.Keys.Cast<object>().Select(k => Convert.ToString(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
                sb.Append('{');
                for (int i = 0; i < keys.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(Json.Serialize(keys[i])).Append(':');
                    WriteCanonical(sb, dict[keys[i]]);
                }
                sb.Append('}');
                return;
            }
            if (o is string || o == null || o is bool || !(o is IEnumerable)) { sb.Append(Json.Serialize(o)); return; }
            sb.Append('[');
            bool first = true;
            foreach (var item in (IEnumerable)o)
            {
                if (!first) sb.Append(',');
                first = false;
                WriteCanonical(sb, item);
            }
            sb.Append(']');
        }

        public static string Sign(RSA key, Dictionary<string, object> doc)
        {
            var copy = new Dictionary<string, object>(doc);
            copy.Remove("signature");
            return B64(key.SignData(Canonical(copy), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        }

        public static bool Verify(RSA pub, Dictionary<string, object> doc)
        {
            try
            {
                string sig = Json.Str(doc, "signature");
                if (sig == null) return false;
                var copy = new Dictionary<string, object>(doc);
                copy.Remove("signature");
                return pub.VerifyData(Canonical(copy), UnB64(sig), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            }
            catch (CryptographicException) { return false; }
            catch (TeamException) { return false; }
        }

        public static string SignBytes(RSA key, byte[] data) { return B64(key.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)); }

        public static bool VerifyBytes(RSA pub, byte[] data, string sig)
        {
            try { return pub.VerifyData(data, UnB64(sig), HashAlgorithmName.SHA256, RSASignaturePadding.Pss); }
            catch (CryptographicException) { return false; }
            catch (TeamException) { return false; }
        }

        // ------------------------------------------------------------------ 暗号化（AES-256-CBC ＋ HMAC-SHA256）

        static Dictionary<string, object> SealWithKeys(byte[] encKey, byte[] macKey, byte[] plain, byte[] associated)
        {
            byte[] iv = Random(16);
            byte[] ct;
            using (var aes = Aes.Create())
            {
                aes.KeySize = 256; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                using (var enc = aes.CreateEncryptor(encKey, iv)) ct = enc.TransformFinalBlock(plain, 0, plain.Length);
            }
            byte[] mac;
            using (var h = new HMACSHA256(macKey)) mac = h.ComputeHash(Frame(associated, iv, ct));
            return new Dictionary<string, object> { { "iv", B64(iv) }, { "ciphertext", B64(ct) }, { "mac", B64(mac) } };
        }

        static byte[] OpenWithKeys(byte[] encKey, byte[] macKey, object sealedData, byte[] associated)
        {
            byte[] iv = UnB64(Json.Str(sealedData, "iv"));
            byte[] ct = UnB64(Json.Str(sealedData, "ciphertext"));
            byte[] mac = UnB64(Json.Str(sealedData, "mac"));
            if (iv.Length != 16) throw new TeamException("データ形式が正しくありません");
            byte[] expected;
            using (var h = new HMACSHA256(macKey)) expected = h.ComputeHash(Frame(associated, iv, ct));
            if (!FixedTimeEquals(mac, expected)) throw new TeamException("データが改ざんされているか、鍵が違います");
            using (var aes = Aes.Create())
            {
                aes.KeySize = 256; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                using (var dec = aes.CreateDecryptor(encKey, iv)) return dec.TransformFinalBlock(ct, 0, ct.Length);
            }
        }

        static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        /// <summary>チームの公開鍵あてに暗号化（マスターの秘密鍵でだけ開ける）</summary>
        public static Dictionary<string, object> EncryptForTeam(RSA teamPublic, byte[] plain, byte[] associated)
        {
            byte[] keys = Random(64);
            var d = SealWithKeys(keys.Take(32).ToArray(), keys.Skip(32).ToArray(), plain, associated);
            d["wrappedKey"] = B64(teamPublic.Encrypt(keys, RSAEncryptionPadding.OaepSHA256));
            Array.Clear(keys, 0, keys.Length);
            return d;
        }

        public static byte[] DecryptForTeam(RSA teamPrivate, object sealedData, byte[] associated)
        {
            byte[] keys;
            try { keys = teamPrivate.Decrypt(UnB64(Json.Str(sealedData, "wrappedKey")), RSAEncryptionPadding.OaepSHA256); }
            catch (CryptographicException) { throw new TeamException("チームの鍵で開けません（鍵の更新前の報告か、別のチームの報告です）"); }
            if (keys.Length != 64) throw new TeamException("データ形式が正しくありません");
            try { return OpenWithKeys(keys.Take(32).ToArray(), keys.Skip(32).ToArray(), sealedData, associated); }
            finally { Array.Clear(keys, 0, keys.Length); }
        }

        // ------------------------------------------------------------------ パスフレーズ

        public static Dictionary<string, object> ProtectWithPassphrase(byte[] secret, string passphrase, string purpose)
        {
            byte[] salt = Random(32);
            byte[] keys;
            using (var kdf = new Rfc2898DeriveBytes(passphrase, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256)) keys = kdf.GetBytes(64);
            var d = SealWithKeys(keys.Take(32).ToArray(), keys.Skip(32).ToArray(), secret, Encoding.UTF8.GetBytes(purpose));
            d["kdf"] = "pbkdf2-sha256";
            d["iterations"] = (long)Pbkdf2Iterations;
            d["salt"] = B64(salt);
            d["purpose"] = purpose;
            Array.Clear(keys, 0, keys.Length);
            return d;
        }

        public static byte[] UnprotectWithPassphrase(object sealedData, string passphrase, string expectedPurpose)
        {
            string purpose = Json.Str(sealedData, "purpose") ?? "";
            long iterations = Json.Long(sealedData, "iterations") ?? 0;
            byte[] salt = UnB64(Json.Str(sealedData, "salt"));
            // 反復回数・ソルト長・用途は固定値だけを受け入れる（不正なファイルで処理を重くされない）
            if (Json.Str(sealedData, "kdf") != "pbkdf2-sha256" || iterations != Pbkdf2Iterations || salt.Length != 32 || purpose != expectedPurpose)
                throw new TeamException("鍵ファイルの形式が正しくないか、このチーム・この世代の鍵ではありません");
            byte[] keys;
            using (var kdf = new Rfc2898DeriveBytes(passphrase, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256)) keys = kdf.GetBytes(64);
            try { return OpenWithKeys(keys.Take(32).ToArray(), keys.Skip(32).ToArray(), sealedData, Encoding.UTF8.GetBytes(purpose)); }
            catch (TeamException) { throw new TeamException("パスフレーズが違います"); }
            finally { Array.Clear(keys, 0, keys.Length); }
        }

        // ------------------------------------------------------------------ DPAPI（このPCの本人だけ）

        public static string DpapiProtect(string text, string purpose)
        {
            return B64(ProtectedData.Protect(Encoding.UTF8.GetBytes(text), Encoding.UTF8.GetBytes("AIUsageBar." + purpose), DataProtectionScope.CurrentUser));
        }

        public static string DpapiUnprotect(string b64, string purpose)
        {
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(UnB64(b64), Encoding.UTF8.GetBytes("AIUsageBar." + purpose), DataProtectionScope.CurrentUser)); }
            catch (CryptographicException) { throw new TeamException("このPCに保存した鍵を読み取れません（別のWindowsアカウントやPCからは使えません）"); }
        }
    }
}
