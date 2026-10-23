# -*- coding: utf-8 -*-
import binascii
from aiusagebar import crypto, jsonc

ok = 0; fail = 0
def check(cond, name):
    global ok, fail
    if cond: ok += 1; print("  PASS ", name)
    else: fail += 1; print("  FAIL ", name)

# AES-256 known-answer (NIST FIPS-197 appendix C.3)
key = binascii.unhexlify("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f")
pt  = binascii.unhexlify("00112233445566778899aabbccddeeff")
aes = crypto._Aes256(key)
ct = aes.encrypt_block(pt)
check(binascii.hexlify(ct).decode() == "8ea2b7ca516745bfeafc49904b496089", "AES-256 暗号化（FIPS-197）")
check(aes.decrypt_block(ct) == pt, "AES-256 復号")

# CBC round trip
k = crypto.secrets.token_bytes(32); iv = crypto.secrets.token_bytes(16)
for n in (0,1,15,16,17,100):
    msg = crypto.secrets.token_bytes(n)
    c = crypto.aes256_cbc_encrypt(k, iv, msg)
    check(crypto.aes256_cbc_decrypt(k, iv, c) == msg, "AES-CBC 往復 len=%d" % n)

# RSA keygen + OAEP + PSS round trip (小さめの鍵で速く)
key2048 = crypto.RsaKey.generate(2048)
pub = crypto.import_public(key2048.public_dict(), 2048)
m = b"hello team " * 5
c = crypto.rsa_oaep_encrypt(pub, m)
check(crypto.rsa_oaep_decrypt(key2048, c) == m, "RSA-OAEP-SHA256 往復")
sig = crypto.rsa_pss_sign(key2048, b"sign me")
check(crypto.rsa_pss_verify(pub, b"sign me", sig), "RSA-PSS-SHA256 署名/検証")
check(not crypto.rsa_pss_verify(pub, b"sign ME", sig), "改ざんは検証失敗")

# EncryptForTeam round trip
team = crypto.RsaKey.generate(3072)
tpub = crypto.import_public(team.public_dict(), 3072)
sealed = crypto.encrypt_for_team(tpub, b"payload data", b"assoc")
check(crypto.decrypt_for_team(team, sealed, b"assoc") == b"payload data", "EncryptForTeam 往復")
try:
    crypto.decrypt_for_team(team, sealed, b"other"); check(False, "関連データ不一致で失敗")
except crypto.TeamError: check(True, "関連データ不一致で失敗")

# fingerprint stable + 32 hex
fp = crypto.fingerprint(team.public_dict())
check(len(fp.replace("-","")) == 32 and crypto.normalize_fingerprint(fp) == fp.replace("-",""), "確認コード 128bit")

# jsonc canonical: sorted keys, exact number formatting
check(jsonc.canonical({"b":1,"a":[1,2],"c":"x"}) == b'{"a":[1,2],"b":1,"c":"x"}', "正規化 JSON（キー整列）")
check(jsonc.dumps(1000000) == "1000000" and jsonc.dumps(3.5)=="3.5" and jsonc.dumps(2.0)=="2", "数値の書式")
check(jsonc.dumps("あ\"\n") == '"あ\\"\\n"', "文字列のエスケープ")

# フレンドの残り枠（検証・招待コード・自分除外・古い/未来の除外）
from aiusagebar import friends as F
import os, tempfile
_tmp = tempfile.mkdtemp(prefix="aiub-friends-")
_cfg = {"id": F.new_id(), "folder": _tmp, "displayName": "テスト太郎", "enabled": True}
_snap = {"windows": [{"minutes": 300, "usedPercent": 40, "resetsAt": F.now_ms()+3600000},
                     {"minutes": 10080, "usedPercent": 10, "resetsAt": F.now_ms()+600000000}]}
_mine = F.build_mine(_cfg, {"fiveHourRemaining": 99, "weeklyRemaining": 0}, _snap)
_rt = F.parse_status(jsonc.loads(jsonc.dumps(_mine)))
check(bool(_rt) and _rt["id"] == _cfg["id"] and _rt["name"] == "テスト太郎", "フレンド：往復（表示名）")
check(_rt["claudeFiveHour"] == 99 and _rt["claudeWeekly"] == 0, "フレンド：Claude 残り枠")
check([w["label"] for w in _rt["codex"]] == ["5時間", "週間"], "フレンド：Codex の枠")
check(F.min_remaining(_rt) == 0, "フレンド：最も少ない残り")
check(F.valid_id(F.new_id()) and not F.valid_id("xyz") and not F.valid_id("ABC"*10), "フレンド：id は32桁hex")
check(F.parse_status({"format": "wrong", "id": _cfg["id"], "updatedAt": F.now_ms()}) is None, "フレンド：形式違いは拒否")
check(F.parse_status(jsonc.loads(jsonc.dumps({"format": F.FORMAT, "id": "short", "updatedAt": F.now_ms()}))) is None, "フレンド：不正idは拒否")
_folder = "/Users/x/OneDrive/共有"
check(F.decode_invite("どうぞ→ " + F.encode_invite(_folder) + " ←ここまで") == _folder, "フレンド：招待コードの往復")
check(F.decode_invite("パスフレーズ含まない普通の文") is None, "フレンド：招待コードでない文字列は None")
# 書き込み→読み出し（自分は除外）
F.publish(_cfg, _mine)
check(F.read_others(_cfg) == [], "フレンド：自分のファイルは一覧に出さない")
_other = dict(_cfg, id=F.new_id())
F.publish(_other, F.build_mine(_other, {"fiveHourRemaining": 50, "weeklyRemaining": 60}, None))
_read = F.read_others(_cfg)
check(len(_read) == 1 and _read[0]["id"] == _other["id"], "フレンド：他人のファイルは読める")
# 名前のサニタイズ（制御・改行を除く）
check(F.safe_line("a‮b\nc", 40) == "ab c", "フレンド：名前のサニタイズ")
import shutil as _sh; _sh.rmtree(_tmp, ignore_errors=True)

# リセット通知の判定（Windows と同じ ResetWatch のロジック）
from aiusagebar import resetwatch as R
check(R.should_notify(R.MODE_LOW_ONLY, 15, 20) and not R.should_notify(R.MODE_LOW_ONLY, 25, 20), "リセット：少ないときだけ")
check(R.should_notify(R.MODE_ALWAYS, 50, 20) and not R.should_notify(R.MODE_ALWAYS, 100, 20), "リセット：毎回（未使用枠は除く）")
check(not R.should_notify(R.MODE_OFF, 5, 20), "リセット：しない")
_w = R.ResetWatcher()
_now = F.now_ms()
# まだリセット前 → 通知なし。時刻を過ぎたら1回だけ通知
check(_w.check_codex({"windows": [{"minutes": 300, "usedPercent": 90, "resetsAt": _now+60000}]}, R.MODE_ALWAYS, 20, now=_now) == [], "リセット：リセット前は通知しない")
_n = _w.check_codex({"windows": []}, R.MODE_ALWAYS, 20, now=_now+120000)
check(len(_n) == 1 and "リセット" in _n[0]["title"], "リセット：時刻を過ぎたら通知")
check(_w.check_codex({"windows": []}, R.MODE_ALWAYS, 20, now=_now+130000) == [], "リセット：同じリセットは二度通知しない")
# Claude：残りが大きく戻ったら通知
_b = {"fiveHourRemaining": 5, "weeklyRemaining": 50, "observedAt": _now}
_a = {"fiveHourRemaining": 99, "weeklyRemaining": 50, "observedAt": _now+60000}
_cn = R.ResetWatcher().check_claude(_b, _a, R.MODE_ALWAYS, 20, now=_now+90000)
check(len(_cn) == 1 and "5時間" in _cn[0]["title"], "リセット：Claude が大きく戻ったら通知")

print("結果: %d passed, %d failed" % (ok, fail))
raise SystemExit(1 if fail else 0)
