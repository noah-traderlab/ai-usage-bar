# -*- coding: utf-8 -*-
"""
Codex CLI の app-server から利用枠・日別トークンを取得し、Claude Desktop の保存履歴から残り枠を読む。
Windows 版（CodexClient.cs / ClaudeUsage.cs）と同じ JSON-RPC・同じ判定を Mac 向けに実装。
生成リクエストは送らない。
"""
import json
import os
import subprocess
import threading
import time
from datetime import datetime, timezone


# ------------------------------------------------------------------ Codex

def find_codex():
    """PATH → ~/.local/bin → npm → Codex.app 同梱CLI。"""
    cands = []
    for d in (os.environ.get("PATH") or "").split(os.pathsep):
        d = d.strip().strip('"')
        if d and os.path.isabs(d):
            cands.append(os.path.join(d, "codex"))
    home = os.path.expanduser("~")
    cands.append(os.path.join(home, ".local", "bin", "codex"))
    cands.append("/usr/local/bin/codex")
    cands.append("/opt/homebrew/bin/codex")
    for app in ("/Applications/Codex.app", os.path.join(home, "Applications", "Codex.app")):
        cands.append(os.path.join(app, "Contents", "Resources", "codex"))
        cands.append(os.path.join(app, "Contents", "MacOS", "codex"))
    for c in cands:
        try:
            if os.path.isfile(c) and os.access(c, os.X_OK):
                return c
        except OSError:
            pass
    return None


def fetch_codex(cli_path=None, timeout=25.0, work_dir=None):
    """{planType, observedAt, windows:[{minutes,usedPercent,resetsAt}], daily:[{date,tokens}], accountEmail} or None"""
    cli = cli_path or find_codex()
    if not cli:
        return None
    work = work_dir or os.path.join(os.path.expanduser("~"), ".cache", "aiusagebar", "codex-work")
    try:
        os.makedirs(work, exist_ok=True)
    except OSError:
        work = None
    env = dict(os.environ)
    if work:
        env["GIT_CEILING_DIRECTORIES"] = os.path.dirname(work.rstrip("/"))
    try:
        proc = subprocess.Popen(
            [cli, "-c", "analytics.enabled=false", "app-server"],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
            cwd=work or None, env=env, bufsize=1)
    except OSError:
        return None

    responses = {}
    lock = threading.Condition()
    done = {"exit": False}

    def reader():
        for raw in proc.stdout:
            try:
                msg = json.loads(raw.decode("utf-8", "replace"))
            except ValueError:
                continue
            mid = msg.get("id")
            if mid is not None and ("result" in msg or "error" in msg):
                with lock:
                    responses[mid] = msg
                    lock.notify_all()
        with lock:
            done["exit"] = True
            lock.notify_all()

    th = threading.Thread(target=reader, daemon=True)
    th.start()
    deadline = time.time() + timeout

    def send(obj):
        proc.stdin.write((json.dumps(obj) + "\n").encode("utf-8"))
        proc.stdin.flush()

    def wait(mid):
        with lock:
            while True:
                if mid in responses:
                    return responses[mid]
                if done["exit"]:
                    return None
                left = deadline - time.time()
                if left <= 0:
                    return None
                lock.wait(left)

    try:
        send({"jsonrpc": "2.0", "id": 1, "method": "initialize",
              "params": {"clientInfo": {"name": "ai_usage_bar", "title": "AI Usage Bar", "version": "0.1.0"}, "capabilities": None}})
        if wait(1) is None:
            return None
        send({"jsonrpc": "2.0", "method": "initialized"})
        send({"jsonrpc": "2.0", "id": 2, "method": "account/rateLimits/read", "params": {"excludeResetCreditDetails": True}})
        send({"jsonrpc": "2.0", "id": 3, "method": "account/usage/read", "params": {}})
        send({"jsonrpc": "2.0", "id": 4, "method": "account/read", "params": {"refreshToken": False}})
        limits = wait(2)
        if limits and "error" in limits:
            code = ((limits.get("error") or {}).get("code")) or 0
            if code in (-32602, -32600):
                send({"jsonrpc": "2.0", "id": 5, "method": "account/rateLimits/read"})
                limits = wait(5)
        if not limits or "error" in limits:
            return None
        snap = _parse_rate_limits((limits.get("result") or {}))
        acct = wait(4)
        if acct and "result" in acct:
            a = (acct.get("result") or {}).get("account") or {}
            snap["accountEmail"] = a.get("email")
            snap["planType"] = snap.get("planType") or a.get("planType")
        usage = wait(3)
        if usage and "result" in usage:
            snap["daily"] = _parse_daily(usage.get("result") or {})
        return snap
    finally:
        try:
            proc.stdin.close()
        except Exception:
            pass
        try:
            proc.wait(timeout=3)
        except Exception:
            try:
                proc.kill()
            except Exception:
                pass


def _parse_rate_limits(result):
    by_id = result.get("rateLimitsByLimitId") if isinstance(result, dict) else None
    main = None
    if isinstance(by_id, dict) and "codex" in by_id:
        main = by_id["codex"]
    if main is None:
        main = result.get("rateLimits")
    snap = {"planType": None, "observedAt": int(datetime.now(timezone.utc).timestamp() * 1000),
            "windows": [], "daily": None, "accountEmail": None}
    if not isinstance(main, dict):
        return snap
    snap["planType"] = main.get("planType")
    for key in ("primary", "secondary"):
        w = main.get(key)
        if not isinstance(w, dict) or w.get("usedPercent") is None:
            continue
        mins = w.get("windowDurationMins")
        resets = w.get("resetsAt")
        if isinstance(resets, (int, float)) and (resets < 0 or resets > 253402300799):
            resets = None
        snap["windows"].append({
            "minutes": mins,
            "usedPercent": float(w.get("usedPercent")),
            "resetsAt": int(resets * 1000) if isinstance(resets, (int, float)) else None,
        })
    snap["windows"].sort(key=lambda x: x["minutes"] if x["minutes"] is not None else 1 << 60)
    return snap


def _parse_daily(result):
    buckets = result.get("dailyUsageBuckets") if isinstance(result, dict) else None
    if not isinstance(buckets, list):
        return None
    out = []
    for b in buckets:
        date = b.get("startDate") if isinstance(b, dict) else None
        tokens = b.get("tokens") if isinstance(b, dict) else None
        if date and isinstance(tokens, (int, float)):
            out.append({"date": date[:10], "tokens": int(tokens)})
    return out


def codex_quota(snap, now_ms=None):
    """報告用の {planType, remaining, fiveHourRemaining, weeklyRemaining, resetsAt...}"""
    if not snap:
        return None
    now_ms = now_ms or int(datetime.now(timezone.utc).timestamp() * 1000)
    q = {"planType": snap.get("planType"), "observedAt": snap.get("observedAt")}
    rem = []
    for w in snap["windows"]:
        if w["resetsAt"] and w["resetsAt"] < now_ms:
            continue
        r = max(0.0, min(100.0, 100.0 - w["usedPercent"]))
        rem.append(r)
        key = "fiveHour" if w["minutes"] == 300 else "weekly" if w["minutes"] == 10080 else None
        if key:
            q[key + "Remaining"] = int(r)
            if w["resetsAt"]:
                q[key + "ResetsAt"] = w["resetsAt"]
    q["remaining"] = int(min(rem)) if rem else None
    return q


# ------------------------------------------------------------------ Claude

CLAUDE_MAX_AGE_MS = 16 * 60 * 1000


def claude_history_paths(home=None):
    home = home or os.path.expanduser("~")
    return [
        os.path.join(home, "Library", "Application Support", "Claude", "plan-usage-history.json"),
    ]


def read_claude(home=None, interval_minutes=1):
    """{fiveHourRemaining, weeklyRemaining, observedAt} or None（16分より古ければ None）"""
    home = home or os.path.expanduser("~")
    path = None
    best = -1
    for p in claude_history_paths(home):
        try:
            if os.path.isfile(p) and os.path.getmtime(p) > best:
                best = os.path.getmtime(p)
                path = p
        except OSError:
            pass
    if not path:
        return None
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as f:
            o = json.load(f)
    except (IOError, OSError, ValueError):
        return None
    samples = o.get("samples") if isinstance(o, dict) else None
    if not isinstance(samples, list):
        return None
    best_t = -1
    best_u = None
    for s in samples:
        if not isinstance(s, dict):
            continue
        t = s.get("t")
        u = s.get("u")
        if not isinstance(t, (int, float)) or not isinstance(u, dict):
            continue
        if u.get("fh") is None and u.get("sd") is None:
            continue
        if t > best_t:
            best_t = t
            best_u = u
    if best_u is None:
        return None
    now_ms = int(datetime.now(timezone.utc).timestamp() * 1000)
    if now_ms - int(best_t) > CLAUDE_MAX_AGE_MS + max(0, interval_minutes - 1) * 60000:
        return None

    def rem(v):
        return None if v is None else max(0.0, min(100.0, 100.0 - float(v)))

    return {
        "fiveHourRemaining": None if rem(best_u.get("fh")) is None else int(rem(best_u.get("fh"))),
        "weeklyRemaining": None if rem(best_u.get("sd")) is None else int(rem(best_u.get("sd"))),
        "observedAt": int(best_t),
    }


def claude_email(home=None):
    home = home or os.path.expanduser("~")
    try:
        with open(os.path.join(home, ".claude.json"), "r", encoding="utf-8", errors="replace") as f:
            o = json.load(f)
        a = o.get("oauthAccount") or {}
        return a.get("emailAddress")
    except (IOError, OSError, ValueError):
        return None
