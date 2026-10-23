using System;
using System.IO;
using System.Linq;

namespace AIUsageBar
{
    public class ClaudeSnapshot
    {
        public DateTime SampleAtUtc;
        public double? FiveHourUsed;
        public double? WeeklyUsed;
        public string SourcePath;

        public double? FiveHourRemaining { get { return Remaining(FiveHourUsed); } }
        public double? WeeklyRemaining { get { return Remaining(WeeklyUsed); } }

        static double? Remaining(double? used)
        {
            if (used == null) return null;
            return Math.Max(0, Math.Min(100, 100 - used.Value));
        }

        /// <summary>5時間・週間のうち少ない方</summary>
        public double? MinRemaining
        {
            get
            {
                var a = FiveHourRemaining; var b = WeeklyRemaining;
                if (a == null) return b;
                if (b == null) return a;
                return Math.Min(a.Value, b.Value);
            }
        }

        /// <summary>古い情報か。確認間隔を長くした場合は、その分だけ許容時間を延ばす</summary>
        public bool IsStale(DateTime nowUtc, int checkIntervalMinutes = 1)
        {
            return nowUtc - SampleAtUtc > ClaudeUsage.MaxAge + TimeSpan.FromMinutes(Math.Max(0, checkIntervalMinutes - 1));
        }
    }

    /// <summary>
    /// Claude Desktop が保存する plan-usage-history.json を読む。
    /// fh = 5時間枠の使用率、sd = 7日間枠の使用率。
    /// </summary>
    public static class ClaudeUsage
    {
        // Claude Desktop は約15分ごとに記録する。書き込みの揺れを1分だけ許容する
        public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(16);

        public const string MsgNoFile = "Claudeアプリの保存履歴を読み取れません。アプリで使用状況を開いてください";
        public const string MsgNoSamples = "保存された利用履歴がまだありません";
        public const string MsgFormat = "対応していないデータ形式です";

        public static string FindHistory(AppPaths paths)
        {
            return paths.ClaudeHistoryCandidates
                .Where(p => { try { return File.Exists(p); } catch { return false; } })
                .OrderByDescending(p => File.GetLastWriteTimeUtc(p))
                .FirstOrDefault();
        }

        public static ClaudeSnapshot Read(AppPaths paths, out string error)
        {
            error = null;
            string path = FindHistory(paths);
            if (path == null) { error = MsgNoFile; return null; }
            object o;
            try { o = Json.Parse(FileUtil.ReadShared(path)); }
            catch (IOException) { error = MsgNoFile; return null; }
            catch (UnauthorizedAccessException) { error = MsgNoFile; return null; }
            catch (Exception) { error = MsgFormat; return null; }
            try { return Parse(o, path, out error); }
            catch (Exception) { error = MsgFormat; return null; }
        }

        public static ClaudeSnapshot Parse(object o, string path, out string error)
        {
            error = null;
            var samples = Json.Arr(o, "samples");
            if (samples == null) { error = MsgFormat; return null; }
            object best = null;
            long bestT = long.MinValue;
            foreach (var s in samples)
            {
                long? t = Json.Long(s, "t");
                var u = Json.Obj(s, "u");
                if (t == null || u == null || !Fmt.ValidUnixMs(t.Value)) continue;
                if (Json.Num(u, "fh") == null && Json.Num(u, "sd") == null) continue;
                if (t.Value > bestT) { bestT = t.Value; best = s; }
            }
            if (best == null) { error = MsgNoSamples; return null; }
            var bu = Json.Obj(best, "u");
            return new ClaudeSnapshot
            {
                SampleAtUtc = Fmt.FromUnixMs(bestT),
                FiveHourUsed = Json.Num(bu, "fh"),
                WeeklyUsed = Json.Num(bu, "sd"),
                SourcePath = path
            };
        }
    }
}
