# -*- coding: utf-8 -*-
"""
フレンドの残り枠（任意・既定オフ）。
チーム機能とは別に、共有フォルダ（OneDrive など）を通じて残り枠だけを見せ合う。
Windows 版（Friends.cs）と同じファイル形式・同じフォルダ名なので、Windows と Mac が互いに読める。
含めるのは表示名・残り枠(%)・リセット時刻・更新時刻だけ（メールアドレス・トークン数・作業内容は含めない）。
アプリ自身は通信しない（共有フォルダの同期は OneDrive などが行う）。
"""
import os
import re
import secrets
import unicodedata
from datetime import datetime, timezone

from . import jsonc

FORMAT = "aiub-friend-1"
FOLDER_NAME = "AIUsageBar-Friends"
STALE_MINUTES = 30
HIDE_AFTER_DAYS = 7
MAX_FRIENDS = 100
MAX_FILE_BYTES = 16 * 1024
MAX_WINDOWS = 6
MAX_NAME = 40
INVITE_PREFIX = "AIUB-FRIEND-1:"


def now_ms():
    return int(datetime.now(timezone.utc).timestamp() * 1000)


def valid_id(fid):
    return isinstance(fid, str) and len(fid) == 32 and all(c in "0123456789abcdefABCDEF" for c in fid)


def new_id():
    return secrets.token_hex(16)


def _valid_ms(v):
    return isinstance(v, (int, float)) and 0 <= v <= 253402300799000


def safe_line(s, limit):
    """他人が書いた文字列を安全に：制御・双方向・ゼロ幅文字を除き、1行にして切り詰める。"""
    if not isinstance(s, str):
        return None
    out = []
    for ch in s:
        cat = unicodedata.category(ch)
        if ch in "\r\n\t":
            out.append(" ")
        elif cat[0] == "C" or ch in "​‌‍⁠﻿" or "‪" <= ch <= "‮" or "⁦" <= ch <= "⁩":
            continue
        else:
            out.append(ch)
    text = "".join(out).strip()
    if len(text) > limit:
        text = text[:limit]
    return text or None


def _pct(v):
    if not isinstance(v, (int, float)):
        return None
    if v != v or v in (float("inf"), float("-inf")):
        return None
    return max(0.0, min(100.0, float(v)))


# ------------------------------------------------------------------ 設定（friends.json）

def config_path(data_dir):
    return os.path.join(data_dir, "friends.json")


def load_config(data_dir):
    c = {"id": None, "folder": None, "displayName": None, "enabled": False}
    try:
        with open(config_path(data_dir), "r", encoding="utf-8", errors="replace") as f:
            o = jsonc.loads(f.read())
        if isinstance(o, dict):
            c["id"] = o.get("id")
            c["folder"] = safe_line(o.get("folder"), 1000)
            c["displayName"] = safe_line(o.get("displayName"), MAX_NAME)
            c["enabled"] = bool(o.get("enabled"))
    except (IOError, OSError, ValueError):
        pass
    if not valid_id(c["id"]):
        c["id"] = new_id()
    return c


def save_config(data_dir, c):
    os.makedirs(data_dir, exist_ok=True)
    try:
        os.chmod(data_dir, 0o700)
    except OSError:
        pass
    d = {"id": c["id"], "folder": c.get("folder"), "displayName": c.get("displayName"), "enabled": bool(c.get("enabled"))}
    tmp = config_path(data_dir) + ".tmp-" + secrets.token_hex(4)
    with open(tmp, "w", encoding="utf-8") as f:
        f.write(jsonc.dumps(d, indent=True))
    os.replace(tmp, config_path(data_dir))


def friends_dir(c):
    folder = c.get("folder")
    return os.path.join(folder, FOLDER_NAME) if folder else None


def is_configured(c):
    return bool(c.get("enabled")) and bool(c.get("folder"))


# ------------------------------------------------------------------ 招待コード

def encode_invite(folder):
    import base64
    raw = jsonc.dumps({"folder": folder}).encode("utf-8")
    return INVITE_PREFIX + base64.urlsafe_b64encode(raw).decode("ascii").rstrip("=")


def decode_invite(text):
    if not isinstance(text, str) or not text or len(text) > 20000:
        return None
    m = re.search(re.escape(INVITE_PREFIX) + r"([A-Za-z0-9_-]{8,4000})", text)
    if not m:
        return None
    try:
        import base64
        b = m.group(1)
        b += "=" * ((4 - len(b) % 4) % 4)
        o = jsonc.loads(base64.urlsafe_b64decode(b.encode("ascii")).decode("utf-8"))
        return safe_line(o.get("folder"), 1000) or None
    except (ValueError, TypeError):
        return None


# ------------------------------------------------------------------ 自分の残り枠を組み立て

def _label_for(minutes):
    if minutes == 300:
        return "5時間"
    if minutes == 10080:
        return "週間"
    return (str(int(minutes)) + "分") if minutes else ""


def build_mine(c, claude, codex_snap, now=None):
    """このPCの今の残り枠。リセット済み・古い枠は載せない（相手に誤った数字を見せないため）。"""
    now = now or now_ms()
    name = safe_line(c.get("displayName"), MAX_NAME) or "名前なし"
    s = {"format": FORMAT, "id": c["id"], "name": name, "updatedAt": now,
         "claude": {"fiveHour": None, "weekly": None}, "codex": []}
    if isinstance(claude, dict):
        s["claude"]["fiveHour"] = claude.get("fiveHourRemaining")
        s["claude"]["weekly"] = claude.get("weeklyRemaining")
    if isinstance(codex_snap, dict):
        for w in (codex_snap.get("windows") or [])[:MAX_WINDOWS]:
            resets = w.get("resetsAt")
            if isinstance(resets, (int, float)) and resets < now:
                continue  # すでにリセット済みの枠は載せない
            up = w.get("usedPercent")
            if not isinstance(up, (int, float)):
                continue
            s["codex"].append({
                "label": _label_for(w.get("minutes")),
                "remaining": int(max(0.0, min(100.0, 100.0 - float(up)))),
                "resetsAt": int(resets) if isinstance(resets, (int, float)) else None,
            })
    return s


def parse_status(o):
    """他人が書いたファイルなので、形式・範囲・長さをすべて確かめる。不正なら None。"""
    if not isinstance(o, dict) or o.get("format") != FORMAT:
        return None
    fid = o.get("id")
    if not valid_id(fid):
        return None
    up = o.get("updatedAt")
    if not _valid_ms(up):
        return None
    s = {"id": fid, "name": safe_line(o.get("name"), MAX_NAME) or "名前なし",
         "updatedAt": int(up), "claudeFiveHour": None, "claudeWeekly": None, "codex": []}
    cl = o.get("claude")
    if isinstance(cl, dict):
        s["claudeFiveHour"] = _pct(cl.get("fiveHour"))
        s["claudeWeekly"] = _pct(cl.get("weekly"))
    cx = o.get("codex")
    if isinstance(cx, list):
        for w in cx[:MAX_WINDOWS]:
            if not isinstance(w, dict):
                continue
            rem = _pct(w.get("remaining"))
            if rem is None:
                continue
            r = w.get("resetsAt")
            s["codex"].append({
                "label": safe_line(w.get("label"), 20) or "",
                "remaining": rem,
                "resetsAt": int(r) if _valid_ms(r) else None,
            })
    return s


def min_remaining(s):
    vals = []
    if s.get("claudeFiveHour") is not None:
        vals.append(s["claudeFiveHour"])
    if s.get("claudeWeekly") is not None:
        vals.append(s["claudeWeekly"])
    vals += [w["remaining"] for w in s.get("codex", [])]
    return min(vals) if vals else None


def is_stale(s, now=None):
    now = now or now_ms()
    return now - s["updatedAt"] > STALE_MINUTES * 60 * 1000


# ------------------------------------------------------------------ 読み書き

def file_for(c):
    return os.path.join(friends_dir(c), c["id"] + ".json")


def publish(c, mine):
    d = friends_dir(c)
    if not d or not os.path.isdir(c.get("folder") or ""):
        raise IOError("共有フォルダが見つかりません：" + str(c.get("folder")))
    os.makedirs(d, exist_ok=True)
    path = file_for(c)
    tmp = path + ".tmp-" + secrets.token_hex(4)
    with open(tmp, "w", encoding="utf-8") as f:
        f.write(jsonc.dumps(mine, indent=True))
    os.replace(tmp, path)


def withdraw(c):
    if not valid_id(c.get("id")) or not friends_dir(c):
        return
    path = file_for(c)
    try:
        if os.path.isfile(path):
            os.remove(path)
    except OSError:
        pass


def read_others(c, now=None):
    """共有フォルダのフレンドを読む（自分は除く）。壊れた・大きすぎる・古すぎるファイルは無視する。"""
    now = now or now_ms()
    out = []
    d = friends_dir(c)
    if not d or not os.path.isdir(d):
        return out
    seen = set()
    try:
        names = sorted(os.listdir(d))
    except OSError:
        return out
    for fn in names[:MAX_FRIENDS * 3]:
        if not fn.endswith(".json"):
            continue
        stem = fn[:-5]
        if not valid_id(stem) or stem == c["id"]:
            continue
        path = os.path.join(d, fn)
        try:
            st = os.lstat(path)
            import stat as _stat
            if _stat.S_ISLNK(st.st_mode) or st.st_size > MAX_FILE_BYTES:
                continue
            with open(path, "r", encoding="utf-8", errors="replace") as f:
                s = parse_status(jsonc.loads(f.read()))
        except (IOError, OSError, ValueError):
            continue
        if not s or s["id"] != stem or s["id"] in seen:
            continue
        seen.add(s["id"])
        if now - s["updatedAt"] > HIDE_AFTER_DAYS * 86400 * 1000:
            continue
        if s["updatedAt"] > now + 10 * 60 * 1000:  # 未来の時刻は信用しない
            continue
        out.append(s)
        if len(out) >= MAX_FRIENDS:
            break
    out.sort(key=lambda f: (is_stale(f, now), f["name"]))
    return out
