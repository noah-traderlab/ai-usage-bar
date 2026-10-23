using System;
using System.Collections.Generic;
using System.Linq;

namespace AIUsageBar
{
    public class ResetNotice
    {
        public string Key;
        public string Title;
        public string Text;
    }

    /// <summary>
    /// 利用枠のリセットを見つけて通知内容を作る（スレッドをまたいで呼ばれても安全）。
    /// Codex：取得したリセット時刻を覚えておき、その時刻を過ぎたらリセットとみなす（アプリが再取得する前でも知らせる）。
    /// Claude：保存履歴にリセット時刻がないので、残りが大きく戻った（15ポイント以上増えて90%以上）ときにリセットとみなす。
    /// </summary>
    public class ResetWatcher
    {
        public const int ModeOff = 0, ModeLowOnly = 1, ModeAlways = 2;
        public static readonly string[] ModeNames = { "しない", "残りが少なかったときだけ", "毎回" };
        /// <summary>リセットからこれ以上たってから気づいた場合（スリープ明けなど）は知らせない</summary>
        public static readonly TimeSpan MaxLate = TimeSpan.FromMinutes(60);
        public const double ClaudeJump = 15, ClaudeAfterMin = 90;

        class Tracked { public string Label; public DateTime ResetAt; public double Remaining; }

        readonly object gate = new object();
        readonly Dictionary<string, Tracked> codex = new Dictionary<string, Tracked>();
        readonly HashSet<string> done = new HashSet<string>();

        public static bool ShouldNotify(int mode, double remainingBefore, int threshold)
        {
            if (mode == ModeLowOnly) return remainingBefore <= threshold;
            if (mode == ModeAlways) return remainingBefore < 99.5;   // まったく使っていない枠のリセットは知らせない
            return false;
        }

        /// <summary>Codex の取得結果（または1分ごとの見直し）を渡す。リセット時刻を過ぎた枠の通知を返す</summary>
        public List<ResetNotice> CheckCodex(CodexSnapshot snap, DateTime nowUtc, int mode, int threshold)
        {
            var list = new List<ResetNotice>();
            lock (gate)
            {
                if (snap != null && snap.Main != null)
                    foreach (var w in snap.Main.Windows)
                    {
                        if (w.ResetsAtUtc == null) continue;
                        string key = "codex:" + w.WindowMinutes;
                        Tracked t;
                        // 次の周期の値が届いたら、前の周期のリセットを先に処理する
                        if (codex.TryGetValue(key, out t) && t.ResetAt != w.ResetsAtUtc.Value && t.ResetAt <= nowUtc) Fire(key, t, nowUtc, mode, threshold, list);
                        if (w.ResetsAtUtc.Value > nowUtc)
                            codex[key] = new Tracked { Label = w.Label, ResetAt = w.ResetsAtUtc.Value, Remaining = w.RemainingPercent };
                    }
                foreach (var kv in codex.ToList())
                    if (kv.Value.ResetAt <= nowUtc) Fire(kv.Key, kv.Value, nowUtc, mode, threshold, list);
            }
            return list;
        }

        void Fire(string key, Tracked t, DateTime nowUtc, int mode, int threshold, List<ResetNotice> list)
        {
            codex.Remove(key);
            if (!done.Add(key + "|" + t.ResetAt.Ticks)) return;
            if (nowUtc - t.ResetAt > MaxLate || !ShouldNotify(mode, t.Remaining, threshold)) return;
            list.Add(new ResetNotice
            {
                Key = key,
                Title = "Codex（" + t.Label + "）の枠がリセットされました",
                Text = "リセット前の残り " + Math.Floor(t.Remaining) + "% → 使える状態に戻りました",
            });
        }

        /// <summary>Claude の新しい保存履歴を渡す（前回の値と比べる）</summary>
        public List<ResetNotice> CheckClaude(ClaudeSnapshot before, ClaudeSnapshot after, DateTime nowUtc, int mode, int threshold)
        {
            var list = new List<ResetNotice>();
            if (before == null || after == null || after.SampleAtUtc <= before.SampleAtUtc) return list;
            if (nowUtc - after.SampleAtUtc > MaxLate) return list;
            lock (gate)
            {
                Claude(list, "claude:5h", "5時間", before.FiveHourRemaining, after.FiveHourRemaining, after.SampleAtUtc, mode, threshold);
                Claude(list, "claude:week", "週間", before.WeeklyRemaining, after.WeeklyRemaining, after.SampleAtUtc, mode, threshold);
            }
            return list;
        }

        void Claude(List<ResetNotice> list, string key, string label, double? before, double? after, DateTime at, int mode, int threshold)
        {
            if (before == null || after == null) return;
            if (after.Value - before.Value < ClaudeJump || after.Value < ClaudeAfterMin) return;
            if (!done.Add(key + "|" + at.Ticks)) return;
            if (!ShouldNotify(mode, before.Value, threshold)) return;
            list.Add(new ResetNotice
            {
                Key = key,
                Title = "Claude（" + label + "）の枠がリセットされました",
                Text = "残り " + Math.Floor(before.Value) + "% → " + Math.Floor(after.Value) + "% に戻りました",
            });
        }
    }
}
