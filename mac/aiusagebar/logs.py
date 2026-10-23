# -*- coding: utf-8 -*-
"""
Claude Code（~/.claude/projects）と Codex（~/.codex/sessions）のローカル記録を集計する。
Windows 版（C# の LocalLogs.cs）と同じ規則で、日別・ツール別・モデル別のトークンを出す。
Mac でも Windows でも同じ結果になるよう、重複除去のキーや日付の区切りを合わせている。
"""
import glob
import json
import os
from datetime import datetime, timezone


RETENTION_DAYS = 14
HISTORY_DAYS = 400


def _iso_ms(s):
    if not s:
        return 0
    try:
        # 例: 2026-09-17T08:44:00.123Z / +09:00
        t = s.strip()
        if t.endswith("Z"):
            t = t[:-1] + "+00:00"
        dt = datetime.fromisoformat(t)
        if dt.tzinfo is None:
            dt = dt.replace(tzinfo=timezone.utc)
        return int(dt.timestamp() * 1000)
    except Exception:
        return 0


def _local_date(ms):
    # 端末の現地時刻で日付を区切る（Windows 版と同じ）
    return datetime.fromtimestamp(ms / 1000).strftime("%Y-%m-%d")


class DailyUsage:
    def __init__(self, date, tool, model):
        self.date = date
        self.tool = tool          # "claude" / "codex"
        self.model = model
        self.input = self.output = self.cache_read = 0
        self.cache_write = self.cache_write_1h = self.reasoning = self.total = 0
        self.responses = 0

    def add(self, r):
        self.input += r["input"]; self.output += r["output"]; self.cache_read += r["cacheRead"]
        self.cache_write += r["cacheWrite"]; self.cache_write_1h += r["cacheWrite1h"]
        self.reasoning += r["reasoning"]; self.total += r["total"]; self.responses += 1


class LocalLogs:
    def __init__(self, home=None, codex_home=None):
        self.home = home or os.path.expanduser("~")
        self.claude_projects = os.path.join(self.home, ".claude", "projects")
        self.codex_home = codex_home or os.environ.get("CODEX_HOME") or os.path.join(self.home, ".codex")
        self.records = {}   # key -> record
        self.sessions = {}  # key -> session dict
        self.claude_available = os.path.isdir(self.claude_projects)
        self.codex_available = os.path.isdir(os.path.join(self.codex_home, "sessions")) or os.path.isdir(os.path.join(self.codex_home, "archived_sessions"))

    def _session(self, key, tool, sid):
        s = self.sessions.get(key)
        if s is None:
            s = {"key": key, "tool": tool, "id": sid, "cwd": None, "start": 0, "end": 0,
                 "model": None, "entrypoint": None, "originator": None, "thread_source": None,
                 "parent": None, "nickname": None, "title": None, "prompts": []}
            self.sessions[key] = s
        return s

    @staticmethod
    def _touch(s, t):
        if t <= 0:
            return
        if s["start"] == 0 or t < s["start"]:
            s["start"] = t
        if t > s["end"]:
            s["end"] = t

    @staticmethod
    def _add_prompt(s, t, text, source):
        if text is None:
            return
        for p in s["prompts"]:
            if p["t"] == t and p["text"] == text:
                return
        s["prompts"].append({"t": t, "text": text, "source": source})

    # ------------------------------------------------------------------ scan

    def scan(self):
        cutoff = int((datetime.now(timezone.utc).timestamp() - RETENTION_DAYS * 86400) * 1000)
        self.records = {}
        if self.claude_available:
            for f in _all_jsonl(self.claude_projects):
                try:
                    if os.path.getmtime(f) * 1000 < cutoff:
                        continue
                    self._scan_claude(f, cutoff)
                except Exception:
                    pass
        for sub in ("sessions", "archived_sessions"):
            d = os.path.join(self.codex_home, sub)
            if os.path.isdir(d):
                for f in _all_jsonl(d):
                    try:
                        if os.path.getmtime(f) * 1000 < cutoff:
                            continue
                        self._scan_codex(f, cutoff)
                    except Exception:
                        pass
        return self

    def _scan_claude(self, path, cutoff):
        import os as _os
        dirn = os.path.dirname(path)
        is_sub = os.path.basename(dirn).lower() == "subagents"
        if is_sub:
            file_session = os.path.basename(os.path.dirname(dirn))
        else:
            file_session = _os.path.splitext(os.path.basename(path))[0]
        for line in _read_lines(path):
            is_asst = '"role":"assistant"' in line or '"type":"assistant"' in line
            is_user = '"type":"user"' in line
            is_queued = '"type":"queued_command"' in line
            is_aititle = '"type":"ai-title"' in line
            is_custom = '"type":"custom-title"' in line
            if not (is_asst or is_user or is_queued or is_aititle or is_custom):
                continue
            if (is_user or is_queued) and (is_sub or '"isSidechain":true' in line):
                continue
            try:
                o = json.loads(line)
            except ValueError:
                continue
            sid = o.get("sessionId") or file_session
            s = self._session("claude:" + sid, "claude", sid)
            if s["cwd"] is None:
                s["cwd"] = o.get("cwd")
            entry = o.get("entrypoint")
            if entry and not is_sub:
                s["entrypoint"] = entry
            if is_aititle:
                s["title"] = o.get("aiTitle") or s["title"]
                continue
            if is_custom:
                s["title"] = o.get("customTitle") or s["title"]
                continue
            t = _iso_ms(o.get("timestamp"))
            if (is_user or is_queued) and not is_asst:
                if not _is_human_claude_prompt(o):
                    continue
                if t and t < cutoff:
                    continue
                self._touch(s, t)
                self._add_prompt(s, t, _claude_prompt_text(o), entry or s["entrypoint"])
                continue
            if not is_asst:
                continue
            msg = o.get("message") or {}
            usage = msg.get("usage")
            if not isinstance(usage, dict):
                continue
            if t and t < cutoff:
                continue
            model = msg.get("model")
            if isinstance(model, str) and model.startswith("<"):
                model = None
            if model:
                s["model"] = model
            self._touch(s, t)
            msg_id = msg.get("id")
            req_id = o.get("requestId")
            if msg_id is not None or req_id is not None:
                key = "c:" + (msg_id or "") + ":" + (req_id or "")
            else:
                key = "c:" + str(o.get("uuid"))
            cw = _long(usage, "cache_creation_input_tokens")
            cc = usage.get("cache_creation") or {}
            cw1h = _long(cc, "ephemeral_1h_input_tokens")
            if cw == 0:
                cw = _long(cc, "ephemeral_5m_input_tokens") + cw1h
            if cw1h > cw:
                cw1h = cw
            r = {
                "session": "claude", "sessionKey": s["key"], "t": t, "model": model,
                "input": _long(usage, "input_tokens"), "output": _long(usage, "output_tokens"),
                "cacheRead": _long(usage, "cache_read_input_tokens"), "cacheWrite": cw,
                "cacheWrite1h": cw1h, "reasoning": 0,
            }
            r["total"] = r["input"] + r["output"] + r["cacheRead"] + r["cacheWrite"]
            if r["total"] == 0:
                continue
            self._merge(key, r)

    def _scan_codex(self, path, cutoff):
        model = None
        sid = None
        name = os.path.splitext(os.path.basename(path))[0]
        if len(name) >= 36:
            sid = name[-36:]
        offset = 0
        for line in _read_lines(path):
            offset += 1
            if '"type":"turn_context"' in line:
                try:
                    p = (json.loads(line).get("payload") or {})
                    if p.get("model"):
                        model = p.get("model")
                        if sid:
                            self._session("codex:" + sid, "codex", sid)["model"] = model
                except ValueError:
                    pass
                continue
            if '"type":"session_meta"' in line:
                try:
                    p = (json.loads(line).get("payload") or {})
                    sid = p.get("id") or p.get("session_id") or sid
                    if sid:
                        s = self._session("codex:" + sid, "codex", sid)
                        s["cwd"] = s["cwd"] or p.get("cwd")
                        s["originator"] = p.get("originator")
                        s["thread_source"] = p.get("thread_source")
                        src = p.get("source")
                        spawn = (((src if isinstance(src, dict) else {}).get("subagent") or {}).get("thread_spawn")) or {}
                        if spawn.get("parent_thread_id"):
                            s["parent"] = "codex:" + spawn["parent_thread_id"]
                            s["nickname"] = spawn.get("agent_nickname")
                            if not s["thread_source"]:
                                s["thread_source"] = "subagent"
                except ValueError:
                    pass
                continue
            if ('"type":"response_item"' in line and '"role":"user"' in line
                    and '"text":"<' not in line and '"text":"# AGENTS.md' not in line and '"text":""' not in line):
                try:
                    o = json.loads(line)
                except ValueError:
                    continue
                if sid:
                    t = _iso_ms(o.get("timestamp"))
                    if not t or t >= cutoff:
                        s = self._session("codex:" + sid, "codex", sid)
                        self._touch(s, t)
                        self._add_prompt(s, t, _codex_prompt_text(o), s["originator"])
                continue
            if '"type":"token_usage_record"' not in line:
                continue
            try:
                o = json.loads(line)
            except ValueError:
                continue
            p = o.get("payload") or {}
            usage = p.get("usage")
            if not isinstance(usage, dict):
                continue
            t = _iso_ms(o.get("timestamp"))
            if t and t < cutoff:
                continue
            resp = p.get("response_id")
            owner = p.get("thread_id") or sid
            if owner:
                self._touch(self._session("codex:" + owner, "codex", owner), t)
            total = _long(usage, "total_tokens")
            if total == 0:
                total = _long(usage, "input_tokens") + _long(usage, "output_tokens")
            r = {
                "session": "codex", "sessionKey": "codex:" + str(owner), "t": t, "model": model,
                "input": _long(usage, "input_tokens"), "output": _long(usage, "output_tokens"),
                "cacheRead": _long(usage, "cached_input_tokens"),
                "cacheWrite": _long(usage, "cache_write_input_tokens"),
                "cacheWrite1h": 0, "reasoning": _long(usage, "reasoning_output_tokens"),
                "total": total,
            }
            if r["total"] == 0:
                continue
            key = "x:" + (resp or (str(sid) + ":" + str(offset)))
            self._merge(key, r)

    def _merge(self, key, r):
        cur = self.records.get(key)
        if cur is None:
            self.records[key] = r
            return
        # C# の TokenRecord.MaxWith：各項目の最大を取る（同じ応答が複数ファイルに出ても二重に数えない）
        for f in ("input", "output", "cacheRead", "cacheWrite", "cacheWrite1h", "reasoning", "total"):
            cur[f] = max(cur[f], r[f])
        if cur["model"] is None:
            cur["model"] = r["model"]
        if r["t"] and (cur["t"] == 0 or r["t"] < cur["t"]):
            cur["t"] = r["t"]

    # ------------------------------------------------------------------ history

    def history(self):
        keep_from = datetime.now().strftime("%Y-%m-%d")
        keep_from = (datetime.now().date().toordinal() - HISTORY_DAYS)
        by_key = {}
        for r in self.records.values():
            if r["t"] == 0:
                continue
            date = _local_date(r["t"])
            if datetime.strptime(date, "%Y-%m-%d").date().toordinal() < keep_from:
                continue
            tool = "codex" if r["session"] == "codex" else "claude"
            k = date + "|" + tool + "|" + (r["model"] or "")
            d = by_key.get(k)
            if d is None:
                d = DailyUsage(date, tool, r["model"])
                by_key[k] = d
            d.add(r)
        return sorted(by_key.values(), key=lambda d: d.date)

    def history_rows(self):
        """報告に載せる形（C# の BuildPayload の daily 行）: [date, tool, model, in, out, cr, cw, cw1h, reasoning, total, responses]"""
        rows = []
        for d in self.history():
            rows.append([d.date, d.tool, d.model, d.input, d.output, d.cache_read,
                         d.cache_write, d.cache_write_1h, d.reasoning, d.total, d.responses])
        return rows

    def _codex_titles(self):
        titles = {}
        path = os.path.join(self.codex_home, "session_index.jsonl")
        for line in _read_lines(path):
            try:
                o = json.loads(line)
            except ValueError:
                continue
            if o.get("id") and o.get("thread_name"):
                titles[o["id"]] = o["thread_name"]   # 後の行が新しい
        return titles

    def _title(self, s, codex_titles):
        if s["tool"] == "codex":
            return codex_titles.get(s["id"])
        return s["title"]

    def sessions_payload(self, session_days, include_prompts, max_prompt_chars):
        """C# の BuildPayload と同じ sessions / prompts を作る（直近 session_days 日、サブエージェントは親にまとめる）。"""
        now_ms = int(datetime.now(timezone.utc).timestamp() * 1000)
        # 期間の開始（現地時刻の日付境界、SESSION_DAYS-1 日前の 0:00）
        start_day = datetime.now().date().toordinal() - (session_days - 1)
        from_ms = int(datetime.fromordinal(start_day).timestamp() * 1000)
        codex_titles = self._codex_titles()

        def root_key(key):
            k = key
            guard = 0
            while guard < 8:
                s = self.sessions.get(k)
                if not s or not s["parent"]:
                    return k
                k = s["parent"]
                guard += 1
            return k

        tokens_by_root = {}
        for r in self.records.values():
            if r["t"] < from_ms:
                continue
            rk = root_key(r.get("sessionKey") or "")
            tokens_by_root[rk] = tokens_by_root.get(rk, 0) + r["total"]

        sessions = []
        prompts = []
        seen = set()
        # 期間内に動きのあった root セッションを、新しい順に
        active = {}
        for r in self.records.values():
            if r["t"] < from_ms:
                continue
            rk = root_key(r.get("sessionKey") or "")
            active[rk] = max(active.get(rk, 0), r["t"])
        for s in self.sessions.values():
            for p in s["prompts"]:
                if p["t"] >= from_ms:
                    rk = root_key(s["key"])
                    active[rk] = max(active.get(rk, 0), p["t"])
        for key in sorted(active, key=lambda k: active[k], reverse=True):
            s = self.sessions.get(key)
            if s is None or key in seen:
                continue
            seen.add(key)
            n_prompts = sum(1 for p in s["prompts"] if p["t"] >= from_ms)
            sessions.append({
                "key": s["key"], "tool": s["tool"], "title": self._title(s, codex_titles),
                "cwd": s["cwd"], "start": s["start"], "end": s["end"], "model": s["model"],
                "via": _via_label(s, None), "inputKind": _input_kind_label(s, None),
                "subagents": 0, "prompts": n_prompts, "totalTokens": tokens_by_root.get(key, 0),
            })
            if include_prompts:
                for p in sorted((x for x in s["prompts"] if x["t"] >= from_ms), key=lambda x: x["t"]):
                    text = p["text"]
                    if text is not None and len(text) > max_prompt_chars:
                        text = text[:max_prompt_chars] + "…（以下省略）"
                    prompts.append({
                        "t": p["t"], "sessionKey": s["key"], "tool": s["tool"],
                        "via": _via_label(s, p["source"]), "inputKind": _input_kind_label(s, p["source"]),
                        "text": text,
                    })
        return sessions, prompts


def _is_human_claude_prompt(o):
    if o.get("isMeta") is True or o.get("isSidechain") is True:
        return False
    origin = o.get("origin") or ((o.get("attachment") or {}).get("origin"))
    kind = (origin or {}).get("kind") if isinstance(origin, dict) else None
    if kind is not None and kind != "human":
        return False
    text = _claude_prompt_text(o)
    if not isinstance(text, str):
        return False
    t = text.lstrip()
    if not t:
        return False
    for pre in ("<command-", "<local-command", "<task-notification", "<system-reminder", "Caveat:", "[Request interrupted"):
        if t.startswith(pre):
            return False
    return True


def _claude_prompt_text(o):
    att = o.get("attachment")
    if isinstance(att, dict):
        pr = att.get("prompt") if att.get("type") == "queued_command" else None
        return pr if isinstance(pr, str) else None
    msg = o.get("message")
    if not isinstance(msg, dict) or "content" not in msg:
        return None
    content = msg["content"]
    if isinstance(content, str):
        return content
    if not isinstance(content, list):
        return None
    parts = []
    images = 0
    for c in content:
        typ = c.get("type") if isinstance(c, dict) else None
        if typ == "text":
            parts.append(c.get("text") or "")
        elif typ == "image":
            images += 1
        elif typ == "tool_result":
            return None
    if not parts and images:
        return "［画像 %d枚］" % images
    return "\n".join(parts)


def _codex_prompt_text(o):
    p = o.get("payload") or {}
    content = p.get("content")
    if not isinstance(content, list):
        return None
    parts = []
    images = 0
    for c in content:
        typ = c.get("type") if isinstance(c, dict) else None
        if typ in ("input_text", "text"):
            text = c.get("text") or ""
            tt = text.lstrip()
            if tt.startswith("<") or tt.startswith("# AGENTS.md"):
                continue
            idx = text.find("## My request for Codex:")
            if idx >= 0:
                text = text[idx + len("## My request for Codex:"):]
            elif tt.startswith("# Files mentioned by the user"):
                continue
            parts.append(text.strip())
        elif typ == "input_image":
            images += 1
    if not parts and images:
        return "［画像 %d枚］" % images
    return "\n".join(parts) if parts else None


def _via_label(s, source):
    if s["tool"] == "claude":
        e = source or s["entrypoint"]
        return {"claude-desktop": "Claudeアプリ（Code）", "cli": "ターミナル（CLI）", "sdk-cli": "他のプログラムから実行",
                "sdk-ts": "SDK", "sdk-py": "SDK", "claude-vscode": "VS Code", "claude-jetbrains": "JetBrains",
                None: "Claude Code"}.get(e, e)
    via = {"Codex Desktop": "Codexアプリ", "codex_exec": "codex exec", "codex_cli_rs": "ターミナル（CLI）",
           "codex_vscode": "VS Code", None: "Codex"}.get(s["originator"], s["originator"])
    if s["thread_source"] == "automation":
        via += "・オートメーション"
    if s["thread_source"] == "subagent":
        via += "・サブエージェント" + (("（" + s["nickname"] + "）") if s["nickname"] else "")
    return via


def _input_kind_label(s, source):
    if s["tool"] == "claude":
        e = source or s["entrypoint"]
        return "他のプログラム経由" if e in ("sdk-cli", "sdk-ts", "sdk-py") else "本人の入力"
    if s["thread_source"] == "subagent":
        return "AIが入力（サブエージェント）"
    if s["thread_source"] == "automation":
        return "自動実行"
    if s["originator"] == "codex_exec":
        return "コマンド実行"
    return "本人の入力"


def _long(o, k):
    v = o.get(k) if isinstance(o, dict) else None
    if isinstance(v, bool):
        return 0
    if isinstance(v, int):
        return max(0, v)
    if isinstance(v, float):
        return int(v) if v > 0 else 0
    return 0


def _all_jsonl(root):
    return glob.glob(os.path.join(root, "**", "*.jsonl"), recursive=True)


def _read_lines(path):
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as f:
            for line in f:
                line = line.rstrip("\n").rstrip("\r")
                if line:
                    yield line
    except (IOError, OSError):
        return
