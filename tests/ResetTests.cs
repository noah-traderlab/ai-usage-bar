using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AIUsageBar.Tests
{
    public static partial class TestMain
    {
        static CodexSnapshot CodexWindows(DateTime observed, params Tuple<int, string, double, DateTime>[] ws)
        {
            var s = new CodexSnapshot { ObservedAtUtc = observed, Main = new QuotaGroup() };
            foreach (var w in ws)
                s.Main.Windows.Add(new QuotaWindow { WindowMinutes = w.Item1, Label = w.Item2, UsedPercent = 100 - w.Item3, ResetsAtUtc = w.Item4 });
            return s;
        }

        static void ResetUnit()
        {
            var t0 = DateTime.UtcNow;
            const int low = ResetWatcher.ModeLowOnly, always = ResetWatcher.ModeAlways, off = ResetWatcher.ModeOff;

            Console.WriteLine("== リセット通知：Codex");
            {
                var w = new ResetWatcher();
                var reset = t0.AddMinutes(30);
                Check(w.CheckCodex(CodexWindows(t0, Tuple.Create(300, "5時間", 8.0, reset)), t0, low, 20).Count == 0, "リセット前は知らせない");
                var n = w.CheckCodex(null, reset.AddMinutes(1), low, 20);
                Check(n.Count == 1 && n[0].Title.Contains("Codex（5時間）") && n[0].Text.Contains("8%"), "リセット時刻を過ぎたら再取得を待たずに知らせる");
                Check(w.CheckCodex(null, reset.AddMinutes(2), low, 20).Count == 0, "同じリセットは1回だけ");

                w = new ResetWatcher();
                w.CheckCodex(CodexWindows(t0, Tuple.Create(300, "5時間", 60.0, reset)), t0, low, 20);
                Check(w.CheckCodex(null, reset.AddMinutes(1), low, 20).Count == 0, "「少ないときだけ」：残り60%なら知らせない");
                w = new ResetWatcher();
                w.CheckCodex(CodexWindows(t0, Tuple.Create(300, "5時間", 60.0, reset)), t0, always, 20);
                Check(w.CheckCodex(null, reset.AddMinutes(1), always, 20).Count == 1, "「毎回」：残り60%でも知らせる");
                w = new ResetWatcher();
                w.CheckCodex(CodexWindows(t0, Tuple.Create(300, "5時間", 100.0, reset)), t0, always, 20);
                Check(w.CheckCodex(null, reset.AddMinutes(1), always, 20).Count == 0, "使っていない枠（100%）は知らせない");
                w = new ResetWatcher();
                w.CheckCodex(CodexWindows(t0, Tuple.Create(300, "5時間", 5.0, reset)), t0, off, 20);
                Check(w.CheckCodex(null, reset.AddMinutes(1), off, 20).Count == 0, "「しない」なら知らせない");

                // 再取得で次の周期の値が先に届いた場合も、前の周期のリセットを知らせる
                w = new ResetWatcher();
                w.CheckCodex(CodexWindows(t0, Tuple.Create(300, "5時間", 5.0, reset)), t0, low, 20);
                var next = CodexWindows(reset.AddMinutes(1), Tuple.Create(300, "5時間", 100.0, reset.AddHours(5)));
                n = w.CheckCodex(next, reset.AddMinutes(1), low, 20);
                Check(n.Count == 1 && n[0].Text.Contains("5%"), "次の周期の値が先に届いても前のリセットを知らせる");
                Check(w.CheckCodex(null, reset.AddMinutes(3), low, 20).Count == 0, "次の周期はまだ知らせない");

                // スリープ明けなどで大きく遅れて気づいた場合
                w = new ResetWatcher();
                w.CheckCodex(CodexWindows(t0, Tuple.Create(10080, "週間", 3.0, reset)), t0, low, 20);
                Check(w.CheckCodex(null, reset.AddMinutes(90), low, 20).Count == 0, "60分以上たってから気づいた分は知らせない");

                // 起動時：前回の取得結果で、すでに過ぎたリセットは知らせない
                w = new ResetWatcher();
                w.CheckCodex(CodexWindows(t0.AddHours(-6), Tuple.Create(300, "5時間", 2.0, t0.AddMinutes(-5))), t0, ResetWatcher.ModeOff, 0);
                Check(w.CheckCodex(null, t0.AddMinutes(1), low, 20).Count == 0, "起動前に過ぎたリセットは知らせない");

                // 5時間と週間の両方
                w = new ResetWatcher();
                w.CheckCodex(CodexWindows(t0, Tuple.Create(300, "5時間", 10.0, reset), Tuple.Create(10080, "週間", 15.0, reset.AddDays(2))), t0, low, 20);
                Check(w.CheckCodex(null, reset.AddMinutes(1), low, 20).Count == 1, "5時間だけリセット（週間はまだ）");
            }

            Console.WriteLine("== リセット通知：Claude");
            {
                var w = new ResetWatcher();
                var a = new ClaudeSnapshot { SampleAtUtc = t0.AddMinutes(-15), FiveHourUsed = 92, WeeklyUsed = 70 };
                var b = new ClaudeSnapshot { SampleAtUtc = t0, FiveHourUsed = 3, WeeklyUsed = 71 };
                var n = w.CheckClaude(a, b, t0, low, 20);
                Check(n.Count == 1 && n[0].Title.Contains("Claude（5時間）") && n[0].Text.Contains("8%") && n[0].Text.Contains("97%"), "残りが大きく戻ったらリセットとみなす");
                Check(w.CheckClaude(a, b, t0, low, 20).Count == 0, "同じ記録で2回知らせない");
                var c = new ClaudeSnapshot { SampleAtUtc = t0, FiveHourUsed = 40, WeeklyUsed = 70 };
                Check(new ResetWatcher().CheckClaude(a, c, t0, always, 20).Count == 0, "戻りが小さい（90%未満）ときはリセット扱いしない");
                Check(new ResetWatcher().CheckClaude(null, b, t0, always, 20).Count == 0, "起動直後（前回なし）は知らせない");
                Check(new ResetWatcher().CheckClaude(b, a, t0, always, 20).Count == 0, "古い記録では知らせない");
                var old = new ClaudeSnapshot { SampleAtUtc = t0.AddHours(-3), FiveHourUsed = 3, WeeklyUsed = 71 };
                var older = new ClaudeSnapshot { SampleAtUtc = t0.AddHours(-4), FiveHourUsed = 92, WeeklyUsed = 70 };
                Check(new ResetWatcher().CheckClaude(older, old, t0, always, 20).Count == 0, "60分以上前の記録では知らせない");
                var d = new ClaudeSnapshot { SampleAtUtc = t0.AddMinutes(-15), FiveHourUsed = 50, WeeklyUsed = 70 };
                Check(new ResetWatcher().CheckClaude(d, b, t0, low, 20).Count == 0 && new ResetWatcher().CheckClaude(d, b, t0, always, 20).Count == 1, "「少ないときだけ」と「毎回」の違い");
            }

            Console.WriteLine("== リセット通知：設定");
            {
                string root = Tmp();
                var st = new Settings();
                Check(st.ResetNotify == ResetWatcher.ModeOff, "初期状態は「しない」");
                Directory.CreateDirectory(Path.Combine(root, "d"));
                st.ResetNotify = ResetWatcher.ModeAlways;
                st.Save(Path.Combine(root, "d"));
                Check(Settings.Load(Path.Combine(root, "d")).ResetNotify == ResetWatcher.ModeAlways, "保存と読み込み");
                File.WriteAllText(Path.Combine(root, "d", "settings.json"), "{\"resetNotify\":9}");
                Check(Settings.Load(Path.Combine(root, "d")).ResetNotify == ResetWatcher.ModeOff, "範囲外の値は既定値");

                // UsageStore 経由：1分ごとの見直しで Info が出る
                var store = new UsageStore(FakePaths(Tmp()), new Settings { ResetNotify = ResetWatcher.ModeAlways }, null);
                var got = new List<string>();
                store.Info += (title, text) => got.Add(title);
                store.Resets.CheckCodex(CodexWindows(t0.AddMinutes(-10), Tuple.Create(300, "5時間", 40.0, DateTime.UtcNow.AddSeconds(-5))), DateTime.UtcNow.AddMinutes(-1), ResetWatcher.ModeAlways, 20);
                store.CheckResets();
                Check(got.Count == 1 && got[0].Contains("Codex（5時間）"), "1分ごとの見直しで通知が出る");
            }
        }
    }
}
