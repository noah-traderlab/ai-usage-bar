using System;
using System.IO;
using System.Text;

namespace AIUsageBar
{
    /// <summary>
    /// JSONLを「行の先頭数百バイト」で振り分けながら高速に読む。
    /// 不要な行（ツール出力や画像など巨大な行）はメモリに載せずに読み飛ばす。
    /// 改行で終わっていない最後の行は書き込み途中とみなし、次回に回す。
    /// </summary>
    public unsafe class LineReader
    {
        public const int HeadSize = 768;

        /// <summary>行頭を見て、読む行なら種類（1以上）、読まないなら0を返す</summary>
        public delegate int Classifier(byte[] head, int headLen);
        /// <summary>種類ごとの最大バイト数</summary>
        public Func<int, int> LimitForKind = k => 8 * 1024 * 1024;
        /// <summary>選ばれた行。truncated=true は上限を超えて途中までしか持っていない</summary>
        public delegate void LineHandler(long offset, byte[] data, int length, bool truncated, int kind);

        public long BytesRead;
        public int DiscardedLines;

        public long Scan(string path, long startOffset, Classifier classify, LineHandler handler, Action<long> progress)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan))
            {
                if (startOffset > fs.Length) startOffset = 0;
                fs.Seek(startOffset, SeekOrigin.Begin);

                byte[] buf = new byte[4 * 1024 * 1024];
                byte[] head = new byte[HeadSize];
                int headLen = 0;
                bool decided = false;
                int limit = 0;           // 0 = 読み飛ばし
                int kind = 0;
                byte[] line = new byte[64 * 1024];
                int lineLen = 0;
                bool truncated = false;
                long lineStart = startOffset;
                long committed = startOffset;
                long pos = startOffset;
                long sinceProgress = 0;

                while (true)
                {
                    int n = fs.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    BytesRead += n;
                    sinceProgress += n;
                    if (progress != null && sinceProgress >= 16 * 1024 * 1024) { progress(sinceProgress); sinceProgress = 0; }

                    int i = 0;
                    while (i < n)
                    {
                        int nl = IndexOfNewline(buf, i, n - i);
                        int segEnd = nl < 0 ? n : nl;
                        int segLen = segEnd - i;

                        if (!decided)
                        {
                            int take = Math.Min(segLen, HeadSize - headLen);
                            Buffer.BlockCopy(buf, i, head, headLen, take);
                            headLen += take;
                            if (headLen >= HeadSize || nl >= 0)
                            {
                                decided = true;
                                kind = classify(head, headLen);
                                limit = kind > 0 ? LimitForKind(kind) : 0;
                                if (limit > 0)
                                {
                                    lineLen = 0; truncated = false;
                                    Append(ref line, ref lineLen, head, 0, headLen, limit, ref truncated);
                                    Append(ref line, ref lineLen, buf, i + take, segLen - take, limit, ref truncated);
                                }
                            }
                        }
                        else if (limit > 0)
                        {
                            Append(ref line, ref lineLen, buf, i, segLen, limit, ref truncated);
                        }

                        if (nl < 0) { pos += n - i; break; }

                        // 1行完了
                        pos += segLen + 1;
                        if (limit > 0)
                        {
                            int len = lineLen;
                            if (len > 0 && line[len - 1] == (byte)'\r') len--;
                            if (truncated) DiscardedLines++;
                            handler(lineStart, line, len, truncated, kind);
                        }
                        committed = pos;
                        lineStart = pos;
                        headLen = 0; decided = false; limit = 0; lineLen = 0; truncated = false;
                        i = nl + 1;
                    }
                }
                if (progress != null && sinceProgress > 0) progress(sinceProgress);
                return committed;
            }
        }

        static void Append(ref byte[] line, ref int lineLen, byte[] src, int offset, int count, int limit, ref bool truncated)
        {
            if (count <= 0) return;
            int room = limit - lineLen;
            if (room <= 0) { truncated = true; return; }
            if (count > room) { count = room; truncated = true; }
            if (lineLen + count > line.Length)
            {
                int size = line.Length;
                while (size < lineLen + count) size *= 2;
                Array.Resize(ref line, size);
            }
            Buffer.BlockCopy(src, offset, line, lineLen, count);
            lineLen += count;
        }

        static int IndexOfNewline(byte[] buf, int start, int count)
        {
            fixed (byte* p = buf)
            {
                byte* s = p + start;
                byte* end = s + count;
                // 8バイトずつ判定（改行を含むワードだけ詳しく見る）
                while (s + 8 <= end)
                {
                    ulong v = *(ulong*)s;
                    ulong x = v ^ 0x0A0A0A0A0A0A0A0AUL;
                    ulong t = (x - 0x0101010101010101UL) & ~x & 0x8080808080808080UL;
                    if (t != 0) break;
                    s += 8;
                }
                while (s < end)
                {
                    if (*s == (byte)'\n') return (int)(s - p);
                    s++;
                }
                return -1;
            }
        }

        public static bool Contains(byte[] data, int len, byte[] pattern)
        {
            if (pattern.Length == 0) return true;
            int last = len - pattern.Length;
            byte first = pattern[0];
            for (int i = 0; i <= last; i++)
            {
                if (data[i] != first) continue;
                int j = 1;
                while (j < pattern.Length && data[i + j] == pattern[j]) j++;
                if (j == pattern.Length) return true;
            }
            return false;
        }

        public static byte[] B(string s) { return Encoding.UTF8.GetBytes(s); }

        /// <summary>指定位置の1行を読む（表示用・オンデマンド）</summary>
        public static string ReadLineAt(string path, long offset, int maxBytes)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (offset < 0 || offset >= fs.Length) return null;
                    fs.Seek(offset, SeekOrigin.Begin);
                    var ms = new MemoryStream();
                    byte[] buf = new byte[65536];
                    while (ms.Length < maxBytes)
                    {
                        int n = fs.Read(buf, 0, buf.Length);
                        if (n <= 0) break;
                        int nl = Array.IndexOf(buf, (byte)'\n', 0, n);
                        if (nl >= 0) { ms.Write(buf, 0, nl); return Encoding.UTF8.GetString(ms.ToArray()).TrimEnd('\r'); }
                        ms.Write(buf, 0, n);
                    }
                    if (ms.Length >= maxBytes) return null; // 大きすぎる行は表示しない
                    return Encoding.UTF8.GetString(ms.ToArray());
                }
            }
            catch { return null; }
        }
    }
}
