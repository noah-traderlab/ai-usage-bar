# -*- coding: utf-8 -*-
"""
リセット通知（既定オフ）。Windows 版（ResetWatch.cs）と同じ判定。
  Codex：取得したリセット時刻を覚えておき、その時刻を過ぎたらリセットとみなす。
  Claude：保存履歴にリセット時刻がないので、残りが大きく戻った（15ポイント以上増えて90%以上）ときにリセットとみなす。
macOS の通知（osascript）で知らせる。
"""
import math
import subprocess
from datetime import datetime, timezone

MODE_OFF, MODE_LOW_ONLY, MODE_ALWAYS = 0, 1, 2
MODE_NAMES = ["しない", "残りが少なかったときだけ", "毎回"]
MAX_LATE_MS = 60 * 60 * 1000
CLAUDE_JUMP = 15.0
CLAUDE_AFTER = 90.0


def _now_ms():
    return int(datetime.now(timezone.utc).timestamp() * 1000)


def should_notify(mode, remaining_before, threshold):
    if mode == MODE_LOW_ONLY:
        return remaining_before <= threshold
    if mode == MODE_ALWAYS:
        return remaining_before < 99.5   # まったく使っていない枠のリセットは知らせない
    return False


def _label(minutes):
    if minutes == 300:
        return "5時間"
    if minutes == 10080:
        return "週間"
    return (str(int(minutes)) + "分") if minutes else ""


class ResetWatcher:
    def __init__(self):
        self._codex = {}   # key -> {"label","resetAt","remaining"}
        self._done = set()

    def check_codex(self, snap, mode, threshold, now=None):
        """fetch_codex() の結果（windows:[{minutes,usedPercent,resetsAt}]）を渡す。リセットした枠の通知を返す。"""
        now = now or _now_ms()
        out = []
        if isinstance(snap, dict):
            for w in (snap.get("windows") or []):
                resets = w.get("resetsAt")
                if not isinstance(resets, (int, float)):
                    continue
                key = "codex:" + str(w.get("minutes"))
                t = self._codex.get(key)
                if t and t["resetAt"] != resets and t["resetAt"] <= now:
                    self._fire(key, t, now, mode, threshold, out)
                if resets > now:
                    up = w.get("usedPercent")
                    self._codex[key] = {"label": _label(w.get("minutes")),
                                        "resetAt": int(resets),
                                        "remaining": max(0.0, min(100.0, 100.0 - float(up))) if isinstance(up, (int, float)) else 0.0}
        for key, t in list(self._codex.items()):
            if t["resetAt"] <= now:
                self._fire(key, t, now, mode, threshold, out)
        return out

    def _fire(self, key, t, now, mode, threshold, out):
        self._codex.pop(key, None)
        token = key + "|" + str(t["resetAt"])
        if token in self._done:
            return
        self._done.add(token)
        if now - t["resetAt"] > MAX_LATE_MS or not should_notify(mode, t["remaining"], threshold):
            return
        out.append({"key": key,
                    "title": "Codex（" + t["label"] + "）の枠がリセットされました",
                    "text": "リセット前の残り " + str(math.floor(t["remaining"])) + "% → 使える状態に戻りました"})

    def check_claude(self, before, after, mode, threshold, now=None):
        """read_claude() の前回・今回（{fiveHourRemaining,weeklyRemaining,observedAt}）を渡す。"""
        now = now or _now_ms()
        out = []
        if not before or not after:
            return out
        if after.get("observedAt", 0) <= before.get("observedAt", 0):
            return out
        if now - after.get("observedAt", 0) > MAX_LATE_MS:
            return out
        self._claude(out, "claude:5h", "5時間", before.get("fiveHourRemaining"), after.get("fiveHourRemaining"), after.get("observedAt"), mode, threshold)
        self._claude(out, "claude:week", "週間", before.get("weeklyRemaining"), after.get("weeklyRemaining"), after.get("observedAt"), mode, threshold)
        return out

    def _claude(self, out, key, label, before, after, at, mode, threshold):
        if before is None or after is None:
            return
        if after - before < CLAUDE_JUMP or after < CLAUDE_AFTER:
            return
        token = key + "|" + str(at)
        if token in self._done:
            return
        self._done.add(token)
        if not should_notify(mode, before, threshold):
            return
        out.append({"key": key,
                    "title": "Claude（" + label + "）の枠がリセットされました",
                    "text": "残り " + str(math.floor(before)) + "% → " + str(math.floor(after)) + "% に戻りました"})


def notify(title, text):
    """macOS の通知を出す（osascript）。失敗しても無視。"""
    try:
        safe_t = (title or "").replace('"', "'")
        safe_x = (text or "").replace('"', "'")
        subprocess.run(["osascript", "-e",
                        'display notification "%s" with title "%s"' % (safe_x, safe_t)],
                       capture_output=True, timeout=5)
    except (OSError, subprocess.SubprocessError):
        pass
