# -*- coding: utf-8 -*-
"""
Mac 用のチーム参加・報告。共有フォルダ（AIUsageBar-Team）に対して、Windows 版と同じ形式で
報告を暗号化・署名して書き込む。マスター機能（読み取り・鍵管理）は Windows 版が担当し、
このツールは「参加」と「報告」だけを行う。
"""
import base64
import os
import re
import secrets
import stat
import tempfile
import time
from datetime import datetime, timezone

from . import crypto, jsonc
from .crypto import TeamError

FOLDER_NAME = "AIUsageBar-Team"
REPORT_FORMAT = "aiub-report-2"
TEAM_FORMAT = "aiub-team-2"
SESSION_DAYS = 14
MAX_PROMPT_CHARS = 8000
MAX_REPORT_BYTES = 8 * 1024 * 1024
MAX_TEAM_FILE_BYTES = 2 * 1024 * 1024
APP_VERSION = "0.1.0"

SCOPE_USAGE, SCOPE_SUMMARY, SCOPE_PROMPTS = 0, 1, 2
_SCOPE_KEY = {SCOPE_USAGE: "usage", SCOPE_SUMMARY: "summary", SCOPE_PROMPTS: "prompts"}
_SCOPE_PARSE = {"usage": SCOPE_USAGE, "summary": SCOPE_SUMMARY, "prompts": SCOPE_PROMPTS}


def scope_key(s):
    return _SCOPE_KEY.get(s, "usage")


def scope_parse(s):
    return _SCOPE_PARSE.get(s, SCOPE_USAGE)


def normalize_email(e):
    if not e:
        return None
    e = e.strip().lower()
    return e if "@" in e and len(e) <= 254 else None


# ------------------------------------------------------------------ 招待コード

INVITE_PREFIX = "AIUB-INVITE-1:"


def decode_invite(text):
    """招待文・招待コード・招待リンク（aiusagebar:join?c=...）のいずれからも読み取る。 -> (folder, team_name, fingerprint) または None"""
    if not text or len(text) > 20000:
        return None
    m = re.search(re.escape(INVITE_PREFIX) + r"([A-Za-z0-9_-]{16,4000})", text)
    if not m:
        m = re.search(r"aiusagebar:\S*[?&]c=([A-Za-z0-9_-]{16,4000})", text)
    if not m:
        return None
    try:
        b64 = m.group(1).replace("-", "+").replace("_", "/")
        b64 += "=" * ((4 - len(b64) % 4) % 4)
        o = jsonc.loads(base64.b64decode(b64).decode("utf-8"))
        folder = o.get("folder")
        fp = crypto.normalize_fingerprint(o.get("fingerprint"))
        if len(fp) != 32 or not folder:
            return None
        fp = "-".join(fp[i * 4:i * 4 + 4] for i in range(8))
        return folder, o.get("team"), fp
    except Exception:
        return None


# ------------------------------------------------------------------ チーム設定（読み取り・検証）

class TeamConfig:
    def __init__(self):
        self.team_id = None
        self.team_name = None
        self.version = 0
        self.key_generation = 1
        self.public_key = None
        self.rotations = []
        self.default_scope = SCOPE_USAGE
        self.members = []
        self.auto_approve = True

    @property
    def fingerprint(self):
        return crypto.fingerprint(self.public_key)

    @staticmethod
    def from_json(o):
        if not isinstance(o, dict) or o.get("format") != TEAM_FORMAT:
            raise TeamError("チーム設定の形式が正しくありません（対応していない版です）")
        t = TeamConfig()
        t.team_id = o.get("teamId")
        t.team_name = o.get("teamName")
        t.version = int(o.get("version") or 0)
        t.key_generation = max(1, min(100000, int(o.get("keyGeneration") or 1)))
        t.public_key = o.get("publicKey")
        t.default_scope = scope_parse(o.get("defaultScope"))
        t.auto_approve = o.get("autoApproveByLogin", True) is not False
        if t.team_id is None or t.public_key is None:
            raise TeamError("チーム設定の形式が正しくありません")
        for r in (o.get("rotations") or [])[:1000]:
            t.rotations.append({"generation": int(r.get("generation") or 0),
                                "publicKey": r.get("publicKey"), "proof": r.get("proof")})
        for m in (o.get("members") or [])[:5000]:
            emails = [str(x) for x in (m.get("emails") or [])[:20] if isinstance(x, str)]
            t.members.append({"id": m.get("id"), "name": m.get("name"),
                              "emails": emails, "isMaster": m.get("role") == "master",
                              "scope": scope_parse(m.get("scope"))})
        return t

    def scope_for(self, emails):
        norm = set(e for e in (normalize_email(x) for x in emails) if e)
        for m in self.members:
            mine = set(e for e in (normalize_email(x) for x in m["emails"]) if e)
            if mine & norm:
                return m["scope"]
        return self.default_scope


def peek(chosen_folder):
    """参加前の確認。署名の自己検証のみ（本物かは確認コードで照合する）。 -> (TeamConfig, folder)"""
    folder = resolve_folder(chosen_folder)
    team_file = os.path.join(folder, "team.json")
    if not os.path.isfile(team_file):
        raise TeamError("このフォルダにチームが見つかりません（team.json がありません）")
    if os.path.getsize(team_file) > MAX_TEAM_FILE_BYTES:
        raise TeamError("チーム設定ファイルが大きすぎます")
    with open(team_file, "r", encoding="utf-8") as f:
        doc = jsonc.loads(f.read())
    team = TeamConfig.from_json(doc)
    pub = crypto.import_public(team.public_key, crypto.TEAM_KEY_BITS)
    if not crypto.verify(pub, doc):
        raise TeamError("チーム設定の署名が正しくありません（改ざんの可能性があります）")
    return team, folder


def resolve_folder(chosen):
    if not chosen or not chosen.strip():
        raise TeamError("共有フォルダを選んでください")
    chosen = chosen.strip()
    if os.path.isfile(os.path.join(chosen, "team.json")):
        return chosen
    return os.path.join(chosen, FOLDER_NAME)


# ------------------------------------------------------------------ 参加状態（このMac）

class TeamLocal:
    def __init__(self, data_dir):
        self.data_dir = data_dir
        self.install_id = None
        self.shared_folder = None
        self.team_id = None
        self.pinned_generation = 0
        self.pinned_public_key = None
        self.max_version_seen = 0
        self.member_key = None       # 秘密鍵の JSON（このMacの本人のみ読める権限で保存）
        self.declared_name = None
        self.declared_email = None
        self.consented_scope = SCOPE_USAGE
        self.consented_at = None
        self.last_upload = None
        self.last_hash = None

    @property
    def path(self):
        return os.path.join(self.data_dir, "team-local.json")

    @property
    def joined(self):
        return bool(self.team_id and self.shared_folder)

    @staticmethod
    def load(data_dir):
        l = TeamLocal(data_dir)
        try:
            with open(l.path, "r", encoding="utf-8") as f:
                o = jsonc.loads(f.read())
            l.install_id = o.get("installId")
            l.shared_folder = o.get("sharedFolder")
            l.team_id = o.get("teamId")
            l.pinned_generation = int(o.get("pinnedGeneration") or 0)
            l.pinned_public_key = o.get("pinnedPublicKey")
            l.max_version_seen = int(o.get("maxVersionSeen") or 0)
            l.member_key = o.get("memberKey")
            l.declared_name = o.get("declaredName")
            l.declared_email = o.get("declaredEmail")
            l.consented_scope = scope_parse(o.get("consentedScope"))
            l.last_upload = o.get("lastUpload")
            l.last_hash = o.get("lastPayloadHash")
        except (IOError, OSError):
            l.install_id = secrets.token_hex(16)
        except ValueError:
            l.install_id = secrets.token_hex(16)
        if not l.install_id or len(l.install_id) != 32:
            l.install_id = secrets.token_hex(16)
        return l

    def save(self):
        os.makedirs(self.data_dir, exist_ok=True)
        try:
            os.chmod(self.data_dir, stat.S_IRWXU)
        except OSError:
            pass
        d = {
            "installId": self.install_id, "sharedFolder": self.shared_folder, "teamId": self.team_id,
            "pinnedGeneration": self.pinned_generation, "pinnedPublicKey": self.pinned_public_key,
            "maxVersionSeen": self.max_version_seen, "memberKey": self.member_key,
            "declaredName": self.declared_name, "declaredEmail": self.declared_email,
            "consentedScope": scope_key(self.consented_scope),
            "lastUpload": self.last_upload, "lastPayloadHash": self.last_hash,
        }
        _write_private(self.path, jsonc.dumps(d, indent=True))


# ------------------------------------------------------------------ サービス（参加・報告）

class TeamService:
    def __init__(self, data_dir, logs, detected_emails_fn, quota_fn):
        self.local = TeamLocal.load(data_dir)
        self.logs = logs
        self._detected_emails_fn = detected_emails_fn   # () -> [email]
        self._quota_fn = quota_fn                         # () -> {"codex":..., "claude":...}
        self.team = None
        self.error = None

    def my_detected_emails(self):
        return [e for e in (normalize_email(x) for x in (self._detected_emails_fn() or [])) if e]

    def my_emails(self):
        out = list(self.my_detected_emails())
        d = normalize_email(self.local.declared_email)
        if d and d not in out:
            return out + [d]
        return out

    def my_pc_fingerprint(self):
        if not self.local.member_key:
            return None
        try:
            k = crypto.RsaKey.from_private_json(self.local.member_key)
            return crypto.fingerprint(k.public_dict())
        except Exception:
            return None

    def join(self, chosen_folder, typed_fingerprint, name, email, confirmed=None):
        if normalize_email(email) is None:
            raise TeamError("メールアドレスを正しく入力してください")
        team, folder = peek(chosen_folder)
        if confirmed is not None and (confirmed.team_id != team.team_id
                                      or jsonc.dumps(confirmed.public_key) != jsonc.dumps(team.public_key)):
            raise TeamError("確認した後にチーム設定が変わりました。もう一度読み込んでください")
        if crypto.normalize_fingerprint(typed_fingerprint) != crypto.normalize_fingerprint(team.fingerprint):
            raise TeamError("入力したチーム確認コードが、共有フォルダのチームと一致しません")
        member = crypto.RsaKey.generate(crypto.MEMBER_KEY_BITS)
        if not self.local.install_id:
            self.local.install_id = secrets.token_hex(16)
        self.local.shared_folder = folder
        self.local.team_id = team.team_id
        self.local.pinned_generation = team.key_generation
        self.local.pinned_public_key = jsonc.dumps(team.public_key)
        self.local.max_version_seen = team.version
        self.local.member_key = member.private_json()
        self.local.declared_name = (name or "").strip() or os.environ.get("USER") or "me"
        self.local.declared_email = email.strip()
        self.local.consented_scope = SCOPE_PROMPTS   # すべての共有に同意して参加
        self.local.consented_at = int(time.time() * 1000)
        self.local.last_hash = None
        self.local.last_upload = None
        self.local.save()
        self.team = team
        self.error = None

    def leave(self):
        self._delete_my_report()
        install = self.local.install_id or secrets.token_hex(16)
        self.local = TeamLocal(self.local.data_dir)
        self.local.install_id = install
        self.local.save()
        self.team = None

    def load_team(self):
        if not self.local.joined:
            raise TeamError("チームに参加していません")
        team_file = os.path.join(self.local.shared_folder, "team.json")
        if not os.path.isfile(team_file):
            raise TeamError("共有フォルダのチーム設定が見つかりません")
        if os.path.getsize(team_file) > MAX_TEAM_FILE_BYTES:
            raise TeamError("チーム設定ファイルが大きすぎます")
        with open(team_file, "r", encoding="utf-8") as f:
            doc = jsonc.loads(f.read())
        team = TeamConfig.from_json(doc)
        if team.team_id != self.local.team_id:
            raise TeamError("共有フォルダのチームが、参加したチームと違います")
        if team.version < self.local.max_version_seen:
            raise TeamError("チーム設定が古い版に戻されています（改ざんの可能性があります）")
        if team.key_generation < self.local.pinned_generation:
            raise TeamError("チームの鍵が古い世代に戻されています")
        pinned = jsonc.loads(self.local.pinned_public_key)
        same = jsonc.dumps(pinned) == jsonc.dumps(team.public_key)
        if not same:
            if team.key_generation == self.local.pinned_generation:
                raise TeamError("チームの鍵が変わっています（改ざんの可能性があります）")
            current = pinned
            for g in range(self.local.pinned_generation + 1, team.key_generation + 1):
                step = next((r for r in team.rotations if r["generation"] == g), None)
                if not step or not step.get("publicKey") or not step.get("proof"):
                    raise TeamError("チームの鍵の更新を確認できません")
                prev = crypto.import_public(current, crypto.TEAM_KEY_BITS)
                stmt = crypto.jsonc.canonical({"purpose": "aiub-key-rotation", "teamId": team.team_id,
                                               "generation": g, "publicKey": step["publicKey"]})
                if not crypto.verify_bytes(prev, stmt, step["proof"]):
                    raise TeamError("チームの鍵の更新を確認できません")
                current = step["publicKey"]
            if jsonc.dumps(current) != jsonc.dumps(team.public_key):
                raise TeamError("チームの鍵の更新を確認できません")
        pub = crypto.import_public(team.public_key, crypto.TEAM_KEY_BITS)
        if not crypto.verify(pub, doc):
            raise TeamError("チーム設定の署名が正しくありません")
        changed = False
        if not same:
            self.local.pinned_generation = team.key_generation
            self.local.pinned_public_key = jsonc.dumps(team.public_key)
            changed = True
        if team.version > self.local.max_version_seen:
            self.local.max_version_seen = team.version
            changed = True
        if changed:
            self.local.save()
        self.team = team
        self.error = None
        return team

    def effective_scope(self, team):
        # 同意はすべて（Prompts）。マスターの設定より広くは共有しない
        return min(team.scope_for(self.my_emails()), self.local.consented_scope)

    # ------------------------------------------------------------------ 報告

    def build_payload(self, scope):
        d = {
            "appVersion": APP_VERSION,
            "windowsUser": os.environ.get("USER") or "mac",
            "machine": _machine_name(),
            "declaredName": self.local.declared_name,
            "declaredEmail": self.local.declared_email,
            "detectedEmails": self.my_detected_emails(),
            "scope": scope_key(scope),
            "consentedScope": scope_key(self.local.consented_scope),
        }
        q = self._quota_fn() or {}
        quota = {}
        if q.get("codex"):
            quota["codex"] = q["codex"]
        if q.get("claude"):
            quota["claude"] = q["claude"]
        d["quota"] = quota
        d["daily"] = self.logs.history_rows()
        if q.get("codexServiceDaily") is not None:
            d["codexServiceDaily"] = [{"date": x["date"], "tokens": x["tokens"]} for x in q["codexServiceDaily"]]
        if scope >= SCOPE_SUMMARY and hasattr(self.logs, "sessions_payload"):
            sessions, prompts = self.logs.sessions_payload(SESSION_DAYS, scope >= SCOPE_PROMPTS, MAX_PROMPT_CHARS)
            d["sessions"] = sessions
            if scope >= SCOPE_PROMPTS:
                d["prompts"] = prompts
        return d

    @staticmethod
    def pad(plain):
        buckets = [64 * 1024, 256 * 1024, 1024 * 1024, 4 * 1024 * 1024, MAX_REPORT_BYTES // 2]
        target = next((b for b in buckets if b >= len(plain) + 64), 0)
        if target == 0:
            raise TeamError("報告が大きすぎます")
        return plain + b" " * (target - len(plain))

    def upload(self, force=False):
        if not self.local.joined:
            return "チーム未参加"
        now_ms = int(time.time() * 1000)
        if not force and self.local.last_upload and now_ms - self.local.last_upload < 5 * 60 * 1000:
            return "前回から5分以内"
        try:
            team = self.load_team()
            scope = self.effective_scope(team)
            payload_json = jsonc.dumps(self.build_payload(scope))
            import hashlib
            h = crypto.b64(hashlib.sha256((payload_json + "|" + str(team.key_generation)).encode("utf-8")).digest())
            if not force and h == self.local.last_hash and self.local.last_upload and now_ms - self.local.last_upload < 60 * 60 * 1000:
                return "変更なし"
            plain = self.pad(payload_json.encode("utf-8"))
            member = crypto.RsaKey.from_private_json(self.local.member_key)
            header = {"format": REPORT_FORMAT, "teamId": team.team_id, "keyGeneration": team.key_generation,
                      "installId": self.local.install_id, "createdAt": now_ms,
                      "memberPublicKey": member.public_dict()}
            team_pub = crypto.import_public(team.public_key, crypto.TEAM_KEY_BITS)
            sealed = crypto.encrypt_for_team(team_pub, plain, jsonc.canonical(header))
            doc = dict(header)
            doc["payload"] = sealed
            doc["signature"] = crypto.sign(member, doc)
            reports = os.path.join(self.local.shared_folder, "reports")
            os.makedirs(reports, exist_ok=True)
            _write_atomic(os.path.join(reports, self.local.install_id + ".aiubr"), jsonc.dumps(doc))
            self.local.last_upload = now_ms
            self.local.last_hash = h
            self.local.save()
            return None
        except TeamError as ex:
            self.error = str(ex)
            return str(ex)
        except (IOError, OSError):
            return "共有フォルダに書き込めません"

    def _delete_my_report(self):
        try:
            if self.local.shared_folder and self.local.install_id:
                p = os.path.join(self.local.shared_folder, "reports", self.local.install_id + ".aiubr")
                if os.path.isfile(p):
                    os.remove(p)
        except OSError:
            pass


def _machine_name():
    import socket
    try:
        return socket.gethostname().split(".")[0]
    except Exception:
        return "mac"


def _write_atomic(path, text):
    # Exclusive creation avoids truncating an existing file or following a
    # pre-created symlink. Keep the temporary file on the destination volume.
    fd, tmp = tempfile.mkstemp(prefix=os.path.basename(path) + ".tmp-",
                               dir=os.path.dirname(os.path.abspath(path)))
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as f:
            f.write(text)
        os.replace(tmp, path)
    finally:
        try:
            os.unlink(tmp)
        except FileNotFoundError:
            pass


def _write_private(path, text):
    # mkstemp creates the file with owner-only permissions on POSIX, before
    # secret content is written. fdopen also handles complete UTF-8 writes.
    _write_atomic(path, text)


def default_data_dir():
    return os.path.join(os.path.expanduser("~"), "Library", "Application Support", "AIUsageBar")
