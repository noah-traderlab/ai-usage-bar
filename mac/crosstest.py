# -*- coding: utf-8 -*-
"""
Windows 版（C#）と Mac 用ツール（Python）の相互検証。
1) C# がチームを作成（team.json を署名）
2) Python が team.json を検証し、招待コードで参加、報告を暗号化・署名して書き込み
3) C# マスターがその報告を復号・検証して読み、内容が一致するか確認
このスクリプトは Windows 上（このPC）で実行する。Mac の実機がなくても、暗号・署名・報告形式の一致を確認できる。
"""
import json
import os
import shutil
import subprocess
import sys
import tempfile
from datetime import datetime, timezone, timedelta

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from aiusagebar import team, logs, crypto  # noqa: E402

REPO = os.path.dirname(HERE)
TESTS_EXE = os.path.join(REPO, "build", "tests.exe")

ok = 0
fail = 0


def check(cond, name, detail=""):
    global ok, fail
    if cond:
        ok += 1
        print("  PASS  " + name)
    else:
        fail += 1
        print("  FAIL  " + name + ("  " + detail if detail else ""))


def run_cs(*args):
    out = subprocess.run([TESTS_EXE] + list(args), capture_output=True)
    text = out.stdout.decode("utf-8", "replace")
    if out.returncode != 0:
        print(text)
        print(out.stderr.decode("utf-8", "replace"))
        raise SystemExit("C# bridge failed: " + " ".join(args))
    return text


def iso(dt):
    return dt.strftime("%Y-%m-%dT%H:%M:%S.") + ("%03dZ" % (dt.microsecond // 1000))


def write_claude_log(home, sid, when, inp, outp, cr, cw, text="レポートを作成して"):
    d = os.path.join(home, ".claude", "projects", "proj")
    os.makedirs(d, exist_ok=True)
    user = {"parentUuid": None, "isSidechain": False, "type": "user",
            "message": {"role": "user", "content": text}, "uuid": "u1",
            "timestamp": iso(when), "origin": {"kind": "human"},
            "cwd": "/Users/friend/work", "sessionId": sid}
    asst = {"isSidechain": False, "message": {"model": "claude-opus-5", "id": "m1", "role": "assistant",
            "content": [{"type": "text", "text": "hi"}],
            "usage": {"input_tokens": inp, "cache_creation_input_tokens": cw,
                      "cache_read_input_tokens": cr, "output_tokens": outp}},
            "requestId": "r1", "type": "assistant", "uuid": "a1",
            "timestamp": iso(when + timedelta(seconds=5)), "sessionId": sid}
    sep = (",", ":")   # 実物の Claude ログと同じくコンパクトな JSONL
    with open(os.path.join(d, sid + ".jsonl"), "w", encoding="utf-8") as f:
        f.write(json.dumps(user, separators=sep) + "\n" + json.dumps(asst, separators=sep) + "\n")


def main():
    tmp = tempfile.mkdtemp(prefix="aiub-cross-")
    shared = os.path.join(tmp, "共有 フォルダ")
    os.makedirs(shared)
    master_data = os.path.join(tmp, "master-data")
    friend_home = os.path.join(tmp, "friend-home")
    friend_data = os.path.join(tmp, "friend-data")
    member_email = "member@example.com"
    master_email = "noah@example.com"

    print("== C#：チーム作成（team.json を署名）")
    out = run_cs("--mac-bridge", "create", shared, master_email, member_email, master_data)
    info = dict(line.split("=", 1) for line in out.strip().splitlines() if "=" in line)
    folder = info["FOLDER"]
    fp = info["FINGERPRINT"]
    invite = info["INVITE"]
    check(os.path.isfile(os.path.join(folder, "team.json")), "C# が team.json を作成")

    print("== Python：team.json を検証して参加")
    tconf, resolved = team.peek(shared)
    check(tconf.fingerprint == fp, "Python が C# の team.json の署名を検証し、確認コードが一致",
          tconf.fingerprint + " vs " + fp)
    dec = team.decode_invite("招待です\n" + invite + "\nよろしく")
    check(dec is not None and dec[2] == fp, "Python が招待コードを読み取れる")

    # 見本ログ（相手の Mac の記録に相当）
    now = datetime.now(timezone.utc)
    write_claude_log(friend_home, "s-1", now - timedelta(minutes=30), 1000, 2000, 30000, 4000, text="顧客A社の見積もりを作って")
    # AIが付けたタイトル
    d = os.path.join(friend_home, ".claude", "projects", "proj")
    with open(os.path.join(d, "s-1.jsonl"), "r+", encoding="utf-8") as f:
        body = f.read()
        f.seek(0)
        f.write(json.dumps({"type": "ai-title", "aiTitle": "見積もりの作成", "sessionId": "s-1"}, separators=(",", ":")) + "\n" + body)

    # Codex のログ（rollout）も1件
    cid = "01a0cccc-0000-7000-8000-000000000001"
    cdir = os.path.join(friend_home, ".codex", "sessions", now.strftime("%Y"), now.strftime("%m"), now.strftime("%d"))
    os.makedirs(cdir)
    ct = now - timedelta(hours=1)
    sep = (",", ":")
    lines = [
        json.dumps({"timestamp": iso(ct), "type": "session_meta", "payload": {"id": cid, "session_id": cid,
                    "timestamp": iso(ct), "cwd": "/Users/friend/proj", "originator": "codex_cli_rs", "thread_source": "user"}}, separators=sep),
        json.dumps({"timestamp": iso(ct + timedelta(seconds=1)), "type": "turn_context", "payload": {"model": "gpt-5.6-sol"}}, separators=sep),
        json.dumps({"timestamp": iso(ct + timedelta(seconds=2)), "type": "response_item", "payload": {"type": "message", "role": "user",
                    "content": [{"type": "input_text", "text": "ログイン画面のテストを追加して"}]}}, separators=sep),
        json.dumps({"timestamp": iso(ct + timedelta(seconds=3)), "type": "token_usage_record", "payload": {"thread_id": cid, "response_id": "resp1",
                    "usage": {"input_tokens": 120000, "cached_input_tokens": 100000, "cache_write_input_tokens": 0,
                              "output_tokens": 3000, "reasoning_output_tokens": 900, "total_tokens": 123000}}}, separators=sep),
    ]
    with open(os.path.join(cdir, "rollout-x-" + cid + ".jsonl"), "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")

    ll = logs.LocalLogs(home=friend_home)
    ll.scan()
    py_claude = sum(d.total for d in ll.history() if d.tool == "claude")
    py_codex = sum(d.total for d in ll.history() if d.tool == "codex")
    check(py_claude == 37000, "Python の Claude 集計（=37000）", str(py_claude))
    check(py_codex == 123000, "Python の Codex 集計（=123000）", str(py_codex))

    def detected():
        return [member_email]

    def quota():
        return {
            "codex": {"planType": "plus", "remaining": 0, "fiveHourRemaining": 0, "weeklyRemaining": 84,
                      "observedAt": int(now.timestamp() * 1000)},
            "claude": {"fiveHourRemaining": 99, "weeklyRemaining": 0, "observedAt": int(now.timestamp() * 1000)},
            "codexServiceDaily": [{"date": now.strftime("%Y-%m-%d"), "tokens": 15100000}],
        }

    svc = team.TeamService(friend_data, ll, detected, quota)
    svc.join(shared, fp, "知り合い", member_email, confirmed=tconf)
    check(svc.local.joined and svc.my_pc_fingerprint(), "Python が参加し、このPCのチーム確認コードを持つ")
    err = svc.upload(force=True)
    check(err is None, "Python が報告を暗号化・署名して書き込み", str(err))
    report_path = os.path.join(folder, "reports", svc.local.install_id + ".aiubr")
    raw = open(report_path, "r", encoding="utf-8").read()
    check(member_email not in raw and "レポート" not in raw and "37000" not in raw,
          "報告の中身は暗号化されている（メール・内容が読めない）")

    print("== C#：マスターが Python の報告を読む")
    out = run_cs("--mac-bridge", "read", master_data)
    reports = [json.loads(line[len("REPORT="):]) for line in out.strip().splitlines() if line.startswith("REPORT=")]
    r = next((x for x in reports if x["installId"] == svc.local.install_id), None)
    check(r is not None, "C# が Python の報告を1件として読む")
    if r:
        check(r["error"] is None, "エラーなく復号・検証できる", str(r["error"]))
        check(r["sigOk"] is True, "Python の署名を C# が検証できる（RSA-PSS 相互運用）")
        check(r["claudeTotal"] == 37000, "Claude Code のトークンが一致", str(r["claudeTotal"]))
        check(r["codexTotal"] == 123000, "Codex のローカル集計が一致", str(r["codexTotal"]))
        check(r["codexService"] == 15100000, "Codex サービス集計が一致", str(r["codexService"]))
        check(r["quotaCodex"] == 0 and r["quotaCodexWeek"] == 84 and r["quotaClaude"] == 99,
              "残り枠（Codex 0/84・Claude 99）が一致", json.dumps([r["quotaCodex"], r["quotaCodexWeek"], r["quotaClaude"]]))
        check(r["detected"] == member_email, "検出メールが一致")
        check(r["autoApproved"] is True, "登録メールでログイン中のため自動で集計される")
        check(r["fingerprint"] == svc.my_pc_fingerprint(), "PC チーム確認コードが Python と一致")
        check(r["scope"] == "prompts", "共有範囲は依頼文まで（すべて共有に同意）", str(r["scope"]))
        check(r["sessions"] == 2, "セッションが2件（Claude・Codex）として伝わる", str(r["sessions"]))
        check(r["prompts"] == 2 and "顧客A社の見積もりを作って" in r["promptTexts"] and "ログイン画面のテストを追加して" in r["promptTexts"],
              "依頼文の本文（Claude・Codex）が C# 側で復元できる", json.dumps(r["promptTexts"], ensure_ascii=False))

    # 改ざん検知
    print("== 改ざん検知")
    doc = json.loads(raw)
    ct = bytearray(crypto.unb64(doc["payload"]["ciphertext"]))
    ct[len(ct) // 2] ^= 0x55
    doc["payload"]["ciphertext"] = crypto.b64(bytes(ct))
    open(report_path, "w", encoding="utf-8").write(json.dumps(doc))
    out = run_cs("--mac-bridge", "read", master_data)
    reports = [json.loads(line[len("REPORT="):]) for line in out.strip().splitlines() if line.startswith("REPORT=")]
    r = next((x for x in reports if x["installId"] == svc.local.install_id), None)
    check(r is not None and r["error"] is not None, "報告を書き換えると C# 側で検証エラーになる")

    print("== フレンドの残り枠（C#とPythonの相互）")
    from aiusagebar import friends as F
    fshared = os.path.join(tmp, "friends-share")
    os.makedirs(fshared, exist_ok=True)
    # (a) Python が書いた friend ファイルを C# が読める
    pcfg = {"id": F.new_id(), "folder": fshared, "displayName": "マック太郎", "enabled": True}
    snap = {"windows": [{"minutes": 300, "usedPercent": 25, "resetsAt": F.now_ms() + 3600000},
                        {"minutes": 10080, "usedPercent": 5, "resetsAt": F.now_ms() + 600000000}]}
    F.publish(pcfg, F.build_mine(pcfg, {"fiveHourRemaining": 90, "weeklyRemaining": 30}, snap))
    out = run_cs("--mac-bridge", "friend-read", fshared, F.new_id())
    seen = [json.loads(l[len("FRIEND="):]) for l in out.strip().splitlines() if l.startswith("FRIEND=")]
    pf = next((x for x in seen if x["id"] == pcfg["id"]), None)
    check(pf is not None, "C# が Python のフレンドファイルを読める")
    check(bool(pf) and pf["name"] == "マック太郎", "C#←Python: 表示名が一致")
    check(bool(pf) and pf["claude5h"] == 90 and pf["claudeWeek"] == 30, "C#←Python: Claude 残り枠が一致")
    labels = [w["label"] for w in (pf["codex"] if pf else [])]
    check("5時間" in labels and "週間" in labels, "C#←Python: Codex の枠が一致")
    check(bool(pf) and pf["min"] == 30, "C#←Python: 最も少ない残りが一致")
    # (b) C# が書いた friend ファイルを Python が読める
    cid = "abcdef0123456789abcdef0123456789"
    run_cs("--mac-bridge", "friend-write", fshared, cid, "ウィンドウズ花子")
    others = F.read_others({"id": F.new_id(), "folder": fshared, "enabled": True})
    cf = next((x for x in others if x["id"] == cid), None)
    check(cf is not None and cf["name"] == "ウィンドウズ花子", "Python が C# のフレンドファイルを読める")
    check(bool(cf) and cf["claudeFiveHour"] == 88 and cf["claudeWeekly"] == 42, "Python←C#: Claude 残り枠が一致")
    # (c) 自分のファイルは一覧に出さない
    check(all(x["id"] != pcfg["id"] for x in F.read_others(pcfg)), "自分のフレンドファイルは一覧に出さない")

    shutil.rmtree(tmp, ignore_errors=True)
    print("結果: %d passed, %d failed" % (ok, fail))
    return 1 if fail else 0


if __name__ == "__main__":
    raise SystemExit(main())
