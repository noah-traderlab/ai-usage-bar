#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
AI Usage Bar チーム参加ツール（Mac 版）

Mac の Claude Code・Codex の記録と残り枠を集計し、Windows 版の AI Usage Bar と同じ形式
（チーム公開鍵で暗号化・このMacの鍵で署名）で共有フォルダに報告する。

  python3 aiub_team.py            メニュー（参加・報告・状態・退出）
  python3 aiub_team.py join       チームに参加する
  python3 aiub_team.py upload     報告する（自動実行から呼ばれる）
  python3 aiub_team.py status     状態を表示する
  python3 aiub_team.py leave      チームから抜ける（報告を削除し、自動実行を止める）

Python 3.8 以降の標準ライブラリだけを使う（追加のインストールは不要）。
インターネットには接続しない（Codex の残り枠は、インストール済みの Codex CLI に問い合わせる）。
"""

import base64
import datetime as _dt
import getpass
import hashlib
import hmac
import json
import os
import platform
import re
import secrets
import shutil
import socket
import subprocess
import sys
import tempfile
import threading
import time
import uuid

VERSION = "0.1.0-mac"
APP_NAME = "AI Usage Bar"
FOLDER_NAME = "AIUsageBar-Team"
REPORT_FORMAT = "aiub-report-2"
TEAM_FORMAT = "aiub-team-2"
INVITE_PREFIX = "AIUB-INVITE-1:"
TEAM_KEY_BITS = 3072
MEMBER_KEY_BITS = 2048
RETENTION_DAYS = 14
HISTORY_DAYS = 400
SESSION_DAYS = 14
MAX_PROMPT_CHARS = 8000
MAX_PROMPT_REFS = 300
MAX_REPORT_BYTES = 8 * 1024 * 1024
MAX_TEAM_FILE_BYTES = 2 * 1024 * 1024
CACHE_VERSION = 1
AGENT_LABEL = "com.aiusagebar.team"
SCOPES = ["usage", "summary", "prompts"]


class TeamError(Exception):
    pass


# =====================================================================================
# 場所
# =====================================================================================

def home_dir():
    return os.environ.get("AIUB_HOME") or os.path.expanduser("~")


def data_dir():
    d = os.environ.get("AIUB_DATA_DIR")
    if not d:
        d = os.path.join(home_dir(), "Library", "Application Support", "AIUsageBar-Team")
    return d


def claude_projects():
    return os.path.join(home_dir(), ".claude", "projects")


def codex_home():
    return os.environ.get("CODEX_HOME") or os.path.join(home_dir(), ".codex")


def claude_history_candidates():
    explicit = os.environ.get("AIUB_CLAUDE_HISTORY")
    if explicit:
        return [explicit]
    h = home_dir()
    return [os.path.join(h, "Library", "Application Support", "Claude", "plan-usage-history.json")]


def ensure_private_dir(path):
    os.makedirs(path, exist_ok=True)
    try:
        os.chmod(path, 0o700)
    except OSError:
        pass


def write_private(path, data):
    """一時ファイルに書いてから置き換える（本人だけが読める権限）"""
    if isinstance(data, str):
        data = data.encode("utf-8")
    d = os.path.dirname(path)
    fd, tmp = tempfile.mkstemp(prefix=".tmp-", dir=d)
    try:
        with os.fdopen(fd, "wb") as f:
            f.write(data)
        try:
            os.chmod(tmp, 0o600)
        except OSError:
            pass
        os.replace(tmp, path)
    except BaseException:
        try:
            os.remove(tmp)
        except OSError:
            pass
        raise


def write_shared(path, data):
    """共有フォルダへの書き込み（一時ファイル→置き換え）"""
    if isinstance(data, str):
        data = data.encode("utf-8")
    d = os.path.dirname(path)
    tmp = os.path.join(d, "." + os.path.basename(path) + "." + uuid.uuid4().hex[:8] + ".tmp")
    with open(tmp, "wb") as f:
        f.write(data)
    os.replace(tmp, path)


def now_ms():
    override = os.environ.get("AIUB_NOW_MS")
    if override:
        return int(override)
    return int(time.time() * 1000)


def local_date(ms):
    return _dt.datetime.fromtimestamp(ms / 1000.0).strftime("%Y-%m-%d")


_ISO = re.compile(r"^(\d{4})-(\d{2})-(\d{2})[T ](\d{2}):(\d{2})(?::(\d{2})(?:\.(\d+))?)?\s*(Z|[+-]\d{2}:?\d{2})?$")


def parse_iso_ms(s):
    if not isinstance(s, str):
        return None
    m = _ISO.match(s.strip())
    if not m:
        return None
    try:
        y, mo, d, hh, mi = (int(m.group(i)) for i in range(1, 6))
        ss = int(m.group(6) or 0)
        frac = m.group(7) or ""
        micro = int((frac + "000000")[:6]) if frac else 0
        dt = _dt.datetime(y, mo, d, hh, mi, ss, micro, tzinfo=_dt.timezone.utc)
        tz = m.group(8)
        if tz and tz != "Z":
            sign = 1 if tz[0] == "+" else -1
            digits = tz[1:].replace(":", "")
            dt = dt - sign * _dt.timedelta(hours=int(digits[:2]), minutes=int(digits[2:]))
        return int((dt - _dt.datetime(1970, 1, 1, tzinfo=_dt.timezone.utc)).total_seconds() * 1000)
    except (ValueError, OverflowError):
        return None


# =====================================================================================
# JSON（Windows 版と同じ読み方・並べ方）
# =====================================================================================

def jstr(o, key):
    if isinstance(o, dict):
        v = o.get(key)
        return v if isinstance(v, str) else None
    return None


def jobj(o, key):
    if isinstance(o, dict):
        v = o.get(key)
        return v if isinstance(v, dict) else None
    return None


def jarr(o, key):
    if isinstance(o, dict):
        v = o.get(key)
        return v if isinstance(v, list) else None
    return None


def jlong(o, key):
    if not isinstance(o, dict):
        return None
    v = o.get(key)
    if isinstance(v, bool) or v is None:
        return None
    if isinstance(v, int):
        return v if -(2 ** 63) <= v < 2 ** 63 else None
    if isinstance(v, float):
        if v != v or v in (float("inf"), float("-inf")) or not (-9.2e18 < v < 9.2e18):
            return None
        return int(v)
    if isinstance(v, str) and re.match(r"^[+-]?\d+$", v.strip()):
        try:
            return int(v.strip())
        except ValueError:
            return None
    return None


def jnum(o, key):
    if not isinstance(o, dict):
        return None
    v = o.get(key)
    if isinstance(v, bool) or v is None:
        return None
    if isinstance(v, (int, float)):
        return float(v)
    return None


def jbool(o, key):
    if isinstance(o, dict):
        v = o.get(key)
        return v if isinstance(v, bool) else None
    return None


def _net_string(s):
    out = ['"']
    for ch in s:
        if ch == '"':
            out.append('\\"')
        elif ch == "\\":
            out.append("\\\\")
        elif ch == "\n":
            out.append("\\n")
        elif ch == "\r":
            out.append("\\r")
        elif ch == "\t":
            out.append("\\t")
        elif ord(ch) < 0x20:
            out.append("\\u%04x" % ord(ch))
        else:
            out.append(ch)
    out.append('"')
    return "".join(out)


def _net_double(x):
    """.NET の double.ToString("R") と同じ表記"""
    if x != x or x in (float("inf"), float("-inf")):
        return "null"
    r = repr(float(x))
    if "e" in r or "E" in r:
        mant, exp = r.lower().split("e")
        exp = int(exp)
    else:
        mant, exp = r, 0
    neg = mant.startswith("-")
    mant = mant.lstrip("-")
    if "." in mant:
        ip, fp = mant.split(".")
    else:
        ip, fp = mant, ""
    digits = (ip + fp).lstrip("0")
    lead_zeros = len(ip + fp) - len((ip + fp).lstrip("0"))
    if not digits:
        return "0"
    point = len(ip) + exp - lead_zeros       # 小数点の位置（digits の先頭からの桁数）
    digits = digits.rstrip("0")
    sci = point - 1
    if sci < -5 or sci >= 15:
        m = digits[0] + ("." + digits[1:] if len(digits) > 1 else "")
        s = m + "E" + ("+" if sci >= 0 else "-") + "%02d" % abs(sci)
    elif point <= 0:
        s = "0." + "0" * (-point) + digits
    elif point >= len(digits):
        s = digits + "0" * (point - len(digits))
    else:
        s = digits[:point] + "." + digits[point:]
    return ("-" if neg else "") + s


def canonical(o):
    """Windows 版 TeamCrypto.Canonical と同じバイト列（キーを UTF-16 の順で並べる）"""
    parts = []

    def w(v):
        if isinstance(v, dict):
            keys = sorted((str(k) for k in v.keys()), key=lambda k: k.encode("utf-16-be"))
            parts.append("{")
            for i, k in enumerate(keys):
                if i:
                    parts.append(",")
                parts.append(_net_string(k))
                parts.append(":")
                w(v[k])
            parts.append("}")
        elif isinstance(v, (list, tuple)):
            parts.append("[")
            for i, item in enumerate(v):
                if i:
                    parts.append(",")
                w(item)
            parts.append("]")
        elif v is None:
            parts.append("null")
        elif isinstance(v, bool):
            parts.append("true" if v else "false")
        elif isinstance(v, int):
            parts.append(str(v))
        elif isinstance(v, float):
            parts.append(_net_double(v))
        elif isinstance(v, str):
            parts.append(_net_string(v))
        else:
            parts.append(_net_string(str(v)))

    w(o)
    return "".join(parts).encode("utf-8")


def dumps(o, indent=False):
    if indent:
        return json.dumps(o, ensure_ascii=False, indent=2)
    return json.dumps(o, ensure_ascii=False, separators=(",", ":"))


def safe_line(s, limit):
    if s is None:
        return None
    out = []
    for ch in s:
        c = ord(ch)
        if c < 0x20 or c == 0x7F:
            out.append(" ")
            continue
        if 0x200B <= c <= 0x200F or 0x202A <= c <= 0x202E or 0x2066 <= c <= 0x2069 or c in (0xFEFF, 0x061C):
            continue
        out.append(ch)
        if len(out) >= limit:
            out.append("…")
            break
    return "".join(out).strip()


# =====================================================================================
# 暗号（標準規格どおりの実装。Windows 版の .NET 暗号ライブラリと相互に検証済み）
#   RSA-OAEP-SHA256 / RSA-PSS-SHA256（ソルト32バイト）/ AES-256-CBC / HMAC-SHA256
# =====================================================================================

def b64(b):
    return base64.b64encode(b).decode("ascii")


def unb64(s):
    if not isinstance(s, str):
        raise TeamError("データ形式が正しくありません")
    try:
        return base64.b64decode(s.encode("ascii"), validate=True)
    except Exception:
        raise TeamError("データ形式が正しくありません")


def i2osp(x, n):
    return x.to_bytes(n, "big")


def os2ip(b):
    return int.from_bytes(b, "big")


def frame(*parts):
    return b"".join(len(p).to_bytes(8, "little") + p for p in parts)


def _trim0(b):
    i = 0
    while i < len(b) - 1 and b[i] == 0:
        i += 1
    return b[i:]


def fingerprint(pub):
    n = _trim0(unb64(jstr(pub, "n")))
    e = _trim0(unb64(jstr(pub, "e")))
    h = hashlib.sha256(frame(b"aiub-fingerprint-v2", n, e)).hexdigest().upper()[:32]
    return "-".join(h[i * 4:(i + 1) * 4] for i in range(8))


def normalize_fingerprint(s):
    return "".join(ch for ch in (s or "") if ch in "0123456789abcdefABCDEF").upper()


def import_public(pub, min_bits=2048):
    n_b = _trim0(unb64(jstr(pub, "n")))
    e_b = _trim0(unb64(jstr(pub, "e")))
    bits = len(n_b) * 8
    if bits < min_bits or bits > 4096:
        raise TeamError("鍵の長さが正しくありません")
    if e_b != b"\x01\x00\x01":
        raise TeamError("鍵の形式が正しくありません")
    n = os2ip(n_b)
    return {"n": n, "e": 65537, "k": (n.bit_length() + 7) // 8}


def _mgf1(seed, length):
    out = b""
    counter = 0
    while len(out) < length:
        out += hashlib.sha256(seed + counter.to_bytes(4, "big")).digest()
        counter += 1
    return out[:length]


def _xor(a, b):
    return bytes(x ^ y for x, y in zip(a, b))


def rsa_oaep_encrypt(key, message):
    k = key["k"]
    hlen = 32
    if len(message) > k - 2 * hlen - 2:
        raise TeamError("暗号化するデータが長すぎます")
    lhash = hashlib.sha256(b"").digest()
    db = lh